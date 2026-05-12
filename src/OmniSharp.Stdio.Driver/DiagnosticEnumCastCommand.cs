using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileSystemGlobbing;
using OmniSharp.FileSystem;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Services.Diagnostics;
using OmniSharp.Services;
using OmniSharp.Stdio.Eventing;

namespace OmniSharp.Stdio.Driver
{
    internal static class DiagnosticEnumCastCommand
    {
        public static void Register(McMaster.Extensions.CommandLineUtils.CommandLineApplication parent, StdioCommandLineApplication application)
        {
            parent.Command("enum-cast", cmd =>
            {
                cmd.Description = "List methods that contain enum cast expressions in C# files matching glob patterns.";
                cmd.HelpOption("-? | -h | --help");

                var globOpt = cmd.Option("-g|--glob <pattern>", "Include glob pattern for .cs files (repeatable, default: **/*.cs)", CommandOptionType.MultipleValue);

                cmd.OnExecute(() => Execute(application, globOpt.Values));
            });
        }

        private static int Execute(StdioCommandLineApplication application, IReadOnlyCollection<string> patterns)
        {
            try
            {
                var includePatterns = patterns != null && patterns.Count > 0
                    ? patterns.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                    : new[] { "**/*.cs" };

                var environment = application.CreateEnvironment();
                var configurationResult = new ConfigurationBuilder(environment).Build();

                var serviceProvider = CompositionHostBuilder.CreateDefaultServiceProvider(
                    environment,
                    configurationResult.Configuration,
                    new StdioEventEmitter(NullSharedTextWriter.Instance),
                    configureLogging: _ => { });

                var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
                var assemblyLoader = serviceProvider.GetRequiredService<IAssemblyLoader>();
                var options = serviceProvider.GetRequiredService<IOptionsMonitor<OmniSharpOptions>>();
                var plugins = application.CreatePluginAssemblies(options.CurrentValue, environment);

                var compositionHostBuilder = new CompositionHostBuilder(serviceProvider)
                    .WithOmniSharpAssemblies()
                    .WithAssemblies(assemblyLoader.LoadByAssemblyNameOrPath(loggerFactory.CreateLogger(typeof(Program)), plugins.AssemblyNames).ToArray());

                using var compositionHost = compositionHostBuilder.Build(environment.TargetDirectory);

                WorkspaceInitializer.Initialize(serviceProvider, compositionHost);

                var workspace = compositionHost.GetExport<OmniSharpWorkspace>();
                if (!WaitForWorkspaceInitialization(workspace, TimeSpan.FromSeconds(30)))
                {
                    Console.Error.WriteLine("Workspace initialization timed out.");
                    return 1;
                }

                var projectSystems = compositionHost.GetExports<IProjectSystem>();
                Task.WaitAll(projectSystems.Select(ps => ps.WaitForIdleAsync()).ToArray());

                var matcher = new Matcher();
                foreach (var pattern in includePatterns)
                {
                    matcher.AddInclude(pattern);
                }

                var results = FindEnumCastLocations(workspace, matcher, environment.TargetDirectory)
                    .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Line)
                    .ToList();

                foreach (var result in results)
                {
                    if (string.IsNullOrEmpty(result.Token))
                    {
                        Console.WriteLine($"{result.Path}:{result.Line}");
                        continue;
                    }

                    Console.WriteLine($"{result.Path}:{result.Line} token={result.Token}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        private static IEnumerable<ResultItem> FindEnumCastLocations(OmniSharpWorkspace workspace, Matcher matcher, string targetDirectory)
        {
            var allDocuments = workspace.CurrentSolution.Projects.SelectMany(x => x.Documents);

            foreach (var document in allDocuments)
            {
                if (string.IsNullOrWhiteSpace(document.FilePath))
                {
                    continue;
                }

                if (!document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = FileSystemHelper.GetRelativePath(document.FilePath, targetDirectory) ?? document.FilePath;
                if (!matcher.Match(relativePath).HasMatches)
                {
                    continue;
                }

                var syntaxRoot = document.GetSyntaxRootAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (syntaxRoot == null)
                {
                    continue;
                }

                var semanticModel = document.GetSemanticModelAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (semanticModel == null)
                {
                    continue;
                }

                var casts = syntaxRoot.DescendantNodes().OfType<CastExpressionSyntax>();
                foreach (var cast in casts)
                {
                    var targetType = semanticModel.GetTypeInfo(cast.Type).Type;
                    var sourceType = semanticModel.GetTypeInfo(cast.Expression).Type;
                    if (!IsEnumType(targetType) && !IsEnumType(sourceType))
                    {
                        continue;
                    }

                    var line = cast.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var method = cast.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    var token = method == null ? null : GetCpp2IlToken(method, semanticModel);
                    yield return new ResultItem(relativePath, line, token);
                }
            }
        }

        private static string GetCpp2IlToken(MethodDeclarationSyntax method, SemanticModel semanticModel)
        {
            foreach (var attributeList in method.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    if (!IsCpp2IlTokenAttribute(attribute, semanticModel))
                    {
                        continue;
                    }

                    if (attribute.ArgumentList == null)
                    {
                        return null;
                    }

                    foreach (var argument in attribute.ArgumentList.Arguments)
                    {
                        if (argument.NameEquals?.Name.Identifier.Text != "Token")
                        {
                            continue;
                        }

                        var tokenValue = semanticModel.GetConstantValue(argument.Expression);
                        if (!tokenValue.HasValue || tokenValue.Value == null)
                        {
                            return argument.Expression.ToString();
                        }

                        return tokenValue.Value.ToString();
                    }
                }
            }

            return null;
        }

        private static bool IsCpp2IlTokenAttribute(AttributeSyntax attribute, SemanticModel semanticModel)
        {
            var symbol = semanticModel.GetSymbolInfo(attribute).Symbol as IMethodSymbol;
            if (symbol?.ContainingType?.Name == "Cpp2IlTokenAttribute")
            {
                return true;
            }

            var name = attribute.Name.ToString();
            return string.Equals(name, "Cpp2IlToken", StringComparison.Ordinal) ||
                   string.Equals(name, "Cpp2IlTokenAttribute", StringComparison.Ordinal) ||
                   name.EndsWith(".Cpp2IlToken", StringComparison.Ordinal) ||
                   name.EndsWith(".Cpp2IlTokenAttribute", StringComparison.Ordinal);
        }

        private static bool IsEnumType(ITypeSymbol type)
        {
            if (type == null)
            {
                return false;
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                return true;
            }

            if (type is INamedTypeSymbol namedType &&
                namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                namedType.TypeArguments.Length == 1)
            {
                return namedType.TypeArguments[0].TypeKind == TypeKind.Enum;
            }

            return false;
        }

        private static bool WaitForWorkspaceInitialization(OmniSharpWorkspace workspace, TimeSpan timeout)
        {
            var startTime = DateTime.UtcNow;
            var pollInterval = TimeSpan.FromMilliseconds(100);

            while (!workspace.Initialized && (DateTime.UtcNow - startTime) < timeout)
            {
                Thread.Sleep(pollInterval);
            }

            return workspace.Initialized;
        }

        private readonly struct ResultItem
        {
            public ResultItem(string path, int line, string token)
            {
                Path = path;
                Line = line;
                Token = token;
            }

            public string Path { get; }

            public int Line { get; }

            public string Token { get; }
        }

        private class NullSharedTextWriter : ISharedTextWriter
        {
            public static readonly NullSharedTextWriter Instance = new NullSharedTextWriter();

            public void WriteLine(object value)
            {
            }
        }
    }
}
