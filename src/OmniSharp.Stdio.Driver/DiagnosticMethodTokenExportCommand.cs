using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Services.Diagnostics;
using OmniSharp.Roslyn.CSharp.Workers.Diagnostics;
using OmniSharp.Services;
using OmniSharp.Stdio.Eventing;

namespace OmniSharp.Stdio.Driver
{
    internal static class DiagnosticMethodTokenExportCommand
    {
        public static void Register(McMaster.Extensions.CommandLineUtils.CommandLineApplication parent, StdioCommandLineApplication application)
        {
            parent.Command("token-export", cmd =>
            {
                cmd.Description = "Export unique Cpp2IlToken values from methods containing diagnostics.";
                cmd.HelpOption("-? | -h | --help");

                var levelOpt = cmd.Option("-l|--level <level>", "Minimum severity: Hidden, Info, Warning, Error (default: Error)", CommandOptionType.SingleValue);
                var timeoutOpt = cmd.Option("-t|--timeout <seconds>", "Timeout waiting for diagnostics (default: 10)", CommandOptionType.SingleValue);
                var groupByFilepathOpt = cmd.Option("-g|--group-by-filepath", "Output one line per filepath", CommandOptionType.NoValue);

                cmd.OnExecute(() =>
                {
                    var level = levelOpt.Value() ?? "Error";
                    var timeoutSeconds = int.TryParse(timeoutOpt.Value(), out var t) ? t : 10;

                    return Execute(application, level, timeoutSeconds, groupByFilepathOpt.HasValue());
                });
            });
        }

        private static int Execute(StdioCommandLineApplication application, string level, int timeoutSeconds, bool groupByFilepath)
        {
            try
            {
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

                using var compositionHost = new CompositionHostBuilder(serviceProvider)
                    .WithOmniSharpAssemblies()
                    .WithAssemblies(assemblyLoader.LoadByAssemblyNameOrPath(loggerFactory.CreateLogger(typeof(Program)), plugins.AssemblyNames).ToArray())
                    .Build(environment.TargetDirectory);

                WorkspaceInitializer.Initialize(serviceProvider, compositionHost);

                var workspace = compositionHost.GetExport<OmniSharpWorkspace>();
                if (!WaitForWorkspaceInitialization(workspace, TimeSpan.FromSeconds(30)))
                {
                    Console.Error.WriteLine("Workspace initialization timed out.");
                    return 1;
                }

                var projectSystems = compositionHost.GetExports<IProjectSystem>();
                Task.WaitAll(projectSystems.Select(ps => ps.WaitForIdleAsync()).ToArray());

                var diagnosticWorker = compositionHost.GetExport<ICsDiagnosticWorker>();
                if (diagnosticWorker == null)
                {
                    Console.Error.WriteLine("Failed to get ICsDiagnosticWorker.");
                    return 1;
                }

                var minSeverity = ParseMinSeverity(level);
                if (minSeverity == null)
                {
                    Console.Error.WriteLine($"Unknown severity '{level}'. Valid values: Hidden, Info, Warning, Error.");
                    return 1;
                }

                diagnosticWorker.QueueDocumentsForDiagnostics();

                if (!WaitForDiagnostics(timeoutSeconds * 1000))
                {
                    Console.Error.WriteLine($"Warning: Timeout waiting for diagnostics after {timeoutSeconds} seconds.");
                }

                var rootPath = environment.TargetDirectory;
                var allDiagnostics = diagnosticWorker.GetAllDiagnosticsAsync().Result;
                var entries = CollectTokens(workspace, allDiagnostics, minSeverity.Value, rootPath)
                    .OrderBy(x => x.FilePath, StringComparer.Ordinal)
                    .ThenBy(x => x.Token, StringComparer.Ordinal)
                    .ThenBy(x => x.DiagnosticMessage, StringComparer.Ordinal)
                    .ToList();

                if (groupByFilepath)
                {
                    foreach (var group in entries.GroupBy(x => x.FilePath, StringComparer.Ordinal))
                    {
                        var tokenDiagnostics = group
                            .GroupBy(x => x.Token, StringComparer.Ordinal)
                            .OrderBy(x => x.Key, StringComparer.Ordinal)
                            .Select(tokenGroup =>
                            {
                                var normalizedMessages = tokenGroup
                                    .Select(x => NormalizeSingleLine(x.DiagnosticMessage))
                                    .Where(x => !string.IsNullOrWhiteSpace(x))
                                    .Distinct(StringComparer.Ordinal)
                                    .OrderBy(x => x, StringComparer.Ordinal)
                                    .ToList();

                                var token = string.IsNullOrWhiteSpace(tokenGroup.Key) ? "<no-token>" : tokenGroup.Key;
                                return $"{token}:{BuildMessageSummary(normalizedMessages)}";
                            })
                            .ToList();

                        Console.WriteLine($"{group.Key}\tdiagnostics={group.Count()}\ttokens={tokenDiagnostics.Count}\t{string.Join(" | ", tokenDiagnostics)}");
                    }
                }
                else
                {
                    foreach (var tokenGroup in entries
                        .GroupBy(x => new { x.FilePath, x.Token })
                        .OrderBy(x => x.Key.FilePath, StringComparer.Ordinal)
                        .ThenBy(x => x.Key.Token, StringComparer.Ordinal))
                    {
                        var messages = tokenGroup
                            .Select(x => NormalizeSingleLine(x.DiagnosticMessage))
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(x => x, StringComparer.Ordinal)
                            .ToList();

                        var token = string.IsNullOrWhiteSpace(tokenGroup.Key.Token) ? "<no-token>" : tokenGroup.Key.Token;
                        Console.WriteLine($"{tokenGroup.Key.FilePath}\tdiagnostics={tokenGroup.Count()}\ttokens=1\t{token}:{BuildMessageSummary(messages)}");
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        private static IEnumerable<TokenEntry> CollectTokens(
            OmniSharpWorkspace workspace,
            ImmutableArray<DocumentDiagnostics> documentDiagnostics,
            DiagnosticSeverity minSeverity,
            string rootPath)
        {
            var entries = new HashSet<TokenEntry>();
            var documentsById = workspace.CurrentSolution.Projects
                .SelectMany(project => project.Documents)
                .ToDictionary(document => document.Id, document => document);

            foreach (var documentDiagnostic in documentDiagnostics)
            {
                if (!documentsById.TryGetValue(documentDiagnostic.DocumentId, out var document))
                {
                    continue;
                }

                var filePath = GetRelativePath(document.FilePath, rootPath);

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

                foreach (var diagnostic in documentDiagnostic.Diagnostics)
                {
                    if (diagnostic.Severity < minSeverity || !diagnostic.Location.IsInSource)
                    {
                        continue;
                    }

                    var node = syntaxRoot.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                    foreach (var token in GetTokensForDiagnostic(node, semanticModel))
                    {
                        entries.Add(new TokenEntry(filePath, token.Trim(), diagnostic.GetMessage()));
                    }
                }
            }

            return entries;
        }

        private static IEnumerable<string> GetTokensForDiagnostic(SyntaxNode node, SemanticModel semanticModel)
        {
            if (node == null)
            {
                return Enumerable.Empty<string>();
            }

            var method = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (method != null)
            {
                return SingleToken(GetCpp2IlToken(method, semanticModel));
            }

            var constructor = node.AncestorsAndSelf().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
            if (constructor != null)
            {
                return SingleToken(GetCpp2IlToken(constructor, semanticModel));
            }

            var accessor = node.AncestorsAndSelf().OfType<AccessorDeclarationSyntax>().FirstOrDefault();
            if (accessor != null)
            {
                return SingleToken(GetCpp2IlToken(accessor.AttributeLists, semanticModel));
            }

            var indexer = node.AncestorsAndSelf().OfType<IndexerDeclarationSyntax>().FirstOrDefault();
            if (indexer != null)
            {
                return SingleToken(GetCpp2IlToken(indexer.AttributeLists, semanticModel));
            }

            return Enumerable.Empty<string>();
        }

        private static IEnumerable<string> SingleToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Enumerable.Empty<string>();
            }

            return new[] { token };
        }

        private static string GetCpp2IlToken(BaseMethodDeclarationSyntax method, SemanticModel semanticModel)
        {
            return GetCpp2IlToken(method.AttributeLists, semanticModel);
        }

        private static string GetCpp2IlToken(SyntaxList<AttributeListSyntax> attributeLists, SemanticModel semanticModel)
        {
            foreach (var attributeList in attributeLists)
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

        private static DiagnosticSeverity? ParseMinSeverity(string severity)
        {
            if (Enum.TryParse<DiagnosticSeverity>(severity, ignoreCase: true, out var result))
            {
                return result;
            }

            return null;
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

        private static bool WaitForDiagnostics(int timeoutMs)
        {
            Thread.Sleep(timeoutMs);
            return true;
        }

        private static string NormalizeSingleLine(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                builder.Append(ch == '\r' || ch == '\n' || ch == '\t' ? ' ' : ch);
            }

            return builder.ToString().Trim();
        }

        private static string BuildMessageSummary(IReadOnlyList<string> messages)
        {
            if (messages.Count == 0)
            {
                return string.Empty;
            }

            const int previewCount = 3;
            if (messages.Count <= previewCount)
            {
                return string.Join(" ; ", messages);
            }

            return string.Join(" ; ", messages.Take(previewCount)) + $" ; ...(+{messages.Count - previewCount})";
        }

        private static string GetRelativePath(string fullPath, string rootPath)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(rootPath))
            {
                return fullPath;
            }

            var fullUri = new Uri(fullPath);
            var rootUri = new Uri(rootPath.EndsWith("/") || rootPath.EndsWith("\\") ? rootPath : rootPath + "/");
            var relativeUri = rootUri.MakeRelativeUri(fullUri);
            return Uri.UnescapeDataString(relativeUri.ToString());
        }

        private class NullSharedTextWriter : ISharedTextWriter
        {
            public static readonly NullSharedTextWriter Instance = new NullSharedTextWriter();

            public void WriteLine(object value)
            {
            }
        }

        private readonly struct TokenEntry : IEquatable<TokenEntry>
        {
            public string FilePath { get; }
            public string Token { get; }
            public string DiagnosticMessage { get; }

            public TokenEntry(string filePath, string token, string diagnosticMessage)
            {
                FilePath = filePath;
                Token = token;
                DiagnosticMessage = diagnosticMessage;
            }

            public bool Equals(TokenEntry other) => FilePath == other.FilePath && Token == other.Token && DiagnosticMessage == other.DiagnosticMessage;

            public override bool Equals(object obj) => obj is TokenEntry other && Equals(other);

            public override int GetHashCode() => (FilePath, Token, DiagnosticMessage).GetHashCode();
        }
    }
}
