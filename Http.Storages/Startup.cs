#region Related components
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Hosting;
#if !NETCOREAPP2_1
using Microsoft.Extensions.Hosting;
#endif
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Caching;
#endregion

namespace net.vieapps.Services.Files.Storages
{
	public class Startup
	{
		public static void Main(string[] args)
			=> WebHost.CreateDefaultBuilder(args).Run<Startup>(args, 8027);

		public Startup(IConfiguration configuration)
			=> this.Configuration = configuration;

		public IConfiguration Configuration { get; }

		LogLevel LogLevel => this.Configuration.GetAppSetting("Logging/LogLevel/Default", UtilityService.GetAppSetting("Logs:Level", "Information")).TryToEnum(out LogLevel logLevel) ? logLevel : LogLevel.Information;

		public void ConfigureServices(IServiceCollection services)
		{
			// mandatory services
			services
				.AddResponseCompression(options => options.EnableForHttps = true)
				.AddLogging(builder => builder.SetMinimumLevel(this.LogLevel))
				.AddCache(options => this.Configuration.GetSection("Cache").Bind(options))
				.AddHttpContextAccessor()
				.AddSession(options => Global.PrepareSessionOptions(options, 30))
				.Configure<CookiePolicyOptions>(options => Global.PrepareCookiePolicyOptions(options));

			// authentication
			services
				.AddAuthentication(options => Global.PrepareAuthenticationOptions(options, _ =>
				{
#if !NETCOREAPP2_1
					options.RequireAuthenticatedSignIn = false;
#endif
				}))
				.AddCookie(options => Global.PrepareCookieAuthenticationOptions(options, 30));

			// data protection (encrypt/decrypt authenticate ticket cookies & sync across load balancers)
			services.AddDataProtection().PrepareDataProtection();

#if !NETCOREAPP2_1
			// config options of IIS Server (for working with InProcess hosting model)
			if (Global.UseIISInProcess)
				services.Configure<IISServerOptions>(options => Global.PrepareIISServerOptions(options, _ =>
				{
					options.AllowSynchronousIO = true;
					options.MaxRequestBodySize = 1024 * 1024 * Global.MaxRequestBodySize;
				}));
#endif

			/*
			// config authentication with proxy/load balancer
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && "true".IsEquals(UtilityService.GetAppSetting("Proxy:UseIISIntegration")))
				services.Configure<IISOptions>(options => options.ForwardClientCertificate = false);

#if !NETCOREAPP2_1
			else
			{
				var certificateHeader = "true".IsEquals(UtilityService.GetAppSetting("Proxy:UseAzure"))
					? "X-ARR-ClientCert"
					: UtilityService.GetAppSetting("Proxy:X-Forwarded-Certificate");
				if (!string.IsNullOrWhiteSpace(certificateHeader))
					services.AddCertificateForwarding(options => options.CertificateHeader = certificateHeader);
			}
#endif
			*/
		}

		public void Configure(
			IApplicationBuilder appBuilder,
#if !NETCOREAPP2_1
			IHostApplicationLifetime appLifetime,
			IWebHostEnvironment environment
#else
			IApplicationLifetime appLifetime,
			IHostingEnvironment environment
#endif
		)
		{
			// settings
			var stopwatch = Stopwatch.StartNew();
			Console.OutputEncoding = Encoding.UTF8;
			Global.ServiceName = "Storages";
			AspNetCoreUtilityService.ServerName = UtilityService.GetAppSetting("ServerName", "VIEApps NGX");

			var loggerFactory = appBuilder.ApplicationServices.GetService<ILoggerFactory>();
			var logPath = UtilityService.GetAppSetting("Path:Logs");
			if (!string.IsNullOrWhiteSpace(logPath) && Directory.Exists(logPath))
			{
				logPath = Path.Combine(logPath, "{Hour}" + $"_{Global.ServiceName.ToLower()}.http.all.txt");
				loggerFactory.AddFile(logPath, this.LogLevel);
			}
			else
				logPath = null;

			Logger.AssignLoggerFactory(loggerFactory);
			Global.Logger = loggerFactory.CreateLogger<Startup>();

			Global.Logger.LogInformation($"The Files {Global.ServiceName} HTTP service is starting");
			Global.Logger.LogInformation($"Version: {typeof(Startup).Assembly.GetVersion()}");
#if DEBUG
			Global.Logger.LogInformation($"Working mode: DEBUG ({(environment.IsDevelopment() ? "Development" : "Production")})");
#else
			Global.Logger.LogInformation($"Working mode: RELEASE ({(environment.IsDevelopment() ? "Development" : "Production")})");
#endif
			Global.Logger.LogInformation($"Environment:\r\n\t{Extensions.GetRuntimeEnvironment()}");
			Global.Logger.LogInformation($"Service URIs:\r\n\t- Round robin: services.{Global.ServiceName.ToLower()}.http\r\n\t- Single (unique): services.{Extensions.GetUniqueName(Global.ServiceName + ".http")}");

			Global.ServiceProvider = appBuilder.ApplicationServices;
			Global.RootPath = environment.ContentRootPath;

			JsonConvert.DefaultSettings = () => new JsonSerializerSettings
			{
				Formatting = Formatting.None,
				ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
				DateTimeZoneHandling = DateTimeZoneHandling.Local
			};

			// setup middlewares
			appBuilder
				.UseForwardedHeaders(Global.GetForwardedHeadersOptions())
				.UseStatusCodeHandler()
				.UseResponseCompression()
				.UseCache()
				.UseSession()
#if !NETCOREAPP2_1
				.UseCertificateForwarding()
#endif
				.UseCookiePolicy()
				.UseAuthentication()
				.UseMiddleware<Handler>();

			// setup the caching storage
			Global.Cache = appBuilder.ApplicationServices.GetService<ICache>();

			// connect to API Gateway Router
			Handler.Connect();

			// on started
			appLifetime.ApplicationStarted.Register(() =>
			{
				Global.Logger.LogInformation($"API Gateway Router: {new Uri(Router.GetRouterStrInfo()).GetResolvedURI()}");
				Global.Logger.LogInformation($"API Gateway HTTP service: {UtilityService.GetAppSetting("HttpUri:APIs", "None")}");
				Global.Logger.LogInformation($"Files HTTP service: {UtilityService.GetAppSetting("HttpUri:Files", "None")}");
				Global.Logger.LogInformation($"Portals HTTP service: {UtilityService.GetAppSetting("HttpUri:Portals", "None")}");
				Global.Logger.LogInformation($"Passports HTTP service: {UtilityService.GetAppSetting("HttpUri:Passports", "None")}");
				Global.Logger.LogInformation($"Root (base) directory: {Global.RootPath}");
				Global.Logger.LogInformation($"Temporary directory: {UtilityService.GetAppSetting("Path:Temp", "None")}");
				Global.Logger.LogInformation($"Static files directory: {UtilityService.GetAppSetting("Path:StaticFiles", "None")}");
				Global.Logger.LogInformation($"Static segments: {Global.StaticSegments.ToString(", ")}");
				Global.Logger.LogInformation($"Logging level: {this.LogLevel} - Local rolling log files is {(string.IsNullOrWhiteSpace(logPath) ? "disabled" : $"enabled => {logPath}")}");
				Global.Logger.LogInformation($"Show debugs: {Global.IsDebugLogEnabled} - Show results: {Global.IsDebugResultsEnabled} - Show stacks: {Global.IsDebugStacksEnabled}");

				stopwatch.Stop();
				Global.Logger.LogInformation($"The Files {Global.ServiceName} HTTP service is started - PID: {Process.GetCurrentProcess().Id} - Execution times: {stopwatch.GetElapsedTimes()}");
				Global.Logger = loggerFactory.CreateLogger<Handler>();
			});

			// on stopping
			appLifetime.ApplicationStopping.Register(() => Global.Logger = loggerFactory.CreateLogger<Startup>());

			// on stopped
			appLifetime.ApplicationStopped.Register(() =>
			{
				Handler.Disconnect();
				Global.CancellationTokenSource.Cancel();
				Global.CancellationTokenSource.Dispose();
				Global.Logger.LogInformation($"The Files {Global.ServiceName} HTTP service was stopped");
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