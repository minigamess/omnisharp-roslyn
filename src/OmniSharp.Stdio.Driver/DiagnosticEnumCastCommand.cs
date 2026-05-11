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
                    Console.WriteLine($"{result.Path}:{result.Line}");
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
                    var castType = semanticModel.GetTypeInfo(cast.Type).Type;
                    if (!IsEnumType(castType))
                    {
                        continue;
                    }

                    var line = cast.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    yield return new ResultItem(relativePath, line);
                }
            }
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
            public ResultItem(string path, int line)
            {
                Path = path;
                Line = line;
            }

            public string Path { get; }

            public int Line { get; }
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
