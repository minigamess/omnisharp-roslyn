using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace OmniSharp.Utilities
{
    public static class ProcessExtensions
    {
        private static readonly object s_gate = new object();
        private static Thread s_backgroundWatcher;
        private static List<WatchedProcess> s_watchedProcesses;

        private sealed class WatchedProcess
        {
            public Process Process { get; }
            public Action Action { get; }
            public int Triggered;

            public WatchedProcess(Process process, Action action)
            {
                Process = process;
                Action = action;
            }
        }

        public static void OnExit(this Process process, Action action)
        {
            if (process == null)
            {
                throw new ArgumentNullException(nameof(process));
            }

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            var watchedProcess = new WatchedProcess(process, action);

            lock (s_gate)
            {
                if (s_watchedProcesses == null)
                {
                    s_watchedProcesses = new List<WatchedProcess>();
                }

                s_watchedProcesses.Add(watchedProcess);

                if (s_backgroundWatcher == null)
                {
                    s_backgroundWatcher = new Thread(Watcher) { IsBackground = true };
                    s_backgroundWatcher.Start();
                }
            }

            if (!PlatformHelper.IsMono)
            {
                TryAttachExitEvent(watchedProcess);
            }

            if (HasProcessExited(process))
            {
                Trigger(watchedProcess);
            }
        }

        private static bool HasProcessExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        private static void Trigger(WatchedProcess watchedProcess)
        {
            if (Interlocked.Exchange(ref watchedProcess.Triggered, 1) != 0)
            {
                return;
            }

            lock (s_gate)
            {
                s_watchedProcesses?.Remove(watchedProcess);
            }

            try
            {
                watchedProcess.Action();
            }
            catch
            {
            }
        }

        private static void TryAttachExitEvent(WatchedProcess watchedProcess)
        {
            try
            {
                watchedProcess.Process.EnableRaisingEvents = true;
                watchedProcess.Process.Exited += (sender, e) => Trigger(watchedProcess);
            }
            catch
            {
                // Fall back to background polling in Watcher/CleanUpProcesses.
            }
        }

        private static void CleanUpProcesses()
        {
            WatchedProcess[] processes;

            lock (s_gate)
            {
                if (s_watchedProcesses == null || s_watchedProcesses.Count == 0)
                {
                    return;
                }

                processes = s_watchedProcesses.ToArray();
            }

            foreach (var watchedProcess in processes)
            {
                if (HasProcessExited(watchedProcess.Process))
                {
                    Trigger(watchedProcess);
                }
            }
        }

        private static void Watcher()
        {
            while (true)
            {
                CleanUpProcesses();

                // REVIEW: Configurable?
                Thread.Sleep(2000);
            }
        }

        public static void KillChildrenAndThis(this Process process)
        {
            if (PlatformHelper.IsMono)
            {
                foreach (var childProcess in GetChildProcesses(process.Id))
                {
                    childProcess.Kill();
                }
            }

            process.Kill();
        }

        private static IEnumerable<Process> GetChildProcesses(int processId)
        {
            foreach (var entry in GetAllProcessIds())
            {
                if (entry.parentId == processId)
                {
                    yield return Process.GetProcessById(entry.id);
                }
            }
        }

        private static IEnumerable<(int id, int parentId)> GetAllProcessIds()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ps",
                Arguments = string.Format("-o \"ppid, pid\" -ax"),
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var entries = new List<(int processId, int parentProcessId)>();

            var ps = Process.Start(startInfo);
            ps.BeginOutputReadLine();
            ps.OutputDataReceived += (sender, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                {
                    return;
                }

                var parts = e.Data.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (Int32.TryParse(parts[0], out var ppid) &&
                    Int32.TryParse(parts[1], out var pid))
                {
                    entries.Add((pid, ppid));
                }
            };

            ps.WaitForExit();

            return entries;
        }
    }
}
