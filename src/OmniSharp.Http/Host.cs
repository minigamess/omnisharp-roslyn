using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
#if NETCOREAPP
using Microsoft.Extensions.Hosting;
#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Eventing;
using OmniSharp.Plugins;
using OmniSharp.Services;
using OmniSharp.Utilities;

namespace OmniSharp.Http
{
    internal class Host
    {
        private readonly IOmniSharpEnvironment _environment;
        private readonly ISharedTextWriter _sharedTextWriter;
        private readonly PluginAssemblies _commandLinePlugins;
        private readonly int _serverPort;
        private readonly string _serverInterface;
        private Process _hostProcess;

        public Host(
            IOmniSharpEnvironment environment,
            ISharedTextWriter sharedTextWriter,
            PluginAssemblies commandLinePlugins,
            int serverPort,
            string serverInterface)
        {
            _environment = environment;
            _sharedTextWriter = sharedTextWriter;
            _commandLinePlugins = commandLinePlugins;
            _serverPort = serverPort;
            _serverInterface = serverInterface;
        }

        public void Start()
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddCommandLine(new[] { "--server.urls", $"http://{_serverInterface}:{_serverPort}" });

            var builder = new WebHostBuilder()
#if NETCOREAPP
                .UseKestrel(config => {
                    config.AllowSynchronousIO = true;
                })
#else
                .UseKestrel()
#endif
                .ConfigureServices(serviceCollection =>
                {
                    serviceCollection.AddSingleton(_environment);
                    serviceCollection.AddSingleton(_sharedTextWriter);
                    serviceCollection.AddSingleton(NullEventEmitter.Instance);
                    serviceCollection.AddSingleton(_commandLinePlugins);
                    serviceCollection.AddSingleton(new HttpEnvironment { Port = _serverPort });
                })
                .UseUrls($"http://{_serverInterface}:{_serverPort}")
                .UseConfiguration(config.Build())
                .UseEnvironment("OmniSharp")
                .UseStartup(typeof(Startup));

            using (var app = builder.Build())
            {
                app.Start();

#if NETCOREAPP
                var appLifeTime = app.Services.GetRequiredService<IHostApplicationLifetime>();
#else
                var appLifeTime = app.Services.GetRequiredService<IApplicationLifetime>();
#endif

                Console.CancelKeyPress += (sender, e) =>
                {
                    appLifeTime.StopApplication();
                    e.Cancel = true;
                };

                if (_environment.HostProcessId != -1)
                {
                    try
                    {
                        _hostProcess = Process.GetProcessById(_environment.HostProcessId);
                        _hostProcess.EnableRaisingEvents = true;
                        _hostProcess.OnExit(() => appLifeTime.StopApplication());
                    }
                    catch
                    {
                        appLifeTime.StopApplication();
                    }
                }

                appLifeTime.ApplicationStopping.WaitHandle.WaitOne();
            }
        }
    }
}
