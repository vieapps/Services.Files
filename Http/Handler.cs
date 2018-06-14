#region Related components
using System;
using System.Net;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Configuration;
using System.Xml;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Caching;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class Handler
	{
		RequestDelegate Next { get; }

		public Handler(RequestDelegate next) => this.Next = next;

		public async Task Invoke(HttpContext context)
		{
			// allow origin
			context.Response.Headers["Access-Control-Allow-Origin"] = "*";

			// request with OPTIONS verb
			if (context.Request.Method.IsEquals("OPTIONS"))
			{
				var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "Access-Control-Allow-Methods", "GET,POST,PUT,DELETE" }
				};
				if (context.Request.Headers.ContainsKey("Access-Control-Request-Headers"))
					headers["Access-Control-Allow-Headers"] = context.Request.Headers["Access-Control-Request-Headers"];
				context.SetResponseHeaders((int)HttpStatusCode.OK, headers, true);
			}

			// request with other verbs
			else
				await this.ProcessRequestAsync(context).ConfigureAwait(false);

			// invoke next middleware
			try
			{
				await this.Next.Invoke(context).ConfigureAwait(false);
			}
			catch (InvalidOperationException) { }
			catch (Exception ex)
			{
				Global.Logger.LogCritical($"Error occurred while invoking the next middleware: {ex.Message}", ex);
			}
		}

		internal async Task ProcessRequestAsync(HttpContext context)
		{
			// prepare
			context.Items["PipelineStopwatch"] = Stopwatch.StartNew();
			var requestPath = context.GetRequestPathSegments(true).First();

			// request to favicon.ico file
			if (requestPath.IsEquals("favicon.ico"))
			{
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
				return;
			}

			// request to static segments
			else if (Global.StaticSegments.Contains(requestPath))
			{
				await context.ProcessStaticFileRequestAsync().ConfigureAwait(false);
				return;
			}

			// request  to files
			if (!Handler.Handlers.TryGetValue(requestPath, out Type type))
			{
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
				return;
			}

			// get session
			var session = context.GetSession();

			// get authenticate token
			var header = context.Request.Headers.ToDictionary();
			var query = context.GetRequestUri().ParseQuery();

			var authenticateToken = context.GetParameter("x-app-token") ?? context.GetParameter("x-passport-token");
			if (string.IsNullOrWhiteSpace(authenticateToken)) // Bearer token
			{
				authenticateToken = context.GetHeaderParameter("authorization");
				authenticateToken = authenticateToken != null && authenticateToken.IsStartsWith("Bearer") ? authenticateToken.ToArray(" ").Last() : null;
			}

			// no authenticate tokenn
			if (string.IsNullOrWhiteSpace(authenticateToken))
				session.SessionID = session.User.SessionID = UtilityService.NewUUID;

			// authenticate with token
			else
				try
				{
					await context.UpdateWithAuthenticateTokenAsync(session, authenticateToken).ConfigureAwait(false);
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync("Authenticate", $"Successfully authenticate a token {session.ToJson().ToString(Newtonsoft.Json.Formatting.Indented)}");
				}
				catch (Exception ex)
				{
					await context.WriteLogsAsync("Authenticate", $"Failure authenticate a token: {ex.Message}", ex);
				}

			// store session
			var appName = context.GetParameter("x-app-name");
			if (!string.IsNullOrWhiteSpace(appName))
				session.AppName = appName;

			var appPlatform = context.GetParameter("x-app-platform");
			if (!string.IsNullOrWhiteSpace(appPlatform))
				session.AppPlatform = appPlatform;

			var deviceID = context.GetParameter("x-device-id");
			if (!string.IsNullOrWhiteSpace(deviceID))
				session.DeviceID = deviceID;

			context.Items["Session"] = session;

			// sign-in with authenticate token of passport
			if (session.User.IsAuthenticated && context.GetParameter("x-passport-token") != null)
				await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new UserPrincipal(session.User), new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);

			// or just update user information
			else
				context.User = new UserPrincipal(session.User);

			// process the request to files
			try
			{
				await (type.CreateInstance() as Services.FileHandler).ProcessRequestAsync(context, Global.CancellationTokenSource.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				await Global.WriteLogsAsync(requestPath, ex.Message, ex);
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
			}
		}

		#region  Global settings & helpers
		internal static Dictionary<string, Type> Handlers { get; private set; } = new Dictionary<string, Type>();

		internal static void PrepareHandlers()
		{
			Handler.Handlers = new Dictionary<string, Type>()
			{
				{ "avatars", Type.GetType("net.vieapps.Services.Files.AvatarHandler,VIEApps.Services.Files.Http") },
				{ "captchas", Type.GetType("net.vieapps.Services.Files.CaptchaHandler,VIEApps.Services.Files.Http") },
				{ "qrcodes", Type.GetType("net.vieapps.Services.Files.QRCodeHandler,VIEApps.Services.Files.Http") },
				{ "thumbnails", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler,VIEApps.Services.Files.Http") },
				{ "thumbnailbigs", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler,VIEApps.Services.Files.Http") },
				{ "thumbnailpngs", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler,VIEApps.Services.Files.Http") },
				{ "thumbnailbigpngs", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler,VIEApps.Services.Files.Http") },
				{ "files", Type.GetType("net.vieapps.Services.Files.FileHandler,VIEApps.Services.Files.Http") },
				{ "downloads", Type.GetType("net.vieapps.Services.Files.DownloadHandler,VIEApps.Services.Files.Http") }
			};

			// additional handlers
			if (ConfigurationManager.GetSection("net.vieapps.files.handlers") is AppConfigurationSectionHandler config)
				if (config.Section.SelectNodes("handler") is XmlNodeList nodes)
					nodes.ToList()
						.Where(node => !string.IsNullOrWhiteSpace(node.Attributes["key"].Value) && !Handler.Handlers.ContainsKey(node.Attributes["key"].Value))
						.ForEach(node =>
						{
							var type = Type.GetType(node.Attributes["type"].Value);
							if (type != null && type.CreateInstance() is Services.FileHandler)
								Handler.Handlers[node.Attributes["key"].Value] = type;
						});
		}

		static string _UserAvatarFilesPath = null;

		internal static string UserAvatarFilesPath => Handler._UserAvatarFilesPath ?? (Handler._UserAvatarFilesPath = UtilityService.GetAppSetting("Path:UserAvatars", Path.Combine(Global.RootPath, "data-files", "user-avatars")));

		static string _DefaultUserAvatarFilePath = null;

		internal static string DefaultUserAvatarFilePath => Handler._DefaultUserAvatarFilePath ?? (Handler._DefaultUserAvatarFilePath = UtilityService.GetAppSetting("Path:DefaultUserAvatar", Path.Combine(Handler.UserAvatarFilesPath, "@default.png")));

		static string _AttachmentFilesPath = null;

		internal static string AttachmentFilesPath => Handler._AttachmentFilesPath ?? (Handler._AttachmentFilesPath = UtilityService.GetAppSetting("Path:Attachments", Path.Combine(Global.RootPath, "data-files", "attachments")));

		internal static async Task<Attachment> GetAttachmentAsync(string id, Session session = null, CancellationToken cancellationToken = default(CancellationToken))
			=> string.IsNullOrWhiteSpace(id)
				? null
				: (await Global.CallServiceAsync(new RequestInfo(session ?? Global.GetSession())
					{
						ServiceName = "files",
						ObjectName = "attachment",
						Verb = "GET",
						Query = new Dictionary<string, string>
						{
							{ "object-identity", id }
						},
						CorrelationID = Global.GetCorrelationID()
					}, cancellationToken)
				 ).FromJson<Attachment>();
		#endregion

		#region Helper: WAMP connections & real-time updaters
		internal static void OpenWAMPChannels(int waitingTimes = 6789)
		{
			Global.Logger.LogInformation($"Attempting to connect to WAMP router [{WAMPConnections.GetRouterStrInfo()}]");
			Global.OpenWAMPChannels(
				(sender, args) =>
				{
					Global.Logger.LogInformation($"Incoming channel to WAMP router is established - Session ID: {args.SessionId}");
					WAMPConnections.IncomingChannel.Update(WAMPConnections.IncomingChannelSessionID, Global.ServiceName, $"Incoming ({Global.ServiceName} HTTP service)");
					Global.InterCommunicateMessageUpdater?.Dispose();
					Global.InterCommunicateMessageUpdater = WAMPConnections.IncomingChannel.RealmProxy.Services
						.GetSubject<CommunicateMessage>("net.vieapps.rtu.communicate.messages.files")
						.Subscribe(
							async (message) =>
							{
								var correlationID = UtilityService.NewUUID;
								try
								{
									await Handler.ProcessInterCommunicateMessageAsync(message).ConfigureAwait(false);
									if (Global.IsDebugResultsEnabled)
										await Global.WriteLogsAsync(Global.Logger, "RTU",
											$"Successfully process an inter-communicate message" + "\r\n" +
											$"- Type: {message?.Type}" + "\r\n" +
											$"- Message: {message?.Data?.ToString(Global.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}"
										, null, Global.ServiceName, LogLevel.Information, correlationID).ConfigureAwait(false);
								}
								catch (Exception ex)
								{
									await Global.WriteLogsAsync(Global.Logger, "RTU", $"{ex.Message} => {message?.ToJson().ToString(Global.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}", ex, Global.ServiceName, LogLevel.Error, correlationID).ConfigureAwait(false);
								}
							},
							exception => Global.WriteLogs(Global.Logger, "RTU", $"{exception.Message}", exception)
						);
				},
				(sender, args) =>
				{
					Global.Logger.LogInformation($"Outgoing channel to WAMP router is established - Session ID: {args.SessionId}");
					WAMPConnections.OutgoingChannel.Update(WAMPConnections.OutgoingChannelSessionID, Global.ServiceName, $"Outgoing ({Global.ServiceName} HTTP service)");
					try
					{
						Task.WaitAll(new[] { Global.InitializeLoggingServiceAsync(), Global.InitializeRTUServiceAsync() }, waitingTimes > 0 ? waitingTimes : 6789, Global.CancellationTokenSource.Token);
						Global.Logger.LogInformation("Helper services are succesfully initialized");
					}
					catch (Exception ex)
					{
						Global.Logger.LogError($"Error occurred while initializing helper services: {ex.Message}", ex);
					}
				},
				waitingTimes
			);
		}

		static Task ProcessInterCommunicateMessageAsync(CommunicateMessage message) => Task.CompletedTask;
		#endregion

	}

	#region Extensions
	public static class HandlerExtensions
	{
		internal static bool IsReadable(this string mime)
			=> string.IsNullOrWhiteSpace(mime)
				? false
				: mime.IsStartsWith("image/") || mime.IsStartsWith("video/")
					|| mime.IsStartsWith("text/") || mime.IsStartsWith("application/x-shockwave-flash")
					|| mime.IsEquals("application/pdf") || mime.IsEquals("application/x-pdf");

		internal static bool IsReadable(this Attachment @object) => @object != null && @object.ContentType.IsReadable();

		internal static bool IsReadable(this AttachmentInfo @object) => @object.ContentType.IsReadable();

		internal static async Task UpdateCounterAsync(this HttpContext context, Attachment attachment)
		{
			var session = context.GetSession();
			await Task.Delay(0);
		}
	}
	#endregion

}