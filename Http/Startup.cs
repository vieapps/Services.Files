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
		public static void Main(string[] args)
		{
			WebHost.CreateDefaultBuilder(args)
				.CaptureStartupErrors(true)
				.UseStartup<Startup>()
				.UseKestrel()
				.UseUrls(args.FirstOrDefault(a => a.IsStartsWith("/listenuri:"))?.Replace("/listenuri:", "").Trim() ?? UtilityService.GetAppSetting("HttpUri:Listen", "http://0.0.0.0:8025"))
				.Build()
				.Run();
		}

		public Startup(IConfiguration configuration) => this.Configuration = configuration;

		public IConfiguration Configuration { get; }

		public void ConfigureServices(IServiceCollection services)
		{
			// mandatory services
			services.AddResponseCompression(options => options.EnableForHttps = true);
			services.AddLogging(builder => builder.SetMinimumLevel(this.Configuration.GetAppSetting("Logging/LogLevel/Default", "Information").ToEnum<LogLevel>()));
			services.AddCache(options => this.Configuration.GetSection("Cache").Bind(options));
			services.AddHttpContextAccessor();

			// session state
			services.AddSession(options =>
			{
				options.IdleTimeout = TimeSpan.FromMinutes(5);
				options.Cookie.Name = "VIEApps-Session";
				options.Cookie.HttpOnly = true;
			});

			// authentication
			services.AddAuthentication(options => options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme)
				.AddCookie(options =>
				{
					options.Cookie.Name = "VIEApps-Auth";
					options.Cookie.HttpOnly = true;
					options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
					options.SlidingExpiration = true;
				});
			services.AddDataProtection()
				.SetDefaultKeyLifetime(TimeSpan.FromDays(7))
				.SetApplicationName("VIEApps-NGX")
				.UseCryptographicAlgorithms(new AuthenticatedEncryptorConfiguration
				{
					EncryptionAlgorithm = EncryptionAlgorithm.AES_256_CBC,
					ValidationAlgorithm = ValidationAlgorithm.HMACSHA256
				});
		}

		public void Configure(IApplicationBuilder app, IApplicationLifetime appLifetime, IHostingEnvironment environment)
		{
			// settings
			var stopwatch = Stopwatch.StartNew();
			Global.ServiceName = "Files";
			Console.OutputEncoding = Encoding.UTF8;

			var loggerFactory = app.ApplicationServices.GetService<ILoggerFactory>();
			var logLevel = this.Configuration.GetAppSetting("Logging/LogLevel/Default", "Information").ToEnum<LogLevel>();
			var path = UtilityService.GetAppSetting("Path:Logs");
			if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
			{
				path = Path.Combine(path, "{Date}" + $"_{Global.ServiceName.ToLower()}.http.txt");
				loggerFactory.AddFile(path, logLevel);
			}
			else
				path = null;

			Logger.AssignLoggerFactory(loggerFactory);
			Global.Logger = loggerFactory.CreateLogger<Startup>();

			Global.Logger.LogInformation($"The {Global.ServiceName} HTTP service is starting");
			Global.Logger.LogInformation($"Version: {typeof(Startup).Assembly.GetVersion()}");
			Global.Logger.LogInformation($"Platform: {RuntimeInformation.FrameworkDescription} @ {(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : $"Other OS")} {RuntimeInformation.OSArchitecture} ({RuntimeInformation.OSDescription.Trim()})");
#if DEBUG
			Global.Logger.LogInformation($"Working mode: DEBUG ({(environment.IsDevelopment() ? "Development" : "Production")})");
#else
			Global.Logger.LogInformation($"Working mode: RELEASE ({(environment.IsDevelopment() ? "Development" : "Production")})");
#endif

			Global.CreateRSA();
			Global.ServiceProvider = app.ApplicationServices;
			Global.RootPath = environment.ContentRootPath;

			JsonConvert.DefaultSettings = () => new JsonSerializerSettings()
			{
				Formatting = Formatting.None,
				ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
				DateTimeZoneHandling = DateTimeZoneHandling.Local
			};

			// prepare handlers
			Handler.PrepareHandlers();
			Handler.OpenWAMPChannels();

			// middleware
			app.UseStatusCodeHandler();
			app.UseResponseCompression();
			app.UseCache();
			app.UseSession();
			app.UseAuthentication();
			app.UseMiddleware<Handler>();

			// on started
			appLifetime.ApplicationStarted.Register(() =>
			{
				if (environment.IsDevelopment() || Environment.UserInteractive)
					Global.Logger.LogInformation($"Listening URI: {UtilityService.GetAppSetting("HttpUri:Listen", "http://0.0.0.0:8025")}");
				Global.Logger.LogInformation($"WAMP router URI: {WAMPConnections.GetRouterStrInfo()}");
				Global.Logger.LogInformation($"API Gateway HTTP service URI: {UtilityService.GetAppSetting("HttpUri:APIs")}");
				Global.Logger.LogInformation($"Files HTTP service URI: {UtilityService.GetAppSetting("HttpUri:Files")}");
				Global.Logger.LogInformation($"Users HTTP service URI: {UtilityService.GetAppSetting("HttpUri:Users")}");
				Global.Logger.LogInformation($"Root path: {Global.RootPath}");
				Global.Logger.LogInformation($"Logs path: {UtilityService.GetAppSetting("Path:Logs")}");
				Global.Logger.LogInformation($"Default logging level: {logLevel} [ASP.NET Core always set logging level by value of appsettings.json]");
				if (!string.IsNullOrWhiteSpace(path))
					Global.Logger.LogInformation($"Rolling log files is enabled - Path format: {path}");
				Global.Logger.LogInformation($"Static files path: {UtilityService.GetAppSetting("Path:StaticFiles")}");
				Global.Logger.LogInformation($"Static segments: {Global.StaticSegments.ToString(", ")}");
				Global.Logger.LogInformation($"Show debugs: {Global.IsDebugLogEnabled} - Show results: {Global.IsDebugResultsEnabled} - Show stacks: {Global.IsDebugStacksEnabled}");

				stopwatch.Stop();
				Global.Logger.LogInformation($"The {Global.ServiceName} HTTP service is started - Execution times: {stopwatch.GetElapsedTimes()}");
				Global.Logger = loggerFactory.CreateLogger<Handler>();
			});

			// on stopping
			appLifetime.ApplicationStopping.Register(() =>
			{
				Global.Logger = loggerFactory.CreateLogger<Startup>();

				Global.InterCommunicateMessageUpdater?.Dispose();
				WAMPConnections.CloseChannels();

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