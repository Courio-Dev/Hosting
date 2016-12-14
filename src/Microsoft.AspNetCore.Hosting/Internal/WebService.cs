﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting.Views;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.StackTrace.Sources;

namespace Microsoft.AspNetCore.Hosting.Internal
{
    public class WebService : IHostedService, IDisposable
    {
        private static readonly string DeprecatedServerUrlsKey = "server.urls";

        private readonly IServiceCollection _applicationServiceCollection;
        private IStartup _startup;
        private ApplicationLifetime _applicationLifetime;
        private HostedServiceExecutor _hostedServiceExecutor;

        private readonly IServiceProvider _hostingServiceProvider;
        private readonly WebHostOptions _options;
        private readonly IConfiguration _config;

        private IServiceProvider _applicationServices;
        private RequestDelegate _application;
        private ILogger<WebService> _logger;

        // Used for testing only
        internal WebHostOptions Options => _options;

        public WebService(
            IServiceCollection appServices,
            IServiceProvider hostingServiceProvider,
            WebHostOptions options,
            IConfiguration config)
        {
            if (appServices == null)
            {
                throw new ArgumentNullException(nameof(appServices));
            }

            if (hostingServiceProvider == null)
            {
                throw new ArgumentNullException(nameof(hostingServiceProvider));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _config = config;
            _options = options;
            _applicationServiceCollection = appServices;
            _hostingServiceProvider = hostingServiceProvider;
        }

        public IServer Server { get; set; }

        public void Stop()
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public void Initialize()
        {
            if (_application == null)
            {
                _application = BuildApplication();
            }
        }

        public void Start()
        {
            Initialize();

            var diagnosticSource = _applicationServices.GetRequiredService<DiagnosticSource>();
            var httpContextFactory = _applicationServices.GetRequiredService<IHttpContextFactory>();
            Server.Start(new HostingApplication(_application, _logger, diagnosticSource, httpContextFactory));
        }

        private void EnsureApplicationServices()
        {
            if (_applicationServices == null)
            {
                EnsureStartup();
                _applicationServices = _startup.ConfigureServices(_applicationServiceCollection);
            }
        }

        private void EnsureStartup()
        {
            if (_startup != null)
            {
                return;
            }

            _startup = _hostingServiceProvider.GetRequiredService<IStartup>();
        }

        private RequestDelegate BuildApplication()
        {
            try
            {
                EnsureApplicationServices();
                EnsureServer();

                var builderFactory = _applicationServices.GetRequiredService<IApplicationBuilderFactory>();
                var builder = builderFactory.CreateBuilder(Server.Features);
                builder.ApplicationServices = _applicationServices;

                var startupFilters = _applicationServices.GetService<IEnumerable<IStartupFilter>>();
                Action<IApplicationBuilder> configure = _startup.Configure;
                foreach (var filter in startupFilters.Reverse())
                {
                    configure = filter.Configure(configure);
                }

                configure(builder);

                return builder.Build();
            }
            catch (Exception ex) when (_options.CaptureStartupErrors)
            {
                // EnsureApplicationServices may have failed due to a missing or throwing Startup class.
                if (_applicationServices == null)
                {
                    _applicationServices = _applicationServiceCollection.BuildServiceProvider();
                }

                EnsureServer();

                // Write errors to standard out so they can be retrieved when not in development mode.
                Console.Out.WriteLine("Application startup exception: " + ex.ToString());
                var logger = _applicationServices.GetRequiredService<ILogger<WebService>>();
                logger.ApplicationError(ex);

                // Generate an HTML error page.
                var hostingEnv = _applicationServices.GetRequiredService<IHostingEnvironment>();
                var showDetailedErrors = hostingEnv.IsDevelopment() || _options.DetailedErrors;

                var model = new ErrorPageModel();
                var runtimeType = Microsoft.Extensions.Internal.RuntimeEnvironment.RuntimeType;
                model.RuntimeDisplayName = (runtimeType == "CoreCLR") ? ".NET Core" : runtimeType == "CLR" ? ".NET Framework" : "Mono";
#if NETSTANDARD1_3 || NETSTANDARD1_5
                var systemRuntimeAssembly = typeof(System.ComponentModel.DefaultValueAttribute).GetTypeInfo().Assembly;
                var assemblyVersion = new AssemblyName(systemRuntimeAssembly.FullName).Version.ToString();
                var clrVersion = assemblyVersion;
#else
                var clrVersion = Environment.Version.ToString();
#endif
                model.RuntimeArchitecture = RuntimeInformation.ProcessArchitecture.ToString();
                var currentAssembly = typeof(ErrorPage).GetTypeInfo().Assembly;
                model.CurrentAssemblyVesion = currentAssembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    .InformationalVersion;
                model.ClrVersion = clrVersion;
                model.OperatingSystemDescription = RuntimeInformation.OSDescription;

                if (showDetailedErrors)
                {
                    var exceptionDetailProvider = new ExceptionDetailsProvider(
                        hostingEnv.ContentRootFileProvider,
                        sourceCodeLineCount: 6);

                    model.ErrorDetails = exceptionDetailProvider.GetDetails(ex);
                }
                else
                {
                    model.ErrorDetails = new ExceptionDetails[0];
                }

                var errorPage = new ErrorPage(model);
                return context =>
                {
                    context.Response.StatusCode = 500;
                    context.Response.Headers["Cache-Control"] = "no-cache";
                    return errorPage.ExecuteAsync(context);
                };
            }
        }

        private void EnsureServer()
        {
            if (Server == null)
            {
                Server = _applicationServices.GetRequiredService<IServer>();

                var addresses = Server.Features?.Get<IServerAddressesFeature>()?.Addresses;
                if (addresses != null && !addresses.IsReadOnly && addresses.Count == 0)
                {
                    var urls = _config[WebHostDefaults.ServerUrlsKey] ?? _config[DeprecatedServerUrlsKey];
                    if (!string.IsNullOrEmpty(urls))
                    {
                        foreach (var value in urls.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            addresses.Add(value);
                        }
                    }

                    if (addresses.Count == 0)
                    {
                        // Provide a default address if there aren't any configured.
                        addresses.Add("http://localhost:5000");
                    }
                }
            }
        }
    }
}