#region Related components
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;

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
			// setup console
			if (Environment.UserInteractive)
				Console.OutputEncoding = Encoding.UTF8;

			// host the HTTP with Kestrel
			WebHost.CreateDefaultBuilder(args)
				.UseStartup<Startup>()
				.UseKestrel()
				.UseUrls((args.FirstOrDefault(a => a.IsStartsWith("/listenuri:"))?.Replace(StringComparison.OrdinalIgnoreCase, "/listenuri:", "").Trim() ?? UtilityService.GetAppSetting("HttpUri:Listen", "http://0.0.0.0:8025")))
				.Build()
				.Run();

			// dispose objects
			WAMPConnections.CloseChannels();
			Global.InterCommunicateMessageUpdater?.Dispose();
			Global.CancellationTokenSource.Cancel();
			Global.CancellationTokenSource.Dispose();
			Global.RSA.Dispose();
			Global.Logger.LogInformation($"The {Global.ServiceName} HTTP service is stopped");
		}

		public Startup(IConfiguration configuration) => this.Configuration = configuration;

		public IConfiguration Configuration { get; }

		public void ConfigureServices(IServiceCollection services)
		{
			// mandatory services
			services.AddResponseCompression(options => options.EnableForHttps = true);
			services.AddLogging(builder => builder.SetMinimumLevel(UtilityService.GetAppSetting("Logs:Level", this.Configuration.GetAppSetting("Logging/LogLevel/Default", "Information")).ToEnum<LogLevel>()));
			services.AddCache(options => this.Configuration.GetSection("Cache").Bind(options));
			services.AddHttpContextAccessor();

			 // session state
			services.AddSession(options =>
			{
				options.IdleTimeout = TimeSpan.FromMinutes(30);
				options.Cookie.Name = "VIEApps-Session";
				options.Cookie.HttpOnly = true;
			});

			// authentication
			services.AddAuthentication(options => options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme)
				.AddCookie(options =>
				{
					options.Cookie.Name = "VIEApps-Auth";
					options.Cookie.HttpOnly = true;
					options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
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

			// IIS integration
			services.Configure<IISOptions>(options =>
			{
				options.ForwardClientCertificate = false;
				options.AutomaticAuthentication = true;
			});
		}

		public void Configure(IApplicationBuilder app, IHostingEnvironment environment)
		{
			// settings
			var stopwatch = Stopwatch.StartNew();
			Global.ServiceName = "Files";

			var loggerFactory = app.ApplicationServices.GetService<ILoggerFactory>();
			var logLevel = UtilityService.GetAppSetting("Logs:Level", this.Configuration.GetAppSetting("Logging/LogLevel/Default", "Information")).ToEnum<LogLevel>();
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
			Global.Logger.LogInformation($"Logging is enabled [{logLevel}]");
			if (!string.IsNullOrWhiteSpace(path))
				Global.Logger.LogInformation($"Rolling log files is enabled [{path}]");

			Global.ServiceProvider = app.ApplicationServices;
			Global.CreateRSA();

			Handler.RootPath = environment.ContentRootPath;
			Handler.PrepareHandlers();

			JsonConvert.DefaultSettings = () => new JsonSerializerSettings()
			{
				Formatting = Formatting.None,
				ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
				DateTimeZoneHandling = DateTimeZoneHandling.Local
			};

			// WAMP
			Handler.OpenWAMPChannels();

			// middleware
			app.UseStatusCodeHandler();
			app.UseResponseCompression();
			app.UseCache();
			app.UseSession();
			app.UseAuthentication();
			app.UseMiddleware<Handler>();

			// done
			stopwatch.Stop();
			Global.Logger.LogInformation($"The {Global.ServiceName} HTTP service is started - Execution times: {stopwatch.GetElapsedTimes()}");
			Global.Logger = loggerFactory.CreateLogger<Handler>();
		}
	}
}