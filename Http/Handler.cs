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
using WampSharp.V2.Core.Contracts;
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
					["Access-Control-Allow-Methods"] = "HEAD,GET,POST,PUT,DELETE"
				};
				if (context.Request.Headers.TryGetValue("Access-Control-Request-Headers", out var requestHeaders))
					headers["Access-Control-Allow-Headers"] = requestHeaders;
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

		async Task ProcessRequestAsync(HttpContext context)
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
			else if (Global.StaticSegments.Contains(requestPath))
				await context.ProcessStaticFileRequestAsync().ConfigureAwait(false);

			// request to files
			else
				await this.ProcessFileRequestAsync(context).ConfigureAwait(false);

			if (Global.IsVisitLogEnabled)
				await context.WriteVisitFinishingLogAsync().ConfigureAwait(false);
		}

		async Task ProcessFileRequestAsync(HttpContext context)
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

			// no authenticate token
			if (string.IsNullOrWhiteSpace(authenticateToken))
				session.SessionID = session.User.SessionID = UtilityService.NewUUID;

			// authenticate with token
			else
				try
				{
					await context.UpdateWithAuthenticateTokenAsync(session, authenticateToken, null, null, null, Global.Logger, "Http.Authentications", context.GetCorrelationID()).ConfigureAwait(false);
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync(Global.Logger, "Http.Authentications", $"Successfully authenticate a token {session.ToJson().ToString(Newtonsoft.Json.Formatting.Indented)}");
				}
				catch (Exception ex)
				{
					await context.WriteLogsAsync(Global.Logger, "Http.Authentications", $"Failure authenticate a token: {ex.Message}", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
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

			// process the request
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
			if (ConfigurationManager.GetSection(UtilityService.GetAppSetting("Section:Handlers", "net.vieapps.services.files.http.handlers")) is AppConfigurationSectionHandler svcConfig)
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

		internal static string NodeName => Extensions.GetUniqueName(Global.ServiceName + ".http");
		#endregion

		#region API Gateway Router
		internal static void OpenRouterChannels(int waitingTimes = 6789)
		{
			Global.Logger.LogDebug($"Attempting to connect to API Gateway Router [{new Uri(Router.GetRouterStrInfo()).GetResolvedURI()}]");
			Global.OpenRouterChannels(
				(sender, arguments) =>
				{
					Global.Logger.LogDebug($"Incoming channel to API Gateway Router is established - Session ID: {arguments.SessionId}");
					Task.Run(async () =>
					{
						await Router.IncomingChannel.UpdateAsync(Router.IncomingChannelSessionID, Global.ServiceName, $"Incoming ({Global.ServiceName} HTTP service)").ConfigureAwait(false);
						await Handler.RegisterSynchronizerAsync().ConfigureAwait(false);
					}).ConfigureAwait(false);
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
						}
						catch (Exception ex)
						{
							Global.Logger.LogError($"Error occurred while initializing helper services: {ex.Message}", ex);
						}
					})
					.ContinueWith(async _ =>
					{
						while (Router.IncomingChannel == null || Router.OutgoingChannel == null)
							await Task.Delay(UtilityService.GetRandomNumber(234, 567), Global.CancellationTokenSource.Token).ConfigureAwait(false);
						await Global.RegisterServiceAsync("Http.WebSockets").ConfigureAwait(false);
					}, TaskContinuationOptions.OnlyOnRanToCompletion)
					.ConfigureAwait(false);
				},
				waitingTimes
			);
		}

		internal static void CloseRouterChannels(int waitingTimes = 1234)
			=> Task.Run(async () => await Handler.UnregisterSynchronizerAsync().ConfigureAwait(false))
				.ContinueWith(_ =>
				{
					Global.UnregisterService("Http.WebSockets", waitingTimes);
					Global.PrimaryInterCommunicateMessageUpdater?.Dispose();
					Global.SecondaryInterCommunicateMessageUpdater?.Dispose();
					Router.CloseChannels();
				}, TaskContinuationOptions.OnlyOnRanToCompletion)
				.ConfigureAwait(false)
				.GetAwaiter()
				.GetResult();

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

			else if (message.Type.IsEquals("Thumbnail#Sync") || message.Type.IsEquals("Attachment#Sync"))
			{
				var node = message.Data.Get<string>("Node");
				if (!Handler.NodeName.IsEquals(node))
					Task.Run(() => Handler.Synchronizer.SendRequestAsync(node, message.Data.Get<string>("ServiceName"), message.Data.Get<string>("SystemID"), message.Data.Get<string>("Filename"), "true".IsEquals(message.Data.Get<string>("IsTemporary")))).ConfigureAwait(false);
			}
			return Task.CompletedTask;
		}

		static async Task ProcessAPIGatewayCommunicateMessageAsync(CommunicateMessage message)
		{
			if (message.Type.IsEquals("Service#RequestInfo"))
				await Global.UpdateServiceInfoAsync("Http.WebSockets").ConfigureAwait(false);
		}

		static SystemEx.IAsyncDisposable SynchronizerInstance { get; set; }

		static Synchronizer Synchronizer { get; } = new Synchronizer();

		internal static async Task RegisterSynchronizerAsync()
		{
			async Task registerAsync()
			{
				try
				{
					Handler.SynchronizerInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IUniqueService>(() => Handler.Synchronizer, RegistrationInterceptor.Create(Handler.NodeName, WampInvokePolicy.Single)).ConfigureAwait(false);
				}
				catch
				{
					await Task.Delay(UtilityService.GetRandomNumber(456, 789)).ConfigureAwait(false);
					try
					{
						Handler.SynchronizerInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IUniqueService>(() => Handler.Synchronizer, RegistrationInterceptor.Create(Handler.NodeName, WampInvokePolicy.Single)).ConfigureAwait(false);
					}
					catch (Exception)
					{
						throw;
					}
				}
				Global.Logger.LogInformation("The synchronizer is registered successful");
			}

			try
			{
				await registerAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Global.Logger.LogError($"Cannot register the synchronizer => {ex.Message}", ex);
			}
		}

		internal static async Task UnregisterSynchronizerAsync()
		{
			if (Handler.SynchronizerInstance != null)
				try
				{
					await Handler.SynchronizerInstance.DisposeAsync().ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Global.Logger?.LogError($"Error occurred while unregistering the synchronizer: {ex.Message}", ex);
				}
				finally
				{
					Handler.SynchronizerInstance = null;
				}
		}
		#endregion

	}

	#region Attachment Info
	[Serializable]
	public struct AttachmentInfo
	{
		public string ID { get; set; }

		public string ServiceName { get; set; }

		public string ObjectName { get; set; }

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

		public static AttachmentInfo DeleteFile(this AttachmentInfo attachmentInfo, bool isTemporary, ILogger logger = null, string objectName = null)
		{
			var filePath = attachmentInfo.GetFilePath(isTemporary);
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
			if (attachmentInfo.IsTemporary)
				return attachmentInfo;

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
				return attachmentInfo.DeleteFile(false, logger, objectName);
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
				attachmentInfo.ObjectName = json.Get<string>("ObjectName");
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

		public static Task<JToken> CreateAsync(this HttpContext context, AttachmentInfo attachmentInfo, CancellationToken cancellationToken = default(CancellationToken))
			=> context.CallServiceAsync(context.GetRequestInfo(attachmentInfo.IsThumbnail ? "Thumbnail" : "Attachment", "POST", new Dictionary<string, string>
			{
				{ "object-identity", attachmentInfo.ID },
				{ "x-object-title", attachmentInfo.Title }
			}, new JObject
			{
				{ "ID", attachmentInfo.ID },
				{ "ServiceName", attachmentInfo.ServiceName?.ToLower() },
				{ "ObjectName", attachmentInfo.ObjectName?.ToLower() },
				{ "SystemID", attachmentInfo.SystemID?.ToLower() },
				{ "DefinitionID", attachmentInfo.DefinitionID?.ToLower() },
				{ "ObjectID", attachmentInfo.ObjectID?.ToLower() },
				{ "Size", attachmentInfo.Size },
				{ "Filename", attachmentInfo.Filename },
				{ "ContentType", attachmentInfo.ContentType },
				{ "IsTemporary", attachmentInfo.IsTemporary },
				{ "IsShared", attachmentInfo.IsShared },
				{ "IsTracked", attachmentInfo.IsTracked },
				{ "Title", attachmentInfo.Title },
				{ "Description", attachmentInfo.Description }
			}.ToString(Newtonsoft.Json.Formatting.None)), cancellationToken, Global.Logger, "Http.Uploads");

		public static async Task<AttachmentInfo> GetAsync(this HttpContext context, string id, CancellationToken cancellationToken = default(CancellationToken))
			=> new AttachmentInfo
			{
				IsThumbnail = false
			}.Fill(string.IsNullOrWhiteSpace(id) ? null : await context.CallServiceAsync(context.GetRequestInfo("Attachment", "GET", new Dictionary<string, string>
			{
				{ "object-identity", id }
			}), cancellationToken, Global.Logger, "Http.Downloads").ConfigureAwait(false));

		public static Task UpdateAsync(this HttpContext context, AttachmentInfo attachmentInfo, string type, CancellationToken cancellationToken = default(CancellationToken))
			=> attachmentInfo.IsThumbnail || attachmentInfo.IsTemporary || string.IsNullOrWhiteSpace(attachmentInfo.ID)
				? Task.CompletedTask
				: Task.WhenAll(
					context.CallServiceAsync(context.GetRequestInfo("Attachment", "GET", new Dictionary<string, string>
					{
						{ "object-identity", "counters" },
						{ "x-object-id", attachmentInfo.ID },
						{ "x-user-id", context.User.Identity.Name }
					}), cancellationToken, Global.Logger, "Http.Downloads"),
					attachmentInfo.IsTracked
						? context.CallServiceAsync(context.GetRequestInfo("Attachment", "GET", new Dictionary<string, string>
							{
								{ "object-identity", "trackers" },
								{ "x-object-id", attachmentInfo.ID },
								{ "x-user-id", context.User.Identity.Name },
								{ "x-refer", context.GetReferUrl() },
								{ "x-origin", context.GetOriginUri()?.ToString() }
							}), cancellationToken, Global.Logger, "Http.Downloads")
						: Task.CompletedTask,
					new CommunicateMessage(attachmentInfo.ServiceName)
					{
						Type = $"File#{type}",
						Data = new JObject
						{
							{ "x-object-id", attachmentInfo.ID },
							{ "x-user-id", context.User.Identity.Name },
							{ "x-refer", context.GetReferUrl() },
							{ "x-origin", context.GetOriginUri()?.ToString() }
						}
					}.PublishAsync(Global.Logger, "Http.Downloads")
				);

		static RequestInfo GetRequestInfo(this HttpContext context, string objectName, string verb, Dictionary<string, string> query = null, string body = null)
		{
			var session = context.GetSession();
			var header = new Dictionary<string, string>
			{
				["x-app-token"] = context.GetParameter("x-app-token"),
				["x-app-name"] = context.GetParameter("x-app-name"),
				["x-app-platform"] = context.GetParameter("x-app-platform"),
				["x-device-id"] = context.GetParameter("x-device-id"),
				["x-passport-token"] = context.GetParameter("x-passport-token")
			}.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
			var extra = new Dictionary<string, string>
			{
				["Node"] = Handler.NodeName,
				["SessionID"] = session.SessionID.GetHMACBLAKE256(Global.ValidationKey)
			};
			if (!string.IsNullOrWhiteSpace(body))
				extra["Signature"] = body.GetHMACSHA256(Global.ValidationKey);
			else
			{
				if (!header.TryGetValue("x-app-token", out var authenticateToken))
					header.TryGetValue("x-passport-token", out authenticateToken);
				if (!string.IsNullOrWhiteSpace(authenticateToken))
				{
					header["x-app-token"] = authenticateToken;
					extra["Signature"] = authenticateToken.GetHMACSHA256(Global.ValidationKey);
				}
			}
			return new RequestInfo(session, "Files", objectName, verb, query, header, body, extra, context.GetCorrelationID());
		}
	}
	#endregion

}