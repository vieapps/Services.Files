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

			// request to favicon.ico file
			var requestPath = context.GetRequestPathSegments().First().ToLower();
			if (requestPath.IsEquals("favicon.ico"))
			{
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
				return;
			}

			// request to static segments
			else if (Global.StaticSegments.Contains(requestPath))
			{
				await this.ProcessStaticRequestAsync(context).ConfigureAwait(false);
				return;
			}

			// request  to filess
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
				await (type.CreateInstance() as FileHttpHandler).ProcessRequestAsync(context, Global.CancellationTokenSource.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				await Global.WriteLogsAsync(requestPath, ex.Message, ex);
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
			}
		}

		internal async Task ProcessStaticRequestAsync(HttpContext context)
		{
			var requestUri = context.GetRequestUri();
			try
			{
				// prepare
				FileInfo fileInfo = null;
				var pathSegments = requestUri.GetRequestPathSegments();

				var filePath = (pathSegments[0].IsEquals("statics")
					? UtilityService.GetAppSetting("Path:StaticFiles", Global.RootPath + "/data-files/statics")
					: Global.RootPath) + ("/" + string.Join("/", pathSegments)).Replace("//", "/").Replace(@"\", "/").Replace('/', Path.DirectorySeparatorChar);
				if (pathSegments[0].IsEquals("statics"))
					filePath = filePath.Replace($"{Path.DirectorySeparatorChar}statics{Path.DirectorySeparatorChar}statics{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}statics{Path.DirectorySeparatorChar}");

				// headers to reduce traffic
				var eTag = "Static#" + $"{requestUri}".ToLower().GenerateUUID();
				if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")))
				{
					var isNotModified = true;
					var lastModifed = DateTime.Now.ToUnixTimestamp();
					if (context.GetHeaderParameter("If-Modified-Since") != null)
					{
						fileInfo = new FileInfo(filePath);
						if (fileInfo.Exists)
						{
							lastModifed = fileInfo.LastWriteTime.ToUnixTimestamp();
							isNotModified = lastModifed <= context.GetHeaderParameter("If-Modified-Since").FromHttpDateTime().ToUnixTimestamp();
						}
						else
							isNotModified = false;
					}
					if (isNotModified)
					{
						context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, lastModifed, "public", context.GetCorrelationID());
						if (Global.IsDebugLogEnabled)
							await context.WriteLogsAsync("StaticFiles", $"Success response with status code 304 to reduce traffic ({filePath})").ConfigureAwait(false);
						return;
					}
				}

				// check existed
				fileInfo = fileInfo ?? new FileInfo(filePath);
				if (!fileInfo.Exists)
					throw new FileNotFoundException($"Not Found [{requestUri}]");

				// prepare body
				var fileMimeType = fileInfo.GetMimeType();
				var fileContent = fileMimeType.IsEndsWith("json")
					? JObject.Parse(await UtilityService.ReadTextFileAsync(fileInfo, null, Global.CancellationTokenSource.Token).ConfigureAwait(false)).ToString(Newtonsoft.Json.Formatting.Indented).ToBytes()
					: await UtilityService.ReadBinaryFileAsync(fileInfo, Global.CancellationTokenSource.Token).ConfigureAwait(false);

				// response
				context.SetResponseHeaders((int)HttpStatusCode.OK, new Dictionary<string, string>
				{
					{ "Content-Type", $"{fileMimeType}; charset=utf-8" },
					{ "ETag", eTag },
					{ "Last-Modified", $"{fileInfo.LastWriteTime.ToHttpString()}" },
					{ "Cache-Control", "public" },
					{ "Expires", $"{DateTime.Now.AddDays(7).ToHttpString()}" },
					{ "X-CorrelationID", context.GetCorrelationID() }
				});
				await Task.WhenAll(
					context.WriteAsync(fileContent, Global.CancellationTokenSource.Token),
					!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync("StaticFiles", $"Success response ({filePath} - {fileInfo.Length:#,##0} bytes)")
				).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await context.WriteLogsAsync("StaticFiles", $"Failure response [{requestUri}]", ex).ConfigureAwait(false);
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
							if (type != null && type.CreateInstance() is FileHttpHandler)
								Handler.Handlers[node.Attributes["key"].Value] = type;
						});
		}

		static string _UserAvatarFilesPath = null;

		internal static string UserAvatarFilesPath => Handler._UserAvatarFilesPath ?? (Handler._UserAvatarFilesPath = UtilityService.GetAppSetting("Path:UserAvatars", Path.Combine(Global.RootPath, "data-files", "user-avatars")));

		static string _DefaultUserAvatarFilePath = null;

		internal static string DefaultUserAvatarFilePath => Handler._DefaultUserAvatarFilePath ?? (Handler._DefaultUserAvatarFilePath = UtilityService.GetAppSetting("Path:DefaultUserAvatar", Path.Combine(Handler.UserAvatarFilesPath, "@default.png")));

		static string _AttachmentFilesPath = null;

		internal static string AttachmentFilesPath => Handler._AttachmentFilesPath ?? (Handler._AttachmentFilesPath = UtilityService.GetAppSetting("Path:Attachments", Path.Combine(Global.RootPath, "data-files", "attachments")));

		static string _UsersHttpUri = null;

		internal static string UsersHttpUri
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Handler._UsersHttpUri))
					Handler._UsersHttpUri = UtilityService.GetAppSetting("HttpUri:Users", "https://aid.vieapps.net");
				if (!Handler._UsersHttpUri.EndsWith("/"))
					Handler._UsersHttpUri += "/";
				return Handler._UsersHttpUri;
			}
		}

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
					Global.Logger.LogInformation($"Incomming channel to WAMP router is established - Session ID: {args.SessionId}");
					Global.InterCommunicateMessageUpdater?.Dispose();
					Global.InterCommunicateMessageUpdater = WAMPConnections.IncommingChannel.RealmProxy.Services
						.GetSubject<CommunicateMessage>("net.vieapps.rtu.communicate.messages.files")
						.Subscribe(
							async (message) =>
							{
								try
								{
									await Handler.ProcessInterCommunicateMessageAsync(message).ConfigureAwait(false);
									if (Global.IsDebugLogEnabled)
										await Global.WriteLogsAsync(Global.Logger, "RTU", $"Process an inter-communicate message successful {message?.ToJson().ToString(Newtonsoft.Json.Formatting.Indented)}").ConfigureAwait(false);
								}
								catch (Exception ex)
								{
									await Global.WriteLogsAsync(Global.Logger, "RTU", $"{ex.Message} => {message?.ToJson().ToString(Global.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}", ex).ConfigureAwait(false);
								}
							},
							exception => Global.WriteLogs(Global.Logger, "RTU", $"{exception.Message}", exception)
						);
				},
				(sender, args) =>
				{
					Global.Logger.LogInformation($"Outgoing channel to WAMP router is established - Session ID: {args.SessionId}");
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

		internal static string GetTransferToPassportUrl(this HttpContext context, string url = null)
		{
			url = url ?? $"{context.GetRequestUri()}";
			return Handler.UsersHttpUri + "validator"
				+ "?aut=" + (UtilityService.NewUUID.Left(5) + "-" + (context.User.Identity.IsAuthenticated ? "ON" : "OFF")).Encrypt(Global.EncryptionKey).ToBase64Url(true)
				+ "&uid=" + (context.User.Identity.IsAuthenticated ? (context.User.Identity as UserIdentity).ID : "").Encrypt(Global.EncryptionKey).ToBase64Url(true)
				+ "&uri=" + url.Encrypt(Global.EncryptionKey).ToBase64Url(true)
				+ "&rdr=" + UtilityService.GetRandomNumber().ToString().Encrypt(Global.EncryptionKey).ToBase64Url(true);
		}
	}
	#endregion

}