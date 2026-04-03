using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using OmniSharp.Models.Diagnostics;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Services.Diagnostics;
using OmniSharp.Roslyn.CSharp.Workers.Diagnostics;
using OmniSharp.Services;
using OmniSharp.Stdio.Eventing;

namespace OmniSharp.Stdio.Driver
{
    internal static class DiagnosticExportCommand
    {
        public static void Register(McMaster.Extensions.CommandLineUtils.CommandLineApplication parent, StdioCommandLineApplication application)
        {
            parent.Command("export", cmd =>
            {
                cmd.Description = "Export compilation diagnostics to JSON or JSONL.";
                cmd.HelpOption("-? | -h | --help");
                var outputOpt = cmd.Option("-o|--output <path>", "Output file path (default: stdout)", CommandOptionType.SingleValue);
                var formatOpt = cmd.Option("-f|--format <format>", "Output format: json or jsonl (default: json)", CommandOptionType.SingleValue);
                var levelOpt = cmd.Option("-l|--level <level>", "Minimum severity: Hidden, Info, Warning, Error (default: Warning)", CommandOptionType.SingleValue);
                var timeoutOpt = cmd.Option("-t|--timeout <seconds>", "Timeout waiting for diagnostics (default: 10)", CommandOptionType.SingleValue);

                cmd.OnExecute(() =>
                {
                    var outputPath = outputOpt.Value();
                    var format = formatOpt.Value() ?? "json";
                    var level = levelOpt.Value() ?? "Warning";
                    var timeoutSeconds = int.TryParse(timeoutOpt.Value(), out var t) ? t : 10;

                    return Execute(application, outputPath, format, level, timeoutSeconds);
                });
            });
        }

        private static int Execute(StdioCommandLineApplication application, string outputPath, string format, string level, int timeoutSeconds)
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

                var compositionHostBuilder = new CompositionHostBuilder(serviceProvider)
                    .WithOmniSharpAssemblies()
                    .WithAssemblies(assemblyLoader.LoadByAssemblyNameOrPath(loggerFactory.CreateLogger(typeof(Program)), plugins.AssemblyNames).ToArray());

                var compositionHost = compositionHostBuilder.Build(environment.TargetDirectory);

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

                var allDiagnostics = diagnosticWorker.GetAllDiagnosticsAsync().Result;
                var diagnosticResults = ConvertToDiagnosticResults(allDiagnostics, minSeverity.Value).ToList();

                var message = new DiagnosticMessage
                {
                    Results = diagnosticResults
                };

                string json;
                if (format.Equals("jsonl", StringComparison.OrdinalIgnoreCase))
                {
                    var sb = new StringBuilder();
                    foreach (var result in diagnosticResults)
                    {
                        sb.AppendLine(JsonConvert.SerializeObject(result));
                    }
                    json = sb.ToString().TrimEnd();
                }
                else
                {
                    json = JsonConvert.SerializeObject(message, Formatting.Indented);
                }

                if (string.IsNullOrEmpty(outputPath))
                {
                    Console.Write(json);
                }
                else
                {
                    File.WriteAllText(outputPath, json, Encoding.UTF8);
                }

                compositionHost.Dispose();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
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

        private static DiagnosticSeverity? ParseMinSeverity(string severity)
        {
            if (Enum.TryParse<DiagnosticSeverity>(severity, ignoreCase: true, out var result))
            {
                return result;
            }
            return null;
        }

        private static IEnumerable<DiagnosticResult> ConvertToDiagnosticResults(
            ImmutableArray<DocumentDiagnostics> documentDiagnostics,
            DiagnosticSeverity minSeverity)
        {
            return documentDiagnostics
                .GroupBy(d => d.DocumentPath)
                .Select(group => new DiagnosticResult
                {
                    FileName = group.Key,
                    QuickFixes = group
                        .SelectMany(doc => doc.Diagnostics)
                        .Where(d => d.Severity >= minSeverity)
                        .Select(ConvertToDiagnosticLocation)
                        .ToList()
                })
                .Where(result => result.QuickFixes.Any());
        }

        private static DiagnosticLocation ConvertToDiagnosticLocation(Diagnostic diagnostic)
        {
            var span = diagnostic.Location.GetMappedLineSpan();
            return new DiagnosticLocation
            {
                FileName = span.Path,
                Line = span.StartLinePosition.Line,
                Column = span.StartLinePosition.Character,
                EndLine = span.EndLinePosition.Line,
                EndColumn = span.EndLinePosition.Character,
                Text = diagnostic.GetMessage(),
                LogLevel = diagnostic.Severity.ToString(),
                Id = diagnostic.Id
            };
        }

        private class NullSharedTextWriter : ISharedTextWriter
        {
            public static readonly NullSharedTextWriter Instance = new NullSharedTextWriter();
            public void WriteLine(object value) { }
        }
    }
}
