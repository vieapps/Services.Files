#region Related components
using System;
using System.Configuration;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Linq;
using System.Xml;
using System.Web;
using System.Web.Security;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;

using net.vieapps.Services.Base.AspNet;
#endregion

namespace net.vieapps.Services.Files
{
	public static class Global
	{

		#region Attributes
		internal static HashSet<string> HiddenSegments = null, BypassSegments = null, StaticSegments = null;
		internal static Dictionary<string, Type> Handlers = new Dictionary<string, Type>();

		internal static Dictionary<string, IService> Services = new Dictionary<string, IService>();
		internal static IDisposable InterCommunicateMessageUpdater = null;
		#endregion

		#region Start/End the app
		internal static void OnAppStart(HttpContext context)
		{
			var stopwatch = new Stopwatch();
			stopwatch.Start();

			// Json.NET
			JsonConvert.DefaultSettings = () => new JsonSerializerSettings()
			{
				Formatting = Newtonsoft.Json.Formatting.Indented,
				ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
				DateTimeZoneHandling = DateTimeZoneHandling.Local
			};


			// default service name
			Base.AspNet.Global.ServiceName = "Files";

			// open WAMP channels
			Task.Run(async () =>
			{
				await Base.AspNet.Global.OpenChannelsAsync(
					(sender, args) =>
					{
						Global.InterCommunicateMessageUpdater = Base.AspNet.Global.IncommingChannel.RealmProxy.Services
							.GetSubject<CommunicateMessage>("net.vieapps.rtu.communicate.messages.files")
							.Subscribe(
								message => Global.ProcessInterCommunicateMessage(message),
								exception => Base.AspNet.Global.WriteLogs("Error occurred while fetching inter-communicate message", exception)
							);
					},
					(sender, args) =>
					{
						Task.Run(async () =>
						{
							await Task.WhenAll(
								Base.AspNet.Global.InitializeLoggingServiceAsync(),
								Base.AspNet.Global.InitializeRTUServiceAsync()
							).ConfigureAwait(false);
						}).ConfigureAwait(false);
					}
				).ConfigureAwait(false);
			}).ConfigureAwait(false);

			// special segments
			Global.BypassSegments = UtilityService.GetAppSetting("BypassSegments")?.Trim().ToLower().ToHashSet('|', true) ?? new HashSet<string>();
			Global.HiddenSegments = UtilityService.GetAppSetting("HiddenSegments")?.Trim().ToLower().ToHashSet('|', true) ?? new HashSet<string>();
			Global.StaticSegments = UtilityService.GetAppSetting("StaticSegments")?.Trim().ToLower().ToHashSet('|', true) ?? new HashSet<string>();

			// default handlers
			Global.Handlers = new Dictionary<string, Type>()
			{
				{ "avatars", Type.GetType("net.vieapps.Services.Files.AvatarHandler, VIEApps.Services.Files.Http") },
				{ "captchas", Type.GetType("net.vieapps.Services.Files.CaptchaHandler, VIEApps.Services.Files.Http") },
				{ "thumbnails", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler, VIEApps.Services.Files.Http") },
				{ "thumbnailbigs", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler, VIEApps.Services.Files.Http") },
				{ "thumbnailpngs", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler, VIEApps.Services.Files.Http") },
				{ "thumbnailbigpngs", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler, VIEApps.Services.Files.Http") },
				{ "files", Type.GetType("net.vieapps.Services.Files.FileHandler, VIEApps.Services.Files.Http") },
				{ "downloads", Type.GetType("net.vieapps.Services.Files.DownloadHandler, VIEApps.Services.Files.Http") }
			};

			// additional handlers
			if (ConfigurationManager.GetSection("net.vieapps.files.handlers") is AppConfigurationSectionHandler config)
				if (config.Section.SelectNodes("handler") is XmlNodeList nodes)
					foreach (XmlNode node in nodes)
					{
						var info = node.ToJson();

						var keyName = info["key"] != null && info["key"] is JValue && (info["key"] as JValue).Value != null
							? (info["key"] as JValue).Value.ToString().ToLower()
							: null;

						var typeName = info["type"] != null && info["type"] is JValue && (info["type"] as JValue).Value != null
							? (info["type"] as JValue).Value.ToString()
							: null;

						if (!string.IsNullOrWhiteSpace(keyName) && !string.IsNullOrWhiteSpace(typeName) && !Global.Handlers.ContainsKey(keyName))
							try
							{
								var type = Type.GetType(typeName);
								if (type != null && type.CreateInstance() is AbstractHttpHandler)
									Global.Handlers.Add(keyName, type);
							}
							catch { }
					}

			// handling unhandled exception
			AppDomain.CurrentDomain.UnhandledException += (sender, arguments) =>
			{
				Base.AspNet.Global.WriteLogs("An unhandled exception is thrown", arguments.ExceptionObject as Exception);
			};

			stopwatch.Stop();
			Base.AspNet.Global.WriteLogs("*** The File HTTP Service is ready for serving. The app is initialized in " + stopwatch.GetElapsedTimes());
		}

		internal static void OnAppEnd()
		{
			Global.InterCommunicateMessageUpdater?.Dispose();
			Base.AspNet.Global.CancellationTokenSource.Cancel();
			Base.AspNet.Global.CancellationTokenSource.Dispose();
			Base.AspNet.Global.CloseChannels();
			Base.AspNet.Global.RSA.Dispose();
		}
		#endregion

		#region Begin/End the request
		internal static void OnAppBeginRequest(HttpApplication app)
		{
			// update default headers to allow access from everywhere
			app.Context.Response.HeaderEncoding = Encoding.UTF8;
			app.Context.Response.Headers.Add("access-control-allow-origin", "*");
			app.Context.Response.Headers.Add("x-correlation-id", Base.AspNet.Global.GetCorrelationID(app.Context.Items));

			// update special headers on OPTIONS request
			if (app.Context.Request.HttpMethod.Equals("OPTIONS"))
			{
				app.Context.Response.Headers.Add("access-control-allow-methods", "HEAD,GET,POST");

				var allowHeaders = app.Context.Request.Headers.Get("access-control-request-headers");
				if (!string.IsNullOrWhiteSpace(allowHeaders))
					app.Context.Response.Headers.Add("access-control-allow-headers", allowHeaders);

				return;
			}

			// authentication: process passport token
			if (!string.IsNullOrWhiteSpace(app.Context.Request.QueryString["x-passport-token"]))
				try
				{
					// parse
					var token = User.ParsePassportToken(app.Context.Request.QueryString["x-passport-token"], Base.AspNet.Global.AESKey, Base.AspNet.Global.GenerateJWTKey());
					var userID = token.Item1;
					var accessToken = token.Item2;
					var sessionID = token.Item3;
					var deviceID = token.Item4;

					var ticket = AspNetSecurityService.ParseAuthenticateToken(accessToken, Base.AspNet.Global.RSA, Base.AspNet.Global.AESKey);
					accessToken = ticket.Item2;

					var user = User.ParseAccessToken(accessToken, Base.AspNet.Global.RSA, Base.AspNet.Global.AESKey);
					if (!user.ID.Equals(ticket.Item1) || !user.ID.Equals(userID))
						throw new InvalidTokenException("Token is invalid (User identity is invalid)");

					// assign user credential
					app.Context.User = new UserPrincipal(user);
					var authCookie = new HttpCookie(FormsAuthentication.FormsCookieName)
					{
						Value = AspNetSecurityService.GetAuthenticateToken(userID, accessToken, sessionID, deviceID, FormsAuthentication.Timeout.Minutes),
						HttpOnly = true
					};
					app.Context.Response.SetCookie(authCookie);

					// assign session/device identity
					Global.SetSessionID(app.Context, sessionID);
					Global.SetDeviceID(app.Context, deviceID);
				}
				catch { }

			// prepare
			var requestTo = app.Request.AppRelativeCurrentExecutionFilePath;
			if (requestTo.StartsWith("~/"))
				requestTo = requestTo.Right(requestTo.Length - 2);
			requestTo = string.IsNullOrEmpty(requestTo)
				? ""
				: requestTo.ToLower().ToArray('/', true).First();

			// by-pass segments
			if (Global.BypassSegments.Count > 0 && Global.BypassSegments.Contains(requestTo))
				return;

			// hidden segments
			else if (Global.HiddenSegments.Count > 0 && Global.HiddenSegments.Contains(requestTo))
			{
				app.Context.ShowError(403, "Forbidden", "AccessDeniedException", null);
				app.Context.Response.End();
				return;
			}

			// 403/404 errors
			else if (requestTo.IsEquals("global.ashx"))
			{
				var errorElements = app.Context.Request.QueryString != null && app.Context.Request.QueryString.Count > 0
					? app.Context.Request.QueryString.ToString().UrlDecode().ToArray(';')
					: new string[] { "500", "" };
				var errorMessage = errorElements[0].Equals("403")
					? "Forbidden"
					: errorElements[0].Equals("404")
						? "Not Found"
						: "Unknown (" + errorElements[0] + " : " + (errorElements.Length > 1 ? errorElements[1].Replace(":80", "").Replace(":443", "") : "unknown") + ")";
				var errorType = errorElements[0].Equals("403")
					? "AccessDeniedException"
					: errorElements[0].Equals("404")
						? "FileNotFoundException"
						: "Unknown";
				app.Context.ShowError(errorElements[0].CastAs<int>(), errorMessage, errorType, null);
				app.Context.Response.End();
				return;
			}

#if DEBUG || REQUESTLOGS
			var appInfo = app.Context.GetAppInfo();
			Base.AspNet.Global.WriteLogs(new List<string>() {
					"Begin process [" + app.Context.Request.HttpMethod + "]: " + app.Context.Request.Url.Scheme + "://" + app.Context.Request.Url.Host + app.Context.Request.RawUrl,
					"- Origin: " + appInfo.Item1 + " / " + appInfo.Item2 + " - " + appInfo.Item3,
					"- IP: " + app.Context.Request.UserHostAddress,
					"- Agent: " + app.Context.Request.UserAgent,
				});

			app.Context.Items["StopWatch"] = new Stopwatch();
			(app.Context.Items["StopWatch"] as Stopwatch).Start();
#endif

			// rewrite url
			var query = "";
			foreach (string key in app.Request.QueryString)
				if (!string.IsNullOrWhiteSpace(key))
					query += (query.Equals("") ? "" : "&") + key + "=" + app.Request.QueryString[key].UrlEncode();

			app.Context.RewritePath(app.Request.ApplicationPath + "Global.ashx", null, query);
		}

		internal static void OnAppEndRequest(HttpApplication app)
		{
#if DEBUG || REQUESTLOGS
			// add execution times
			if (!app.Context.Request.HttpMethod.Equals("OPTIONS") && app.Context.Items.Contains("StopWatch"))
			{
				(app.Context.Items["StopWatch"] as Stopwatch).Stop();
				var executionTimes = (app.Context.Items["StopWatch"] as Stopwatch).GetElapsedTimes();
				Base.AspNet.Global.WriteLogs("End process - Execution times: " + executionTimes);
				try
				{
					app.Context.Response.Headers.Add("x-execution-times", executionTimes);
				}
				catch { }
			}
#endif
		}
		#endregion

		#region Authenticate request
		internal static void OnAppAuthenticateRequest(HttpApplication app)
		{
			if (app.Context.User == null || !(app.Context.User is UserPrincipal))
			{
				var authCookie = app.Context.Request.Cookies?[FormsAuthentication.FormsCookieName];
				if (authCookie != null)
				{
					var ticket = AspNetSecurityService.ParseAuthenticateToken(authCookie.Value, Base.AspNet.Global.RSA, Base.AspNet.Global.AESKey);
					var userID = ticket.Item1;
					var accessToken = ticket.Item2;
					var sessionID = ticket.Item3;
					var deviceID = ticket.Item4;

					app.Context.User = new UserPrincipal(User.ParseAccessToken(accessToken, Base.AspNet.Global.RSA, Base.AspNet.Global.AESKey));
					app.Context.Items["Session-ID"] = sessionID;
					app.Context.Items["Device-ID"] = deviceID;
				}
				else
				{
					app.Context.User = new UserPrincipal();
					Global.GetSessionID(app.Context);
					Global.GetDeviceID(app.Context);
				}
			}
		}

		internal static void SetSessionID(HttpContext context, string sessionID)
		{
			context = context ?? HttpContext.Current;
			context.Items["Session-ID"] = sessionID;
			var cookie = new HttpCookie(".VIEApps-Authenticated-Session-ID")
			{
				Value = "VIEApps|" + sessionID.Encrypt(Base.AspNet.Global.AESKey),
				HttpOnly = true,
				Expires = DateTime.Now.AddDays(180)
			};
			context.Response.SetCookie(cookie);
		}

		internal static string GetSessionID(HttpContext context)
		{
			context = context ?? HttpContext.Current;
			if (!context.Items.Contains("Session-ID"))
			{
				var cookie = context.Request.Cookies?[".VIEApps-Authenticated-Session-ID"];
				if (cookie != null && cookie.Value.StartsWith("VIEApps|"))
					try
					{
						context.Items["Session-ID"] = cookie.Value.ToArray('|').Last().Decrypt(Base.AspNet.Global.AESKey);
					}
					catch { }
			}
			return context.Items["Session-ID"] as string;
		}

		internal static void SetDeviceID(HttpContext context, string sessionID)
		{
			context = context ?? HttpContext.Current;
			context.Items["Device-ID"] = sessionID;
			var cookie = new HttpCookie(".VIEApps-Authenticated-Device-ID")
			{
				Value = "VIEApps|" + sessionID.Encrypt(Base.AspNet.Global.AESKey),
				HttpOnly = true,
				Expires = DateTime.Now.AddDays(180)
			};
			context.Response.SetCookie(cookie);
		}

		internal static string GetDeviceID(HttpContext context)
		{
			context = context ?? HttpContext.Current;
			if (!context.Items.Contains("Device-ID"))
			{
				var cookie = context.Request.Cookies?[".VIEApps-Authenticated-Device-ID"];
				if (cookie != null && cookie.Value.StartsWith("VIEApps|"))
					try
					{
						context.Items["Device-ID"] = cookie.Value.ToArray('|').Last().Decrypt(Base.AspNet.Global.AESKey);
					}
					catch { }
			}
			return context.Items["Device-ID"] as string;
		}
		#endregion

		#region Pre send headers
		internal static void OnAppPreSendHeaders(HttpApplication app)
		{
			// remove un-nessesary headers
			app.Context.Response.Headers.Remove("allow");
			app.Context.Response.Headers.Remove("public");
			app.Context.Response.Headers.Remove("x-powered-by");

			// add special headers
			if (app.Response.Headers["server"] != null)
				app.Response.Headers.Set("server", "VIEApps NGX");
			else
				app.Response.Headers.Add("server", "VIEApps NGX");
		}
		#endregion

		#region Error handlings
		static string ShowErrorStacks = null;

		internal static bool IsShowErrorStacks
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Global.ShowErrorStacks))
#if DEBUG
					Global.ShowErrorStacks = "true";
#else
					Global.ShowErrorStacks = UtilityService.GetAppSetting("ShowErrorStacks", "false");
#endif
				return Global.ShowErrorStacks.IsEquals("true");
			}
		}

		internal static void ShowError(this HttpContext context, int code, string message, string type, string stack)
		{
			context.ShowHttpError(code, message, type, Base.AspNet.Global.GetCorrelationID(context.Items), stack, Global.IsShowErrorStacks);
		}

		internal static void ShowError(this HttpContext context, Exception exception)
		{
			context.ShowError(exception != null ? exception.GetHttpStatusCode() : 0, exception != null ? exception.Message : "Unknown", exception != null ? exception.GetType().ToString().ToArray('.').Last() : "Unknown", exception != null && Global.IsShowErrorStacks ? exception.StackTrace : null);
		}

		internal static void OnAppError(HttpApplication app)
		{
			var exception = app.Server.GetLastError();
			app.Server.ClearError();

			Base.AspNet.Global.WriteLogs("", exception);
			app.Context.ShowError(exception);
		}
		#endregion

		#region Get & call services
		internal static async Task<IService> GetServiceAsync(string name)
		{
			if (!Global.Services.TryGetValue(name, out IService service))
			{
				await Base.AspNet.Global.OpenOutgoingChannelAsync().ConfigureAwait(false);
				lock (Global.Services)
				{
					if (!Global.Services.TryGetValue(name, out service))
					{
						service = Base.AspNet.Global.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(ProxyInterceptor.Create(name));
						Global.Services.Add(name, service);
					}
				}
			}
			return service;
		}

		internal static async Task<JObject> CallServiceAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			var name = requestInfo.ServiceName.Trim().ToLower();

#if DEBUG
			Base.AspNet.Global.WriteLogs(requestInfo.CorrelationID, null, "Call the service [net.vieapps.services." + name + "]" + "\r\n" + requestInfo.ToJson().ToString(Newtonsoft.Json.Formatting.Indented));
#endif

			JObject json = null;
			try
			{
				var service = await Global.GetServiceAsync(name);
				json = await service.ProcessRequestAsync(requestInfo, cancellationToken);
			}
			catch (Exception)
			{
				throw;
			}

#if DEBUG
			Base.AspNet.Global.WriteLogs(requestInfo.CorrelationID, null, "Result of the service [net.vieapps.services." + name + "]" + "\r\n" + json.ToString(Newtonsoft.Json.Formatting.Indented));
#endif

			return json;
		}

		internal static async Task<JObject> CallServiceAsync(Session session, string serviceName, string objectName, string verb = "GET", Dictionary<string, string> header = null, Dictionary<string, string> query = null, string body = null, Dictionary<string, string> extra = null, string correlationID = null)
		{
			return await Global.CallServiceAsync(new RequestInfo()
			{
				Session = session ?? Global.GetSession(),
				ServiceName = serviceName ?? "unknown",
				ObjectName = objectName ?? "unknown",
				Verb = string.IsNullOrWhiteSpace(verb) ? "GET" : verb,
				Query = query ?? new Dictionary<string, string>(),
				Header = header ?? new Dictionary<string, string>(),
				Body = body,
				Extra = extra ?? new Dictionary<string, string>(),
				CorrelationID = correlationID ?? Base.AspNet.Global.GetCorrelationID()
			},
			Base.AspNet.Global.CancellationTokenSource.Token).ConfigureAwait(false);
		}
		#endregion

		#region Authentication
		internal static Session GetSession(NameValueCollection header, NameValueCollection query, string agentString, string ipAddress, Uri urlReferrer = null)
		{
			var appInfo = Base.AspNet.Global.GetAppInfo(header, query, agentString, ipAddress, urlReferrer);
			return new Session()
			{
				IP = ipAddress,
				AppAgent = agentString,
				DeviceID = UtilityService.GetAppParameter("x-device-id", header, query, ""),
				AppName = appInfo.Item1,
				AppPlatform = appInfo.Item2,
				AppOrigin = appInfo.Item3
			};
		}

		internal static Session GetSession(HttpContext context = null)
		{
			context = context ?? HttpContext.Current;
			var session = Global.GetSession(context.Request.Headers, context.Request.QueryString, context.Request.UserAgent, context.Request.UserHostAddress, context.Request.UrlReferrer);
			session.User = context.User as User;
			if (string.IsNullOrWhiteSpace(session.SessionID))
				session.SessionID = Global.GetSessionID(context);
			if (string.IsNullOrWhiteSpace(session.DeviceID))
				session.DeviceID = Global.GetDeviceID(context);
			return session;
		}

		internal static string GetTransferToPassportUrl(HttpContext context = null, string url = null)
		{
			context = context ?? HttpContext.Current;
			url = url ?? context.Request.Url.Scheme + "://" + context.Request.Url.Host + context.Request.RawUrl;
			return Global.UsersHttpUri + "validator"
				+ "?aut=" + (UtilityService.NewUID.Left(5) + "-" + (context.Request.IsAuthenticated ? "ON" : "OFF")).Encrypt(Base.AspNet.Global.AESKey).ToBase64Url(true)
				+ "&uid=" + (context.Request.IsAuthenticated ? (context.User as User).ID : "").Encrypt(Base.AspNet.Global.AESKey).ToBase64Url(true)
				+ "&uri=" + url.Encrypt(Base.AspNet.Global.AESKey).ToBase64Url(true)
				+ "&rdr=" + UtilityService.GetRandomNumber().ToString().Encrypt(Base.AspNet.Global.AESKey).ToBase64Url(true);
		}

		internal static async Task<bool> ExistsAsync(this Session session)
		{
			var result = await Global.CallServiceAsync(session, "users", "session", "GET", null, null, null, new Dictionary<string, string>()
			{
				{ "Exist", "" }
			});
			var isExisted = result?["Existed"];
			return isExisted != null && isExisted is JValue && (isExisted as JValue).Value != null && (isExisted as JValue).Value.CastAs<bool>() == true;
		}
		#endregion

		#region Authorization
		internal static async Task<bool> CanUploadAsync(string serviceName, string systemID, string definitionID, string objectID)
		{
			var service = await Global.GetServiceAsync(serviceName);
			return await service.CanContributeAsync(HttpContext.Current.User as User, systemID, definitionID, objectID);
		}

		internal static async Task<bool> CanDownloadAsync(string serviceName, string systemID, string definitionID, string objectID)
		{
			var service = await Global.GetServiceAsync(serviceName);
			return await service.CanDownloadAsync(HttpContext.Current.User as User, systemID, definitionID, objectID);
		}

		internal static async Task<bool> CanDeleteAsync(string serviceName, string systemID, string definitionID, string objectID)
		{
			var service = await Global.GetServiceAsync(serviceName);
			return await service.CanEditAsync(HttpContext.Current.User as User, systemID, definitionID, objectID);
		}

		internal static async Task<bool> CanRestoreAsync(string serviceName, string systemID, string definitionID, string objectID)
		{
			var service = await Global.GetServiceAsync(serviceName);
			return await service.CanEditAsync(HttpContext.Current.User as User, systemID, definitionID, objectID);
		}
		#endregion

		#region Send & process inter-communicate message

		internal static async Task SendInterCommunicateMessageAsync(CommunicateMessage message)
		{
			try
			{
				await Base.AspNet.Global.RTUService.SendInterCommunicateMessageAsync(message, Base.AspNet.Global.CancellationTokenSource.Token);
			}
			catch { }
		}

		static void ProcessInterCommunicateMessage(CommunicateMessage message)
		{

		}
		#endregion

		#region Attachment information
		internal static async Task<Attachment> GetAttachmentAsync(string id, Session session = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return string.IsNullOrWhiteSpace(id)
				? null
				: (await Global.CallServiceAsync(new RequestInfo(session ?? Global.GetSession())
					{
						ServiceName = "files",
						ObjectName = "attachment", 
						Verb = "GET",
						Query = new Dictionary<string, string>() { { "object-identity", id } },
						CorrelationID = Base.AspNet.Global.GetCorrelationID()
					}, cancellationToken)
				 ).FromJson<Attachment>();
		}

		internal static bool IsReadable(this string mime)
		{
			return string.IsNullOrWhiteSpace(mime)
				? false
				: mime.IsStartsWith("image/") || mime.IsStartsWith("video/")
					|| mime.IsStartsWith("text/") || mime.IsStartsWith("application/x-shockwave-flash")
					|| mime.IsEquals("application/pdf") || mime.IsEquals("application/x-pdf");
		}

		internal static bool IsReadable(this Attachment @object)
		{
			return @object != null && @object.ContentType.IsReadable();
		}

		internal static bool IsReadable(this AttachmentInfo @object)
		{
			return @object.ContentType.IsReadable();
		}

		internal static async Task UpdateCounterAsync(HttpContext context, Attachment attachment)
		{
			context = context ?? HttpContext.Current;
			var session = Global.GetSession(context);
			await Task.Delay(0);
		}
		#endregion

		#region  Global settings of the app
		static string _UserAvatarFilesPath = null;

		internal static string UserAvatarFilesPath
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Global._UserAvatarFilesPath))
				{
					Global._UserAvatarFilesPath = UtilityService.GetAppSetting("UserAvatarFilesPath");
					if (string.IsNullOrWhiteSpace(Global._UserAvatarFilesPath))
						Global._UserAvatarFilesPath = HttpRuntime.AppDomainAppPath + @"\data-files\user-avatars";
					if (!Global._UserAvatarFilesPath.EndsWith(@"\"))
						Global._UserAvatarFilesPath += @"\";
				}
				return Global._UserAvatarFilesPath;
			}
		}

		static string _DefaultUserAvatarFilename = null;

		internal static string DefaultUserAvatarFilename
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Global._DefaultUserAvatarFilename))
					Global._DefaultUserAvatarFilename = UtilityService.GetAppSetting("DefaultUserAvatarFileName", "@default.png");
				return Global._DefaultUserAvatarFilename;
			}
		}

		static string _AttachmentFilesPath = null;

		internal static string AttachmentFilesPath
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Global._AttachmentFilesPath))
				{
					Global._AttachmentFilesPath = UtilityService.GetAppSetting("AttachmentFilesPath");
					if (string.IsNullOrWhiteSpace(Global._AttachmentFilesPath))
						Global._AttachmentFilesPath = HttpRuntime.AppDomainAppPath + @"\data-files\attachments";
					if (!Global._AttachmentFilesPath.EndsWith(@"\"))
						Global._AttachmentFilesPath += @"\";
				}
				return Global._AttachmentFilesPath;
			}
		}

		static string _UsersHttpUri = null;

		internal static string UsersHttpUri
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Global._UsersHttpUri))
					Global._UsersHttpUri = UtilityService.GetAppSetting("UsersHttpUri", "https://aid.vieapps.net");
				if (!Global._UsersHttpUri.EndsWith("/"))
					Global._UsersHttpUri += "/";
				return Global._UsersHttpUri;
			}
		}
		#endregion

	}

	// ------------------------------------------------------------------------------

	#region Global.ashx
	public class GlobalHandler : HttpTaskAsyncHandler
	{
		public GlobalHandler() : base() { }

		public override async Task ProcessRequestAsync(HttpContext context)
		{
			// stop process request is OPTIONS
			if (context.Request.HttpMethod.Equals("OPTIONS"))
				return;

			// authentication: process app token
			var appToken = context.Request.Headers["x-app-token"] ?? context.Request.QueryString["x-app-token"];
			if (!string.IsNullOrWhiteSpace(appToken))
				try
				{
					// parse token
					var info = User.ParseJSONWebToken(appToken, Base.AspNet.Global.AESKey, Base.AspNet.Global.GenerateJWTKey());
					var userID = info.Item1;
					var accessToken = info.Item2;
					var sessionID = info.Item3;

					// prepare session
					var session = Global.GetSession(context);
					session.SessionID = sessionID;
					context.Items["Session-ID"] = session.SessionID;
					context.Items["Device-ID"] = session.DeviceID;

					// prepare user
					session.User = User.ParseAccessToken(accessToken, Base.AspNet.Global.RSA, Base.AspNet.Global.AESKey);
					if (session.User.ID.Equals(userID) && await session.ExistsAsync())
						context.User = new UserPrincipal(session.User);
				}
				catch (Exception ex)
				{
					Base.AspNet.Global.WriteLogs("Error occurred while processing with authentication", ex);
				}

			// prepare
			var requestTo = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length);
			if (requestTo.StartsWith("/"))
				requestTo = requestTo.Right(requestTo.Length - 1);
			if (requestTo.IndexOf("?") > 0)
				requestTo = requestTo.Left(requestTo.IndexOf("?"));
			requestTo = string.IsNullOrEmpty(requestTo)
				? ""
				: requestTo.ToLower().ToArray('/', true).First();

			// static resources
			if (Global.StaticSegments.Contains(requestTo))
			{
				// check "If-Modified-Since" request to reduce traffict
				var eTag = "StaticResource#" + context.Request.RawUrl.ToLower().GetMD5();
				if (context.Request.Headers["If-Modified-Since"] != null && eTag.Equals(context.Request.Headers["If-None-Match"]))
				{
					context.Response.Cache.SetCacheability(HttpCacheability.Public);
					context.Response.StatusCode = (int)HttpStatusCode.NotModified;
					context.Response.StatusDescription = "Not Modified";
					context.Response.Headers.Add("ETag", "\"" + eTag + "\"");
					return;
				}

				// prepare
				var path = context.Request.RawUrl;
				if (path.IndexOf("?") > 0)
					path = path.Left(path.IndexOf("?"));

				try
				{
					// check exist
					var fileInfo = new FileInfo(context.Server.MapPath(path));
					if (!fileInfo.Exists)
						throw new FileNotFoundException();

					// set cache policy
					context.Response.Cache.SetCacheability(HttpCacheability.Public);
					context.Response.Cache.SetExpires(DateTime.Now.AddDays(1));
					context.Response.Cache.SetSlidingExpiration(true);
					context.Response.Cache.SetOmitVaryStar(true);
					context.Response.Cache.SetValidUntilExpires(true);
					context.Response.Cache.SetLastModified(fileInfo.LastWriteTime);
					context.Response.Cache.SetETag(eTag);

					// prepare content
					var staticMimeType = MimeMapping.GetMimeMapping(fileInfo.Name);
					if (string.IsNullOrWhiteSpace(staticMimeType))
						staticMimeType = "text/plain";
					var staticContent = await UtilityService.ReadTextFileAsync(fileInfo).ConfigureAwait(false);
					if (staticMimeType.IsEndsWith("json"))
						staticContent = JObject.Parse(staticContent).ToString(Newtonsoft.Json.Formatting.Indented);

					// write content
					context.Response.ContentType = staticMimeType;
					await context.Response.Output.WriteAsync(staticContent).ConfigureAwait(false);
				}
				catch (FileNotFoundException ex)
				{
					context.ShowError((int)HttpStatusCode.NotFound, "Not found [" + path + "]", "FileNotFoundException", ex.StackTrace);
				}
				catch (Exception ex)
				{
					context.ShowError(ex);
				}
			}

			// files
			else
			{
				// get the handler
				var type = Global.Handlers.ContainsKey(requestTo)
					? Global.Handlers[requestTo]
					: null;

				// do the process
				if (type != null)
					try
					{
						await (type.CreateInstance() as AbstractHttpHandler).ProcessRequestAsync(context, Base.AspNet.Global.CancellationTokenSource.Token).ConfigureAwait(false);
					}
					catch (OperationCanceledException) { }
					catch (Exception ex)
					{
						context.ShowError(ex);
					}
				else
					context.ShowError(404, "Not Found", "FileNotFoundException", null);
			}
		}
	}
	#endregion

	#region Global.asax
	public class GlobalApp : HttpApplication
	{

		protected void Application_Start(object sender, EventArgs args)
		{
			Global.OnAppStart(sender as HttpContext);
		}

		protected void Application_BeginRequest(object sender, EventArgs args)
		{
			Global.OnAppBeginRequest(sender as HttpApplication);
		}

		protected void Application_AuthenticateRequest(object sender, EventArgs args)
		{
			Global.OnAppAuthenticateRequest(sender as HttpApplication);
		}

		protected void Application_PreSendRequestHeaders(object sender, EventArgs args)
		{
			Global.OnAppPreSendHeaders(sender as HttpApplication);
		}

		protected void Application_EndRequest(object sender, EventArgs args)
		{
			Global.OnAppEndRequest(sender as HttpApplication);
		}

		protected void Application_Error(object sender, EventArgs args)
		{
			Global.OnAppError(sender as HttpApplication);
		}

		protected void Application_End(object sender, EventArgs args)
		{
			Global.OnAppEnd();
		}
	}
	#endregion

}