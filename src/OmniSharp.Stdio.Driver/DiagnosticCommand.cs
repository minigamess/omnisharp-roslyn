using McMaster.Extensions.CommandLineUtils;
using OmniSharp.Stdio;

namespace OmniSharp.Stdio.Driver
{
    internal static class DiagnosticCommand
    {
        public static void Register(McMaster.Extensions.CommandLineUtils.CommandLineApplication parent, StdioCommandLineApplication application)
        {
            parent.Command("diagnostic", cmd =>
            {
                cmd.Description = "Diagnostic commands for OmniSharp.";

                DiagnosticExportCommand.Register(cmd, application);

                cmd.OnExecute(() =>
                {
                    cmd.ShowHelp();
                    return 0;
                });
            });
        }
    }
}
