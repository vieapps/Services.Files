﻿#region Related components
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using WampSharp.V2.Realm;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class Startup
	{
		public static void Main(string[] args)
			=> WebHost.CreateDefaultBuilder(args).Run<Startup>(args);

		public Startup(IConfiguration configuration)
			=> this.Configuration = configuration;

		public IConfiguration Configuration { get; }

		public LogLevel LogLevel => this.Configuration.GetAppSetting("Logging/LogLevel/Default", UtilityService.GetAppSetting("Logs:Level", "Information")).TryToEnum(out LogLevel logLevel) ? logLevel : LogLevel.Information;

		public void ConfigureServices(IServiceCollection services)
		{
			// mandatory services
			services
				.AddHttpContextAccessor()
				.AddResponseCompression(options => options.EnableForHttps = true)
				.AddLogging(builder => builder.SetMinimumLevel(this.LogLevel))
				.AddCache(options => this.Configuration.GetSection("Cache").Bind(options))
				.AddSession(options => Global.PrepareSessionOptions(options, 45))
				.Configure<FormOptions>(options => Global.PrepareFormOptions(options))
				.Configure<CookiePolicyOptions>(options => Global.PrepareCookiePolicyOptions(options));

			// authentication
			services
				.AddAuthentication(options => Global.PrepareAuthenticationOptions(options, _ => options.RequireAuthenticatedSignIn = false))
				.AddCookie(options => Global.PrepareCookieAuthenticationOptions(options, 45));

			// data protection (encrypt/decrypt authenticate ticket cookies & sync across load balancers)
			services.AddDataProtection().PrepareDataProtection("VIEApps-NGX-Files");

			// config authentication with proxy/load balancer
			if (Global.UseIISIntegration)
				services.Configure<IISOptions>(options => options.ForwardClientCertificate = false);

			else
			{
				var certificateHeader = "true".IsEquals(UtilityService.GetAppSetting("Proxy:UseAzure"))
					? "X-ARR-ClientCert"
					: UtilityService.GetAppSetting("Proxy:X-Forwarded-Certificate");
				if (!string.IsNullOrWhiteSpace(certificateHeader))
					services.AddCertificateForwarding(options => options.CertificateHeader = certificateHeader);
			}

			// config options of IIS server (for working with InProcess hosting model)
			if (Global.UseIISInProcess)
				services.Configure<IISServerOptions>(options => Global.PrepareIISServerOptions(options, _ => options.MaxRequestBodySize = 1024 * 1024 * Global.MaxRequestBodySize));
		}

		public void Configure(IApplicationBuilder appBuilder, IHostApplicationLifetime appLifetime, IWebHostEnvironment environment)
		{
			// environments
			var stopwatch = Stopwatch.StartNew();
			Console.OutputEncoding = Encoding.UTF8;
			Global.ServiceName = "Files";

			var loggerFactory = appBuilder.ApplicationServices.GetService<ILoggerFactory>();
			var logPath = UtilityService.GetAppSetting("Path:Logs");
			if ("true".IsEquals(UtilityService.GetAppSetting("Logs:WriteFiles", "true")) && !string.IsNullOrWhiteSpace(logPath) && Directory.Exists(logPath))
			{
				logPath = Path.Combine(logPath, "{Hour}" + $"_{Global.ServiceName.ToLower()}.http.pid-{Environment.ProcessId}.txt");
				loggerFactory.AddFile(logPath, this.LogLevel);
			}
			else
				logPath = null;

			// setup the service
			Logger.AssignLoggerFactory(loggerFactory);
			Global.Logger = loggerFactory.CreateLogger<Startup>();

			Global.ServiceProvider = appBuilder.ApplicationServices;
			Global.RootPath = environment.ContentRootPath;

			Global.Logger.LogInformation($"The {Global.ServiceName} HTTP service is starting");
			Global.Logger.LogInformation($"Version: {typeof(Startup).Assembly.GetVersion()}");
#if DEBUG
			Global.Logger.LogInformation($"Working mode: DEBUG ({(environment.IsDevelopment() ? "Development" : "Production")})");
#else
			Global.Logger.LogInformation($"Working mode: RELEASE ({(environment.IsDevelopment() ? "Development" : "Production")})");
#endif
			Global.Logger.LogInformation($"Environment:\r\n\t{Extensions.GetRuntimeEnvironment()}");
			Global.Logger.LogInformation($"Service URIs:\r\n\t- Round robin: services.{Global.ServiceName.ToLower()}.http\r\n\t- Single (unique): services.{Handler.NodeName}");

			Global.CreateRSA();
			Handler.PrepareHandlers();

			JsonConvert.DefaultSettings = () => new JsonSerializerSettings
			{
				Formatting = Formatting.None,
				ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
				DateTimeZoneHandling = DateTimeZoneHandling.Local
			};

			// prepare outgoing proxy
			var proxy = UtilityService.GetAppSetting("Proxy:Host");
			if (!string.IsNullOrWhiteSpace(proxy))
				try
				{
					UtilityService.AssignWebProxy(proxy, UtilityService.GetAppSetting("Proxy:Port").CastAs<int>(), UtilityService.GetAppSetting("Proxy:User"), UtilityService.GetAppSetting("Proxy:UserPassword"), UtilityService.GetAppSetting("Proxy:Bypass")?.ToArray(";"));
				}
				catch (Exception ex)
				{
					Global.Logger.LogError($"Error occurred while assigning web-proxy => {ex.Message}", ex);
				}

			// setup middlewares
			appBuilder
				.UseForwardedHeaders(Global.GetForwardedHeadersOptions())
				.UseStatusCodeHandler()
				.UseResponseCompression()
				.UseCache()
				.UseSession()
				.UseCertificateForwarding()
				.UseCookiePolicy()
				.UseAuthentication();

			// setup the path mappers
			var onIncomingConnectionEstablished = new List<Action<object, WampSessionCreatedEventArgs>>();
			var onOutgoingConnectionEstablished = new List<Action<object, WampSessionCreatedEventArgs>>();
			if (System.Configuration.ConfigurationManager.GetSection(UtilityService.GetAppSetting("Section:Maps", "net.vieapps.services.files.http.maps")) is AppConfigurationSectionHandler config && config.Section.SelectNodes("map") is System.Xml.XmlNodeList maps)
				maps.ToList()
					.Select(info => new Tuple<string, string>(info.Attributes["path"]?.Value?.ToLower()?.Trim(), info.Attributes["type"]?.Value))
					.Where(info => !string.IsNullOrEmpty(info.Item1) && !string.IsNullOrEmpty(info.Item2))
					.Select(info =>
					{
						var path = info.Item1;
						while (path.StartsWith("/"))
							path = path.Right(path.Length - 1);
						while (path.EndsWith("/"))
							path = path.Left(path.Length - 1);
						return new Tuple<string, string>(path, info.Item2);
					})
					.Where(info => !Handler.Handlers.ContainsKey(info.Item1))
					.ForEach(info =>
					{
						try
						{
							if (AssemblyLoader.GetType(info.Item2)?.CreateInstance() is PathMapper mapper)
							{
								appBuilder.Map($"/{info.Item1}", builder => mapper.Map(builder, appLifetime, onIncomingConnectionEstablished, onOutgoingConnectionEstablished));
								Global.Logger.LogInformation($"Successfully branch the request to a specified path: /{info.Item1} => {mapper.GetTypeName()}");
							}
						}
						catch (Exception ex)
						{
							Global.Logger.LogError($"Cannot load a path mapper ({info.Item2}) => {ex.Message}", ex);
						}
					});

			// setup the handler for all requests
			appBuilder.UseMiddleware<Handler>();

			// connect to API Gateway Router
			Handler.Connect(onIncomingConnectionEstablished, onOutgoingConnectionEstablished);

			// on started
			appLifetime.ApplicationStarted.Register(() =>
			{
				Global.Logger.LogInformation($"API Gateway Router: {new Uri(Router.GetRouterStrInfo()).GetResolvedURI()}");
				Global.Logger.LogInformation($"API Gateway HTTP service: {UtilityService.GetAppSetting("HttpUri:APIs", "None")}");
				Global.Logger.LogInformation($"Files HTTP service: {UtilityService.GetAppSetting("HttpUri:Files", "None")}");
				Global.Logger.LogInformation($"Portals HTTP service: {UtilityService.GetAppSetting("HttpUri:Portals", "None")}");
				Global.Logger.LogInformation($"Root (base) directory: {Global.RootPath}");
				Global.Logger.LogInformation($"Status files directory: {UtilityService.GetAppSetting("Path:Status", "None")}");
				Global.Logger.LogInformation($"Static files directory: {UtilityService.GetAppSetting("Path:Statics", "None")}");
				Global.Logger.LogInformation($"Static segments: {Global.StaticSegments.ToString(", ")}");
				Global.Logger.LogInformation($"Temporary directory: {Handler.TempFilesPath}");
				Global.Logger.LogInformation($"Attachments directory: {Handler.AttachmentFilesPath}");
				Global.Logger.LogInformation($"User avatars directory: {Handler.UserAvatarFilesPath}");
				Global.Logger.LogInformation($"Default user avatar: {Handler.DefaultUserAvatarFilePath}");
				Global.Logger.LogInformation($"Logging level: {this.LogLevel} - Local rolling log files is {(string.IsNullOrWhiteSpace(logPath) ? "disabled" : $"enabled => {logPath}")}");
				Global.Logger.LogInformation($"Show debugs: {Global.IsDebugLogEnabled} - Show results: {Global.IsDebugResultsEnabled} - Show stacks: {Global.IsDebugStacksEnabled}");
				Global.Logger.LogInformation($"Request limits => Files (multipart/form-data): {Global.MaxRequestBodySize:###,###,##0} MB - Avatars: {UtilityService.GetAppSetting("Limits:Avatar", "1024")} KB - Thumbnails: {UtilityService.GetAppSetting("Limits:Thumbnail", "512")} KB");
				
				stopwatch.Stop();
				Global.Logger.LogInformation($"The {Global.ServiceName} HTTP service is started - PID: {Environment.ProcessId} - Execution times: {stopwatch.GetElapsedTimes()}");
				Global.Logger = loggerFactory.CreateLogger<Handler>();
			});

			// on stopping
			appLifetime.ApplicationStopping.Register(() =>
			{
				Global.Logger = loggerFactory.CreateLogger<Startup>();
				Global.RSA.Dispose();
			});

			// on stopped
			appLifetime.ApplicationStopped.Register(() =>
			{
				Handler.Disconnect();
				Global.CancellationTokenSource.Cancel();
				Global.CancellationTokenSource.Dispose();
				Global.Logger.LogInformation($"The {Global.ServiceName} HTTP service was stopped");
			});

			// don't terminate the process immediately, wait for the Main thread to exit gracefully
			Console.CancelKeyPress += (sender, args) =>
			{
				appLifetime.StopApplication();
				args.Cancel = true;
			};
		}
	}
}