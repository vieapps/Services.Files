#region Related components
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Distributed;

using Newtonsoft.Json;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Caching;
#endregion

namespace net.vieapps.Services.Files
{
	public class Startup
	{
		public static void Main(string[] args) => WebHost.CreateDefaultBuilder(args).Run<Startup>(args, 8025);

		public Startup(IConfiguration configuration) => this.Configuration = configuration;

		public IConfiguration Configuration { get; }

		LogLevel LogLevel => this.Configuration.GetAppSetting("Logging/LogLevel/Default", UtilityService.GetAppSetting("Logs:Level", "Information")).ToEnum<LogLevel>();

		public void ConfigureServices(IServiceCollection services)
		{
			// mandatory services
			services
				.AddResponseCompression(options => options.EnableForHttps = true)
				.AddLogging(builder => builder.SetMinimumLevel(this.LogLevel))
				.AddCache(options => this.Configuration.GetSection("Cache").Bind(options))
				.AddHttpContextAccessor()
				.AddSession(options =>
				{
					options.IdleTimeout = TimeSpan.FromMinutes(5);
					options.Cookie.Name = "VIEApps-Session";
					options.Cookie.HttpOnly = true;
				})
				.Configure<FormOptions>(options =>
				{
					options.MultipartBodyLengthLimit = 1024 * 1024 * (Int32.TryParse(UtilityService.GetAppSetting("Limits:Body"), out var limitSize) ? limitSize : 10);
				});

			// authentication
			services
				.AddAuthentication(options => options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme)
				.AddCookie(options =>
				{
					options.Cookie.Name = "VIEApps-Auth";
					options.Cookie.HttpOnly = true;
					options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
					options.SlidingExpiration = true;
				});

			// data protection (to encrypt/decrypt authenticate cookies)
			services
				.AddDataProtection()
				.SetDefaultKeyLifetime(TimeSpan.FromDays(7))
				.SetApplicationName("VIEApps-NGX-Files")
				.UseCryptographicAlgorithms(new AuthenticatedEncryptorConfiguration
				{
					EncryptionAlgorithm = EncryptionAlgorithm.AES_256_CBC,
					ValidationAlgorithm = ValidationAlgorithm.HMACSHA256
				});
		}

		public void Configure(IApplicationBuilder appBuilder, IApplicationLifetime appLifetime, IHostingEnvironment environment)
		{
			// environments
			var stopwatch = Stopwatch.StartNew();
			Console.OutputEncoding = Encoding.UTF8;
			Global.ServiceName = "Files";
			AspNetCoreUtilityService.ServerName = UtilityService.GetAppSetting("ServerName", "VIEApps NGX");

			JsonConvert.DefaultSettings = () => new JsonSerializerSettings
			{
				Formatting = Formatting.None,
				ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
				DateTimeZoneHandling = DateTimeZoneHandling.Local
			};

			var loggerFactory = appBuilder.ApplicationServices.GetService<ILoggerFactory>();
			var logPath = UtilityService.GetAppSetting("Path:Logs");
			if (!string.IsNullOrWhiteSpace(logPath) && Directory.Exists(logPath))
			{
				logPath = Path.Combine(logPath, "{Hour}" + $"_{Global.ServiceName.ToLower()}.http.all.txt");
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
			Global.Logger.LogInformation($"Environment:\r\n\t- User: {Environment.UserName.ToLower()} @ {Environment.MachineName.ToLower()}\r\n\t- Platform: {Extensions.GetRuntimePlatform()}");
			Global.Logger.LogInformation($"Service URIs:\r\n\t- Round robin: services.{Global.ServiceName.ToLower()}.http\r\n\t- Single (unique): services.{Handler.NodeName}");

			Global.CreateRSA();
			Handler.PrepareHandlers();
			Handler.Connect();

			// setup middlewares
			appBuilder
				.UseForwardedHeaders(Global.GetForwardedHeadersOptions())
				.UseStatusCodeHandler()
				.UseResponseCompression()
				.UseCache()
				.UseSession()
				.UseAuthentication()
				.UseMiddleware<Handler>();

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
				Global.Logger.LogInformation($"Logging level: {this.LogLevel} - Rolling log files is {(string.IsNullOrWhiteSpace(logPath) ? "disabled" : $"enabled => {logPath}")}");
				Global.Logger.LogInformation($"Show debugs: {Global.IsDebugLogEnabled} - Show results: {Global.IsDebugResultsEnabled} - Show stacks: {Global.IsDebugStacksEnabled}");
				Global.Logger.LogInformation($"Request limits => Files (multipart/form-data): {UtilityService.GetAppSetting("Limits:Body", "10")} MB - Avatars: {UtilityService.GetAppSetting("Limits:Avatar", "1024")} KB - Thumbnails: {UtilityService.GetAppSetting("Limits:Thumbnail", "512")} KB");
				
				stopwatch.Stop();
				Global.Logger.LogInformation($"The {Global.ServiceName} HTTP service is started - PID: {Process.GetCurrentProcess().Id} - Execution times: {stopwatch.GetElapsedTimes()}");
				Global.Logger = loggerFactory.CreateLogger<Handler>();
			});

			// on stopping
			appLifetime.ApplicationStopping.Register(() =>
			{
				Global.Logger = loggerFactory.CreateLogger<Startup>();
				Handler.Disconnect();
				Global.RSA.Dispose();
				Global.CancellationTokenSource.Cancel();
			});

			// on stopped
			appLifetime.ApplicationStopped.Register(() =>
			{
				Global.CancellationTokenSource.Dispose();
				Global.Logger.LogInformation($"The {Global.ServiceName} HTTP service is stopped");
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