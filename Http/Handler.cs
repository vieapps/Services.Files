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
			// CORS: allow origin
			context.Response.Headers["Access-Control-Allow-Origin"] = "*";

			// CORS: options
			if (context.Request.Method.IsEquals("OPTIONS"))
			{
				var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "Access-Control-Allow-Methods", "HEAD,GET,POST,PUT,DELETE" }
				};
				if (context.Request.Headers.ContainsKey("Access-Control-Request-Headers"))
					headers["Access-Control-Allow-Headers"] = context.Request.Headers["Access-Control-Request-Headers"];
				context.SetResponseHeaders((int)HttpStatusCode.OK, headers, true);
			}

			// load balancing health check
			else if (context.Request.Path.Value.IsEquals("/load-balancing-health-check"))
				await context.WriteAsync("OK", "text/plain", null, 0, null, TimeSpan.Zero, null, Global.CancellationTokenSource.Token).ConfigureAwait(false);

			// requests of files
			else
			{
				// process
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
		}

		internal async Task ProcessRequestAsync(HttpContext context)
		{
			// prepare
			context.Items["PipelineStopwatch"] = Stopwatch.StartNew();
			var requestPath = context.GetRequestPathSegments(true).First();

			if (Global.IsVisitLogEnabled)
				await context.WriteVisitStartingLogAsync().ConfigureAwait(false);

			// request to favicon.ico file
			if (requestPath.IsEquals("favicon.ico"))
				await context.ProcessFavouritesIconFileRequestAsync().ConfigureAwait(false);

			// request to static segments
			if (Global.StaticSegments.Contains(requestPath))
				await context.ProcessStaticFileRequestAsync().ConfigureAwait(false);

			// request to files
			else
				await this.ProcessFileRequestAsync(context).ConfigureAwait(false);

			if (Global.IsVisitLogEnabled)
				await context.WriteVisitFinishingLogAsync().ConfigureAwait(false);
		}

		internal async Task ProcessFileRequestAsync(HttpContext context)
		{
			// prepare handler
			var requestPath = context.GetRequestPathSegments(true).First();
			if (!Handler.Handlers.TryGetValue(requestPath, out var type))
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
					await context.UpdateWithAuthenticateTokenAsync(session, authenticateToken, null, null, null, Global.Logger, "Http.Authenticate", context.GetCorrelationID()).ConfigureAwait(false);
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync(Global.Logger, "Http.Authenticate", $"Successfully authenticate a token {session.ToJson().ToString(Newtonsoft.Json.Formatting.Indented)}");
				}
				catch (Exception ex)
				{
					await context.WriteLogsAsync(Global.Logger, "Http.Authenticate", $"Failure authenticate a token: {ex.Message}", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
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
			Services.FileHandler handler = null;
			try
			{
				handler = type.CreateInstance() as Services.FileHandler;
				await handler.ProcessRequestAsync(context, Global.CancellationTokenSource.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				await context.WriteLogsAsync(handler?.Logger, $"Http.{(requestPath.IsStartsWith("Thumbnail") ? "Thumbnails" : requestPath)}", ex.Message, ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
			}
		}

		#region  Global settings & helpers
		static Dictionary<string, Type> Handlers { get; } = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
		{
			{ "files", typeof(FileHandler) },
			{ "avatars", typeof(AvatarHandler) },
			{ "captchas", typeof(CaptchaHandler) },
			{ "qrcodes", typeof(QRCodeHandler) },
			{ "downloads", typeof(DownloadHandler) },
			{ "thumbnails", typeof(ThumbnailHandler) },
			{ "thumbnailbigs", typeof(ThumbnailHandler) },
			{ "thumbnailpngs", typeof(ThumbnailHandler) },
			{ "thumbnailbigpngs", typeof(ThumbnailHandler) }
		};

		internal static void PrepareHandlers()
		{
			if (ConfigurationManager.GetSection(UtilityService.GetAppSetting("Section:Handlers",  "net.vieapps.services.files.http.handlers")) is AppConfigurationSectionHandler svcConfig)
				if (svcConfig.Section.SelectNodes("handler") is XmlNodeList handlers)
					handlers.ToList()
						.Where(handler => !string.IsNullOrWhiteSpace(handler.Attributes["key"].Value) && !Handler.Handlers.ContainsKey(handler.Attributes["key"].Value.ToLower()))
						.ForEach(handler =>
						{
							var type = Type.GetType(handler.Attributes["type"].Value);
							if (type == null)
								try
								{
									var typeInfo = handler.Attributes["type"].Value.ToArray();
									type = new AssemblyLoader(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"{typeInfo[1]}.dll")).Assembly.GetExportedTypes().FirstOrDefault(serviceType => typeInfo[0].Equals(serviceType.ToString()));
								}
								catch (Exception ex)
								{
									Global.Logger.LogError($"Cannot load a handler ({handler.Attributes["type"].Value})", ex);
								}
							if (type != null && type.CreateInstance() is Services.FileHandler)
								Handler.Handlers[handler.Attributes["key"].Value.ToLower()] = type;
						});

			Global.Logger.LogInformation($"Handlers:\r\n\t{Handler.Handlers.Select(kvp => $"{kvp.Key} => {kvp.Value.GetTypeName()}").ToString("\r\n\t")}");
		}

		static string _UserAvatarFilesPath = null, _DefaultUserAvatarFilePath = null, _AttachmentFilesPath = null, _TempFilesPath = null;

		internal static string UserAvatarFilesPath
			=> Handler._UserAvatarFilesPath ?? (Handler._UserAvatarFilesPath = UtilityService.GetAppSetting("Path:UserAvatars", Path.Combine(Global.RootPath, "data-files", "user-avatars")));

		internal static string DefaultUserAvatarFilePath
			=> Handler._DefaultUserAvatarFilePath ?? (Handler._DefaultUserAvatarFilePath = UtilityService.GetAppSetting("Path:DefaultUserAvatar", Path.Combine(Handler.UserAvatarFilesPath, "@default.png")));

		internal static string AttachmentFilesPath
			=> Handler._AttachmentFilesPath ?? (Handler._AttachmentFilesPath = UtilityService.GetAppSetting("Path:Attachments", Path.Combine(Global.RootPath, "data-files", "attachments")));

		internal static string TempFilesPath
			=> Handler._TempFilesPath ?? (Handler._TempFilesPath = UtilityService.GetAppSetting("Path:Temp", Path.Combine(Global.RootPath, "data-files", "temp")));
		#endregion

		#region Helper: API Gateway Router
		internal static void OpenRouterChannels(int waitingTimes = 6789)
		{
			Global.Logger.LogDebug($"Attempting to connect to API Gateway Router [{new Uri(Router.GetRouterStrInfo()).GetResolvedURI()}]");
			Global.OpenRouterChannels(
				(sender, arguments) =>
				{
					Global.Logger.LogDebug($"Incoming channel to API Gateway Router is established - Session ID: {arguments.SessionId}");
					Task.Run(() => Router.IncomingChannel.UpdateAsync(Router.IncomingChannelSessionID, Global.ServiceName, $"Incoming ({Global.ServiceName} HTTP service)")).ConfigureAwait(false);
					Global.PrimaryInterCommunicateMessageUpdater?.Dispose();
					Global.PrimaryInterCommunicateMessageUpdater = Router.IncomingChannel.RealmProxy.Services
						.GetSubject<CommunicateMessage>("messages.services.files")
						.Subscribe(
							async message =>
							{
								try
								{
									await Handler.ProcessInterCommunicateMessageAsync(message).ConfigureAwait(false);
								}
								catch (Exception ex)
								{
									await Global.WriteLogsAsync(Global.Logger, "Http.WebSockets", $"Error occurred while processing an inter-communicate message: {ex.Message} => {message?.ToJson().ToString(Global.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}", ex, Global.ServiceName).ConfigureAwait(false);
								}
							},
							async exception => await Global.WriteLogsAsync(Global.Logger, "Http.WebSockets", $"Error occurred while fetching an inter-communicate message: {exception.Message}", exception).ConfigureAwait(false)
						);
					Global.SecondaryInterCommunicateMessageUpdater?.Dispose();
					Global.SecondaryInterCommunicateMessageUpdater = Router.IncomingChannel.RealmProxy.Services
						.GetSubject<CommunicateMessage>("messages.services.apigateway")
						.Subscribe(
							async message =>
							{
								try
								{
									await Handler.ProcessAPIGatewayCommunicateMessageAsync(message).ConfigureAwait(false);
								}
								catch (Exception ex)
								{
									await Global.WriteLogsAsync(Global.Logger, "Http.WebSockets", $"Error occurred while processing an inter-communicate message of API Gateway: {ex.Message} => {message?.ToJson().ToString(Global.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}", ex, Global.ServiceName).ConfigureAwait(false);
								}
							},
							async exception => await Global.WriteLogsAsync(Global.Logger, "Http.WebSockets", $"Error occurred while fetching an inter-communicate message of API Gateway: {exception.Message}", exception).ConfigureAwait(false)
						);
				},
				(sender, arguments) =>
				{
					Global.Logger.LogDebug($"Outgoing channel to API Gateway Router is established - Session ID: {arguments.SessionId}");
					Task.Run(async () =>
					{
						await Router.OutgoingChannel.UpdateAsync(Router.OutgoingChannelSessionID, Global.ServiceName, $"Outgoing ({Global.ServiceName} HTTP service)").ConfigureAwait(false);
						try
						{
							await Task.WhenAll(
								Global.InitializeLoggingServiceAsync(),
								Global.InitializeRTUServiceAsync()
							).ConfigureAwait(false);
							Global.Logger.LogInformation("Helper services are succesfully initialized");
							while (Router.IncomingChannel == null || Router.OutgoingChannel == null)
								await Task.Delay(UtilityService.GetRandomNumber(234, 567), Global.CancellationTokenSource.Token).ConfigureAwait(false);
						}
						catch (Exception ex)
						{
							Global.Logger.LogError($"Error occurred while initializing helper services: {ex.Message}", ex);
						}
					})
					.ContinueWith(async _ => await Global.RegisterServiceAsync("Http.WebSockets").ConfigureAwait(false), TaskContinuationOptions.OnlyOnRanToCompletion)
					.ConfigureAwait(false);
				},
				waitingTimes
			);
		}

		internal static void CloseRouterChannels(int waitingTimes = 1234)
		{
			Global.UnregisterService("Http.WebSockets", waitingTimes);
			Global.PrimaryInterCommunicateMessageUpdater?.Dispose();
			Global.SecondaryInterCommunicateMessageUpdater?.Dispose();
			Router.CloseChannels();
		}

		static Task ProcessInterCommunicateMessageAsync(CommunicateMessage message)
		{
			if (message.Type.IsEquals("Thumbnail#Delete") || message.Type.IsEquals("Attachment#Delete"))
				new AttachmentInfo
				{
					IsThumbnail = message.Type.IsEquals("Thumbnail#Delete")
				}.Fill(message.Data).MoveFileIntoTrash(Global.Logger, "Http.Sync");

			else if (message.Type.IsEquals("Thumbnail#Move") || message.Type.IsEquals("Attachment#Move"))
				new AttachmentInfo
				{
					IsThumbnail = message.Type.IsEquals("Thumbnail#Move")
				}.Fill(message.Data).MoveFile(Global.Logger, "Http.Sync");

			return Task.CompletedTask;
		}

		static async Task ProcessAPIGatewayCommunicateMessageAsync(CommunicateMessage message)
		{
			if (message.Type.IsEquals("Service#RequestInfo"))
				await Global.UpdateServiceInfoAsync("Http.WebSockets").ConfigureAwait(false);
		}
		#endregion

	}

	#region Attachment Info
	[Serializable]
	public struct AttachmentInfo
	{
		public string ID { get; set; }

		public string ServiceName { get; set; }

		public string SystemID { get; set; }

		public string DefinitionID { get; set; }

		public string ObjectID { get; set; }

		public string Filename { get; set; }

		public long Size { get; set; }

		public string ContentType { get; set; }

		public bool IsTemporary { get; set; }

		public bool IsShared { get; set; }

		public bool IsTracked { get; set; }

		public string Title { get; set; }

		public string Description { get; set; }

		public bool IsThumbnail { get; set; }
	}
	#endregion

	#region Extensions
	internal static class HandlerExtensions
	{
		public static bool IsReadable(this AttachmentInfo attachmentInfo)
			=> string.IsNullOrWhiteSpace(attachmentInfo.ContentType)
				? false
				: attachmentInfo.ContentType.IsStartsWith("image/") || attachmentInfo.ContentType.IsStartsWith("text/")
					|| attachmentInfo.ContentType.IsStartsWith("audio/") || attachmentInfo.ContentType.IsStartsWith("video/")
					|| attachmentInfo.ContentType.IsEquals("application/pdf") || attachmentInfo.ContentType.IsEquals("application/x-pdf")
					|| attachmentInfo.ContentType.IsStartsWith("application/x-shockwave-flash");

		public static string GetFileName(this AttachmentInfo attachmentInfo)
			=> (attachmentInfo.IsThumbnail ? "" : $"{attachmentInfo.ID}-") + attachmentInfo.Filename;

		public static string GetFilePath(this AttachmentInfo attachmentInfo, bool isTemporary = false)
			=> attachmentInfo.IsTemporary || isTemporary
				? Path.Combine(Handler.TempFilesPath, attachmentInfo.GetFileName())
				: Path.Combine(Handler.AttachmentFilesPath, string.IsNullOrWhiteSpace(attachmentInfo.SystemID) || !attachmentInfo.SystemID.IsValidUUID() ? attachmentInfo.ServiceName.ToLower() : attachmentInfo.SystemID.ToLower(), attachmentInfo.GetFileName());

		public static AttachmentInfo DeleteFile(this AttachmentInfo attachmentInfo, ILogger logger = null, string objectName = null)
		{
			var filePath = attachmentInfo.GetFilePath();
			if (File.Exists(filePath))
				try
				{
					File.Delete(filePath);
					if (Global.IsDebugLogEnabled)
						Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Http.Uploads", $"Successfully delete a file [{filePath}]");
				}
				catch (Exception e)
				{
					Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Http.Uploads", $"Error occurred while deleting a file => {e.Message}", e, Global.ServiceName, LogLevel.Error);
				}
			return attachmentInfo;
		}

		public static AttachmentInfo MoveFile(this AttachmentInfo attachmentInfo, ILogger logger = null, string objectName = null)
		{
			var from = attachmentInfo.GetFilePath(true);
			if (File.Exists(from))
				try
				{
					var to = attachmentInfo.MoveFileIntoTrash(logger, objectName).GetFilePath();
					File.Move(from, to);
					if (Global.IsDebugLogEnabled)
						Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Http.Uploads", $"Successfully move a file [{from} => {to}]");
				}
				catch (Exception e)
				{
					Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Http.Uploads", $"Error occurred while moving a file => {e.Message}", e, Global.ServiceName, LogLevel.Error);
				}
			return attachmentInfo;
		}

		public static AttachmentInfo MoveFileIntoTrash(this AttachmentInfo attachmentInfo, ILogger logger = null, string objectName = null)
		{
			var filePath = attachmentInfo.GetFilePath();
			if (!File.Exists(filePath))
				return attachmentInfo;

			try
			{
				var trashPath = Path.Combine(Handler.AttachmentFilesPath, string.IsNullOrWhiteSpace(attachmentInfo.SystemID) || !attachmentInfo.SystemID.IsValidUUID() ? attachmentInfo.ServiceName.ToLower() : attachmentInfo.SystemID.ToLower(), "trash", attachmentInfo.GetFileName());
				if (File.Exists(trashPath))
					File.Delete(trashPath);
				File.Move(filePath, trashPath);
				if (Global.IsDebugLogEnabled)
					Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Http.Uploads", $"Successfully move a file into trash [{filePath} => {trashPath}]");
				return attachmentInfo;
			}
			catch (Exception ex)
			{
				Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Http.Uploads", $"Error occurred while moving a file into trash => {ex.Message}", ex, Global.ServiceName, LogLevel.Error);
				return attachmentInfo.DeleteFile(logger, objectName);
			}
		}

		public static AttachmentInfo PrepareDirectories(this AttachmentInfo attachmentInfo)
		{
			var path = Path.Combine(Handler.AttachmentFilesPath, string.IsNullOrWhiteSpace(attachmentInfo.SystemID) || !attachmentInfo.SystemID.IsValidUUID() ? attachmentInfo.ServiceName.ToLower() : attachmentInfo.SystemID.ToLower());
			new[] { path, Path.Combine(path, "trash") }.ForEach(directory =>
			{
				if (!Directory.Exists(directory))
					Directory.CreateDirectory(directory);
			});
			return attachmentInfo;
		}

		public static AttachmentInfo Fill(this AttachmentInfo attachmentInfo, JToken json)
		{
			if (json != null)
			{
				attachmentInfo.ID = json.Get<string>("ID");
				attachmentInfo.ServiceName = json.Get<string>("ServiceName");
				attachmentInfo.SystemID = json.Get<string>("SystemID");
				attachmentInfo.DefinitionID = json.Get<string>("DefinitionID");
				attachmentInfo.ObjectID = json.Get<string>("ObjectID");
				attachmentInfo.Filename = json.Get<string>("Filename");
				attachmentInfo.Size = json.Get<long>("Size");
				attachmentInfo.ContentType = json.Get<string>("ContentType");
				attachmentInfo.IsTemporary = json.Get<bool>("IsTemporary");
				if (!attachmentInfo.IsThumbnail)
				{
					attachmentInfo.IsShared = json.Get<bool>("IsShared");
					attachmentInfo.IsTracked = json.Get<bool>("IsTracked");
					attachmentInfo.Title = json.Get<string>("Title");
					attachmentInfo.Description = json.Get<string>("Description");
				}
			}
			return attachmentInfo;
		}

		public static Task<JToken> CreateAsync(this HttpContext context, AttachmentInfo attachmentInfo, string objectName, CancellationToken cancellationToken = default(CancellationToken))
		{
			var session = context.GetSession();
			var body = new JObject
			{
				{ "ID", attachmentInfo.ID },
				{ "ServiceName", attachmentInfo.ServiceName },
				{ "SystemID", attachmentInfo.SystemID },
				{ "DefinitionID", attachmentInfo.DefinitionID },
				{ "ObjectID", attachmentInfo.ObjectID },
				{ "Size", attachmentInfo.Size },
				{ "Filename", attachmentInfo.Filename },
				{ "ContentType", attachmentInfo.ContentType },
				{ "IsTemporary", attachmentInfo.IsTemporary },
				{ "IsShared", attachmentInfo.IsShared },
				{ "IsTracked", attachmentInfo.IsTracked },
				{ "Title", attachmentInfo.Title },
				{ "Description", attachmentInfo.Description }
			}.ToString(Newtonsoft.Json.Formatting.None);
			return context.CallServiceAsync(new RequestInfo(session, "Files", attachmentInfo.IsThumbnail ? "Thumbnail" : "Attachment", "POST")
			{
				Query = new Dictionary<string, string>
				{
					{ "object-identity", attachmentInfo.ID },
					{ "x-object-title", attachmentInfo.Title },
					{ "x-object-name", objectName }
				},
				Body = body,
				Extra = new Dictionary<string, string>
				{
					{ "Signature", body.GetHMACSHA256(Global.ValidationKey) },
					{ "SessionID", session.SessionID.GetHMACBLAKE256(Global.ValidationKey) }
				},
				CorrelationID = context.GetCorrelationID()
			}, cancellationToken, Global.Logger, "Http.Uploads");
		}

		public static async Task<AttachmentInfo> GetAsync(this HttpContext context, string id, CancellationToken cancellationToken = default(CancellationToken))
			=> new AttachmentInfo
			{
				IsThumbnail = false
			}.Fill(string.IsNullOrWhiteSpace(id) ? null : await context.CallServiceAsync(new RequestInfo(context.GetSession(), "Files", "Attachment", "GET")
			{
				Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ "object-identity", id }
				},
				CorrelationID = context.GetCorrelationID()
			}, cancellationToken, Global.Logger, "Http.Downloads").ConfigureAwait(false));

		public static Task<bool> CanDownloadAsync(this HttpContext context, AttachmentInfo attachmentInfo)
			=> attachmentInfo.IsThumbnail
				? Task.FromResult(true)
				: context.CanDownloadAsync(attachmentInfo.ServiceName, attachmentInfo.SystemID, attachmentInfo.DefinitionID, attachmentInfo.ObjectID);

		public static Task UpdateAsync(this HttpContext context, AttachmentInfo attachmentInfo, CancellationToken cancellationToken = default(CancellationToken))
			=> attachmentInfo.IsThumbnail || attachmentInfo.IsTemporary || string.IsNullOrWhiteSpace(attachmentInfo.ID)
				? Task.CompletedTask
				: Task.WhenAll(
					context.CallServiceAsync(new RequestInfo(context.GetSession(), "Files", "Attachment", "GET")
					{
						Query = new Dictionary<string, string>
						{
							{ "object-identity", "counters" },
							{ "x-object-id", attachmentInfo.ID },
							{ "x-user-id", context.User.Identity.Name }
						},
						CorrelationID = context.GetCorrelationID()
					}, cancellationToken, Global.Logger, "Http.Downloads"),
					attachmentInfo.IsTracked
						? context.CallServiceAsync(new RequestInfo(context.GetSession(), "Files", "Attachment", "GET")
							{
								Query = new Dictionary<string, string>
								{
									{ "object-identity", "trackers" },
									{ "x-object-id", attachmentInfo.ID },
									{ "x-user-id", context.User.Identity.Name },
									{ "x-refer", context.GetReferUrl() },
									{ "x-origin", context.GetOriginUri()?.ToString() }
								},
								CorrelationID = context.GetCorrelationID()
							}, cancellationToken, Global.Logger, "Http.Downloads")
						: Task.CompletedTask);
		}
		#endregion

}