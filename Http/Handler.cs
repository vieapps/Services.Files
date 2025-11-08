#region Related components
using System;
using System.Net;
using System.IO;
using System.Xml;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using WampSharp.V2.Core.Contracts;
using WampSharp.V2.Realm;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class Handler
	{

		#region Properties
		string LoadBalancerHealthCheckURL { get; } = UtilityService.GetAppSetting("LoadBalancer:HealthCheckURL", "/load-balancer-health-check");

		internal static int TokenExpiresAfter { get; } = Int32.TryParse(UtilityService.GetAppSetting("APIs:ExpiresAfter", "0"), out var expiresAfter) && expiresAfter > -1 ? expiresAfter : 900;

		internal static bool IsCacheImages { get; } = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Images", "true")) && Global.Cache != null;

		internal static bool IsCacheThumbnails { get; } = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Thumbnails", "true")) && Global.Cache != null;

		internal static bool PrepareCache { get; } = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Prepare", "false")) && Global.Cache != null;

		internal static string UserAvatarFilesPath { get; } = UtilityService.GetAppSetting("Path:UserAvatars", Path.Combine(Global.RootPath, "data-files", "user-avatars"));

		internal static string DefaultUserAvatarFilePath { get; } = UtilityService.GetAppSetting("Path:DefaultUserAvatar", Path.Combine(Handler.UserAvatarFilesPath, "@default.png"));

		internal static string AttachmentFilesPath { get; } = UtilityService.GetAppSetting("Path:Attachments", Path.Combine(Global.RootPath, "data-files", "attachments"));

		internal static string TempFilesPath { get; } = UtilityService.GetAppSetting("Path:Temp", Path.Combine(Global.RootPath, "data-files", "temp"));

		internal static string NoThumbnailImageFilePath { get; } = UtilityService.GetAppSetting("Path:NoThumbnailImage", Path.Combine(Handler.AttachmentFilesPath, "@no-image.png"));

		internal static bool TrackSessions { get; set; } = "true".IsEquals(UtilityService.GetAppSetting("Sessions:Track", "true")) && "true".IsEquals(UtilityService.GetAppSetting("Sessions:Track:Files", "true"));

		internal static IEnumerable<(string Handler, string MIMEType)> MIMEs { get; } =
		[
			("pngs", "image=png"),
			("jpgs", "image=jpeg"),
			("jpegs", "image=jpeg"),
			("webps", "image=webp"),
			("mp3s", "audio=mp3"),
			("m4as", "audio=m4a"),
			("mp4s", "video=mp4"),
			("pdfs", "application=pdf"),
			("docs", "application=msword"),
			("docxs", "application=vnd.openxmlformats-officedocument.wordprocessingml.document")
		];

		internal static Dictionary<string, Type> Handlers { get; } = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
		{
			{ "avatars", typeof(AvatarHandler) },
			{ "captchas", typeof(CaptchaHandler) },
			{ "downloads", typeof(DownloadHandler) },
			{ "files", typeof(FileHandler) },
			{ "temp.file", typeof(FileHandler) },
			{ "one.file", typeof(FileHandler) },
			{ "one.image", typeof(FileHandler) },
			{ "images", typeof(WebpImageHandler) },
			{ "webp.image", typeof(WebpImageHandler) },
			{ "qrcodes", typeof(QRCodeHandler) },
			{ "vietqrs", typeof(VietQRHandler) },
			{ "thumbnails", typeof(ThumbnailHandler) },
			{ "thumbnailpngs", typeof(ThumbnailHandler) },
			{ "thumbnailwebps", typeof(ThumbnailHandler) },
			{ "thumbnailsmalls", typeof(ThumbnailHandler) },
			{ "thumbnailsmallpngs", typeof(ThumbnailHandler) },
			{ "thumbnailsmallwebps", typeof(ThumbnailHandler) },
			{ "thumbnailbigs", typeof(ThumbnailHandler) },
			{ "thumbnailbigpngs", typeof(ThumbnailHandler) },
			{ "thumbnailbigwebps", typeof(ThumbnailHandler) }
		};
		#endregion

		public Handler(RequestDelegate _) { }

		internal static void PrepareHanlders()
		{
			Handler.MIMEs.ForEach(mime => Handler.Handlers[mime.Handler] = typeof(FileHandler));
			if (ConfigurationManager.GetSection(UtilityService.GetAppSetting("Section:Handlers", "net.vieapps.services.files.http.handlers")) is AppConfigurationSectionHandler config && config.Section.SelectNodes("handler") is XmlNodeList handlers)
				handlers.ToList()
				.Select(info => (Path: info.Attributes["path"]?.Value?.ToLower()?.Trim(), Type: info.Attributes["type"]?.Value))
				.Where(info => !string.IsNullOrEmpty(info.Path) && !string.IsNullOrEmpty(info.Type))
				.Select(info =>
				{
					var path = info.Path;
					while (path.StartsWith('/'))
						path = path.Right(path.Length - 1);
					while (path.EndsWith('/'))
						path = path.Left(path.Length - 1);
					return (Path: path, info.Type);
				})
				.Where(info => !Handler.Handlers.ContainsKey(info.Path))
				.ForEach(info =>
				{
					try
					{
						var type = AssemblyLoader.GetType(info.Type);
						if (type != null && type.CreateInstance() is Services.FileHandler)
							Handler.Handlers[info.Path] = type;
					}
					catch (Exception ex)
					{
						Global.Logger.LogError($"Cannot load a file handler ({info.Type}) => {ex.Message}", ex);
					}
				});
			Global.Logger.LogInformation($"Handlers:\r\n\t{Handler.Handlers.Select(kvp => $"{kvp.Key} => {kvp.Value.GetTypeName()}").ToString("\r\n\t")}");
		}

		public Task Invoke(HttpContext context)
		{
			// CORS: allow origin
			context.Response.Headers.AccessControlAllowOrigin = "*";

			// CORS: options
			if (context.Request.Method.IsEquals("OPTIONS"))
			{
				var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					["X-Node"] = Global.NodeID,
					["Access-Control-Allow-Methods"] = "HEAD,GET,POST,PUT,PATCH"
				};
				if (context.Request.Headers.TryGetValue("Access-Control-Request-Headers", out var requestHeaders))
					headers["Access-Control-Allow-Headers"] = requestHeaders;
				context.SetResponseHeaders((int)HttpStatusCode.OK, headers);
				return Task.CompletedTask;
			}

			// health check
			if (context.Request.Path.Value.IsEquals(this.LoadBalancerHealthCheckURL))
				return context.WriteAsync("OK", "text/plain", null, 0, null, TimeSpan.Zero, null, Global.CancellationToken);

			// requests of the service
			return this.ProcessRequestAsync(context);
		}

		async Task ProcessRequestAsync(HttpContext context)
		{
			// prepare
			context.SetItem("PipelineStopwatch", Stopwatch.StartNew());
			context.SetItem("Correlation-ID", context.GetParameter("x-original-correlation-id") ?? context.GetParameter("x-correlation-id") ?? UtilityService.NewUUID);

			if (Global.IsVisitLogEnabled)
				await context.WriteVisitStartingLogAsync().ConfigureAwait(false);

			var requestPath = context.GetRequestPathSegments(true).First();

			// request to favicon.ico file
			if (requestPath.IsEquals("favicon.ico"))
				await context.ProcessFavouritesIconFileRequestAsync().ConfigureAwait(false);

			// request to robots.txt file
			else if (requestPath.Equals("robots.txt"))
				await context.WriteAsync("User-agent: *\r\nDisallow: /File.ashx/\r\nDisallow: /Download.ashx/\r\nDisallow: /Thumbnails.ashx/\r\nDisallow: /Captcha.ashx/\r\nDisallow: /captchas/\r\nDisallow: /qrcodes/\r\nDisallow: /vietqrs/", "text/plain", null, 0, "public", TimeSpan.Zero, null, Global.CancellationToken).ConfigureAwait(false);

			// request to static segments
			else if (Global.StaticSegments.Contains(requestPath))
				await context.ProcessStaticFileRequestAsync().ConfigureAwait(false);

			// request to prepare cache of an image
			else if (requestPath.IsStartsWith("prepare") || requestPath.IsStartsWith("preload"))
				await this.PrepareCacheAsync(context).ConfigureAwait(false);

			// handle the request to files
			else
				await this.HandleRequestAsync(context).ConfigureAwait(false);

			if (Global.IsVisitLogEnabled)
				await context.WriteVisitFinishingLogAsync().ConfigureAwait(false);
		}

		async Task HandleRequestAsync(HttpContext context)
		{
			// prepare
			var requestPath = context.GetRequestPathSegments(true).First();
			if (!Handler.Handlers.TryGetValue(requestPath.Replace(StringComparison.OrdinalIgnoreCase, ".ashx", "s"), out var type))
			{
				context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
				return;
			}

			var header = context.Request.Headers.ToDictionary();
			var query = context.ParseQuery();
			var session = context.GetSession();
			var isDebugLogEnabled = Global.IsDebugLogEnabled || context.ContainsKey("x-logs");

			// prepare authenticate token
			var authenticateToken = context.GetParameter("x-app-token") ?? context.GetParameter("x-temp-token");
			if (string.IsNullOrWhiteSpace(authenticateToken) && header.TryGetValue("authorization", out authenticateToken))
			{
				header.Remove("authorization");
				try
				{
					var isBasicToken = authenticateToken.IsStartsWith("Basic");
					authenticateToken = isBasicToken || authenticateToken.IsStartsWith("Bearer") || authenticateToken.IsStartsWith("JWT") ? authenticateToken.ToArray(" ").Last() : null;
					if (authenticateToken != null)
					{
						var response = await new RequestInfo(session, "Users", "Token", "GET")
						{
							Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
							Header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
							{
								["x-authorization-token"] = authenticateToken,
								["x-authorization-mode"] = isBasicToken ? "Basic" : "Bearer",
								["x-authorization-signature"] = authenticateToken.GetHMACSHA256(Global.ValidationKey)
							},
							CorrelationID = context.GetCorrelationID()
						}.CallServiceAsync(Global.CancellationToken).ConfigureAwait(false);
						header["x-app-token"] = authenticateToken = response.Get<string>("Token");
						session.Fill(response.Get<JObject>("Session"));
					}
				}
				catch { }
			}

			// got authenticate token => update the session
			var performSignIn = context.ContainsKey("x-authenticate");
			if (!string.IsNullOrWhiteSpace(authenticateToken))
				try
				{
					// authenticate
					await context.UpdateWithAuthenticateTokenAsync(session, authenticateToken, Handler.TokenExpiresAfter, null, null, null, Global.Logger, "Authentications", context.GetCorrelationID()).ConfigureAwait(false);
					if (isDebugLogEnabled)
						await context.WriteLogsAsync(Global.Logger, "Authentications", $"Successfully authenticate an user with token {session.ToJson().ToString(Newtonsoft.Json.Formatting.Indented)}");

					// perform sign-in (to create authenticate ticket cookie)
					if (performSignIn)
					{
						await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new UserPrincipal(session.User), new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);
						if (isDebugLogEnabled)
							await context.WriteLogsAsync(Global.Logger, "Authentications", $"Successfully create the authenticate ticket cookie for an user ({session.User.ID})").ConfigureAwait(false);
						if ("json".IsEquals(context.GetParameter("x-response")))
						{
							await context.WriteAsync(new JObject { ["Status"] = "OK" }, new Dictionary<string, string> { ["X-Correlation-ID"] = context.GetCorrelationID(), ["X-Node"] = Global.NodeID }, Global.CancellationToken).ConfigureAwait(false);
							return;
						}
					}

					// just assign user information
					else
						context.User = new UserPrincipal(session.User);
				}
				catch (Exception ex)
				{
					await context.WriteLogsAsync(Global.Logger, "Authentications", $"Failure authenticate a token => {ex.Message}", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
					if (performSignIn)
					{
						context.WriteError(ex);
						return;
					}
				}

			// no authenticate token => update user of the session if already signed-in
			else if (context.IsAuthenticated())
				session.User = context.GetUser();

			// update session
			session.SessionID = session.User.SessionID = string.IsNullOrWhiteSpace(session.User.SessionID)
				? UtilityService.NewUUID
				: session.User.SessionID;

			if (context.TryGetParameter("x-device-id", out var deviceID))
				try
				{
					session.DeviceID = deviceID.Url64Decode();
				}
				catch
				{
					session.DeviceID = deviceID;
				}
			else if (context.TryGetParameter("x-did", out deviceID))
				try
				{
					session.DeviceID = deviceID.Url64Decode();
				}
				catch {	}
			session.DeviceID = string.IsNullOrWhiteSpace(session.DeviceID) ? $"{UtilityService.NewUUID}@vieapps-ngx" : session.DeviceID;

			if (context.TryGetParameter("x-app-name", out var appName) && !string.IsNullOrWhiteSpace(appName))
				try
				{
					session.AppName = appName.Url64Decode();
				}
				catch
				{
					session.AppName = appName;
				}

			if (context.TryGetParameter("x-app-platform", out var appPlatform) && !string.IsNullOrWhiteSpace(appPlatform))
				try
				{
					session.AppPlatform = appPlatform.Url64Decode();
				}
				catch
				{
					session.AppPlatform = appPlatform;
				}

			// store the session for further use
			context.SetItem("Session", session);

			// process the request
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(Global.CancellationToken, context.RequestAborted);
			var handler = type.CreateInstance<Services.FileHandler>();
			try
			{
				await handler.ProcessRequestAsync(context, cts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				var logName = context.Request.Method.IsEquals("POST")
					? "Uploads"
					: requestPath.IsStartsWith("thumbnail")
						? "Thumbnails"
						: requestPath.IsStartsWith("file") || requestPath.IsStartsWith("download")
							? "Downloads"
							: requestPath.Replace(StringComparison.OrdinalIgnoreCase, ".ashx", "s");
				await context.WriteLogsAsync(handler?.Logger, logName, $"Error occurred => {context.Request.Method} {context.GetRequestUri()}", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);

				if (context.Request.Method.IsEquals("POST"))
					context.WriteError(handler?.Logger, ex, null, null, false);
				else
				{
					if (ex is WampException wampException)
					{
						var wampDetails = wampException.GetDetails();
						if (wampDetails.Type != "OperationCanceledException")
							context.ShowError(wampDetails.Code, wampDetails.Message, wampDetails.Type, context.GetCorrelationID(), wampDetails.Stack + "\r\n\t" + ex.StackTrace, Global.IsDebugLogEnabled);
					}
					else
						context.ShowError(ex.GetHttpStatusCode(), ex.Message, ex.GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
				}
			}
		}

		async Task PrepareCacheAsync(HttpContext context)
		{
			try
			{
				var request = context.Request.Method.IsEquals("GET") ? context.GetQueryParameter("x-request")?.Url64Decode() : null;
				if ((request ?? "nothing").GetHMACSHA256(Global.ValidationKey).IsEquals(context.Request.Method.IsEquals("GET") ? context.GetQueryParameter("x-signature") : "nothing"))
				{
					var stopwatch = Stopwatch.StartNew();
					var json = request.ToJson();
					var attachment = new AttachmentInfo { IsThumbnail = "Thumbnail".IsEquals(json.Get<string>("Type")) }.Fill(json);
					var isDebugLogEnabled = Global.IsDebugLogEnabled || context.ContainsKey("x-logs");
					var forceCache = context.ContainsKey("x-force-cache");
					if (attachment.IsThumbnail && (forceCache || !await Global.Cache.ExistsAsync(attachment.GetCacheKey(), Global.CancellationToken).ConfigureAwait(false)))
					{
						var keys = await attachment.PrepareCacheAsync().ConfigureAwait(false);
						if (isDebugLogEnabled)
							await context.WriteLogsAsync(Global.Logger, "Caches", $"Prepare cache of a thumbnail successful - Execution times: {stopwatch.GetElapsedTimes()}\r\n- File: {attachment.GetFilePath()}\r\n- Keys: {keys.Where(key => !key.IsEndsWith(":time")).Join(", ")}").ConfigureAwait(false);
					}
					else if (!attachment.IsThumbnail && (forceCache || !await Global.Cache.ExistsAsync(attachment.GetCacheKey("file"), Global.CancellationToken).ConfigureAwait(false)))
					{
						var keys = await attachment.PrepareCacheAsync(attachment.IsWebP(), null, null, 0, context.ContainsKey("x-only-webp")).ConfigureAwait(false);
						if (isDebugLogEnabled)
							await context.WriteLogsAsync(Global.Logger, "Caches", $"Prepare cache of an attachment successful - Execution times: {stopwatch.GetElapsedTimes()}\r\n- File: {attachment.GetFilePath()}\r\n- Keys: {keys.Where(key => !key.IsEndsWith(":time")).Join(", ")}").ConfigureAwait(false);
					}
				}
			}
			catch (Exception ex)
			{
				await context.WriteLogsAsync(Global.Logger, "Caches", $"Error occurred while preparing cache of an image\r\n- URI: {context.GetRequestUri()}\r\n- Error: {ex.Message}", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
			}
			await context.WriteAsync(new JObject { ["ID"] = context.GetCorrelationID() }, Global.CancellationToken).ConfigureAwait(false);
		}

		#region API Gateway Router
		internal static void Connect(List<Action<object, WampSessionCreatedEventArgs>> onIncomingConnectionEstablished = null, List<Action<object, WampSessionCreatedEventArgs>> onOutgoingConnectionEstablished = null, int waitingTimes = 6789)
		{
			Global.Logger.LogInformation($"Attempting to connect to API Gateway Router [{new Uri(Router.GetRouterStrInfo()).GetResolvedURI()}]");
			Global.Connect
			(
				async (sender, arguments) =>
				{
					onIncomingConnectionEstablished?.ForEach(action =>
					{
						try
						{
							action?.Invoke(sender, arguments);
						}
						catch (Exception ex)
						{
							Global.Logger.LogError($"Error occurred while calling on-incoming action => {ex.Message}", ex);
						}
					});

					try
					{
						await Handler.RegisterSynchronizerAsync().ConfigureAwait(false);
						Global.Logger.LogInformation("The synchronizer is registered successful");
					}
					catch (Exception ex)
					{
						Global.Logger.LogError($"Cannot register the synchronizer => {ex.Message}", ex);
					}

					Global.PrimaryInterCommunicateMessageUpdater?.Dispose();
					Global.PrimaryInterCommunicateMessageUpdater = Router.IncomingChannel.Subscribe<CommunicateMessage>
					(
						"messages.services.files",
						message => Global.NodeID.IsEquals(message.ExcludedNodeID) ? Task.CompletedTask : Handler.ProcessInterCommunicateMessageAsync(message),
						exception => Global.WriteLogsAsync(Global.Logger, null, $"Error occurred while fetching an inter-communicate message: {exception.Message}", exception)
					);
					Global.SecondaryInterCommunicateMessageUpdater?.Dispose();
					Global.SecondaryInterCommunicateMessageUpdater = Router.IncomingChannel.Subscribe<CommunicateMessage>
					(
						"messages.services.apigateway",
						message => Global.NodeID.IsEquals(message.ExcludedNodeID) ? Task.CompletedTask : Handler.ProcessAPIGatewayCommunicateMessageAsync(message),
						exception => Global.WriteLogsAsync(Global.Logger, null, $"Error occurred while fetching an inter-communicate message of API Gateway: {exception.Message}", exception)
					);
				},
				async (sender, arguments) =>
				{
					onOutgoingConnectionEstablished?.ForEach(action =>
					{
						try
						{
							action?.Invoke(sender, arguments);
						}
						catch (Exception ex)
						{
							Global.Logger.LogError($"Error occurred while calling on-outgoing action => {ex.Message}", ex);
						}
					});
					await Global.RegisterServiceAsync().ConfigureAwait(false);
				},
				waitingTimes,
				exception => Global.Logger.LogError($"Cannot connect to API Gateway Router in a period of times => {exception.Message}", exception),
				exception => Global.Logger.LogError($"Error occurred while connecting to API Gateway Router => {exception.Message}", exception)
			);
		}

		internal static void Disconnect()
			=> Handler.UnregisterSynchronizerAsync()
			.ContinueWith(async task =>
			{
				var ex = task.Exception?.InnerException ?? task.Exception;
				if (ex != null)
					Global.Logger.LogError($"Error occurred while unregistering the synchronizer => {ex.Message}", ex);
				await Global.UnregisterServiceAsync().ConfigureAwait(false);
			}, TaskContinuationOptions.OnlyOnRanToCompletion)
			.ContinueWith(task =>
			{
				var ex = task.Exception?.InnerException ?? task.Exception;
				if (ex != null)
					Global.Logger.LogError($"Error occurred while unregistering the service => {ex.Message}", ex);
				Global.Disconnect();
			}, TaskContinuationOptions.OnlyOnRanToCompletion)
			.ContinueWith(task =>
			{
				var ex = task.Exception?.InnerException ?? task.Exception;
				if (ex != null)
					Global.Logger.LogError($"Error occurred while disconnecting from API Gateway Router => {ex.Message}", ex);
			}, TaskContinuationOptions.OnlyOnRanToCompletion)
			.Execute(true);

		static IAsyncDisposable SynchronizerInstance { get; set; }

		static Synchronizer Synchronizer { get; } = new();

		internal static async Task RegisterSynchronizerAsync()
		{
			try
			{
				Handler.SynchronizerInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IUniqueService>(() => Handler.Synchronizer, RegistrationInterceptor.Create(Handler.Synchronizer.ServiceUniqueName, WampInvokePolicy.Single)).ConfigureAwait(false);
			}
			catch
			{
				await Task.Delay(UtilityService.GetRandomNumber(456, 789)).ConfigureAwait(false);
				try
				{
					Handler.SynchronizerInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IUniqueService>(() => Handler.Synchronizer, RegistrationInterceptor.Create(Handler.Synchronizer.ServiceUniqueName, WampInvokePolicy.Single)).ConfigureAwait(false);
				}
				catch (Exception)
				{
					throw;
				}
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
					Global.Logger?.LogError($"Error occurred while unregistering the synchronizer => {ex.Message}", ex);
				}
				finally
				{
					Handler.SynchronizerInstance = null;
				}
			try
			{
				await Handler.Synchronizer.DisposeAsync().ConfigureAwait(false);
			}
			catch { }
		}
		#endregion

		#region Process inter-communicate messages
		static async Task ProcessInterCommunicateMessageAsync(CommunicateMessage message)
		{
			// refine thumbnail to rebuild info
			if (message.Type.IsEquals("Thumbnail#Refine"))
			{
				await Task.Delay(UtilityService.GetRandomNumber(123, 456), Global.CancellationToken).ConfigureAwait(false);

				var correlationID = message.Data.Get("CorrelationID", UtilityService.NewUUID);
				var attachmentInfo = new AttachmentInfo { IsThumbnail = true }.Fill(message.Data);

				var sourceFilename = attachmentInfo.Filename;
				var destinationFilename = $"{attachmentInfo.ObjectID.ToLower()}.jpg";

				var fileInfo = new FileInfo(Path.Combine(Handler.AttachmentFilesPath, attachmentInfo.SystemID, sourceFilename));
				if (!fileInfo.Exists)
				{
					sourceFilename = $"{attachmentInfo.ObjectID.ToLower()}.jpg";
					fileInfo = new FileInfo(Path.Combine(Handler.AttachmentFilesPath, attachmentInfo.SystemID, sourceFilename));
					if (!fileInfo.Exists)
					{
						sourceFilename = $"{attachmentInfo.ID.ToLower()}.jpg";
						fileInfo = new FileInfo(Path.Combine(Handler.AttachmentFilesPath, attachmentInfo.SystemID, sourceFilename));
					}
				}

				if (fileInfo.Exists)
					try
					{
						File.Move(fileInfo.FullName, Path.Combine(Handler.AttachmentFilesPath, attachmentInfo.SystemID, destinationFilename));
					}
					catch { }

				new CommunicateMessage(Global.ServiceName)
				{
					Type = "Thumbnail#Rebuild",
					Data = new JObject
					{
						{ "ID", string.IsNullOrWhiteSpace(attachmentInfo.ID) || !attachmentInfo.ID.IsValidUUID() ? UtilityService.NewUUID : attachmentInfo.ID },
						{ "ServiceName", attachmentInfo.ServiceName },
						{ "ObjectName", attachmentInfo.ObjectName },
						{ "SystemID", attachmentInfo.SystemID },
						{ "EntityInfo", attachmentInfo.EntityInfo },
						{ "ObjectID", attachmentInfo.ObjectID },
						{ "Filename", fileInfo.Exists ? destinationFilename : "Not-Existed" },
						{ "Size", fileInfo.Exists ? fileInfo.Length : 0 },
						{ "ContentType", "image/jpeg" },
						{ "IsTemporary", false },
						{ "IsShared", false },
						{ "IsTracked", false },
						{ "IsThumbnail", true },
						{ "Title", "" },
						{ "Description", "" },
						{ "LastModified", message.Data.Get<DateTime>("LastModified") },
						{ "LastModifiedID", message.Data.Get<string>("LastModifiedID") },
						{ "CorrelationID", correlationID }
					}
				}.Send();
			}

			// move files into trash
			else if (message.Type.IsEquals("Thumbnail#Delete") || message.Type.IsEquals("Attachment#Delete"))
				new AttachmentInfo
				{
					IsThumbnail = message.Type.IsEquals("Thumbnail#Delete")
				}.Fill(message.Data).MoveFileIntoTrash(Global.Logger, "Synchronizers", message.Data.Get<string>("CorrelationID"));

			// mark as official => move files from temporary directory to official directory
			else if (message.Type.IsEquals("Thumbnail#Move") || message.Type.IsEquals("Attachment#Move"))
				new AttachmentInfo
				{
					IsThumbnail = message.Type.IsEquals("Thumbnail#Move")
				}.Fill(message.Data).MoveFile(Global.Logger, "Synchronizers");

			// copy files from a legacy system
			else if (message.Type.IsEquals("Thumbnail#Copy") || message.Type.IsEquals("Attachment#Copy"))
				new AttachmentInfo
				{
					IsThumbnail = message.Type.IsEquals("Thumbnail#Copy")
				}.Fill(message.Data).CopyFile(Global.Logger, "Synchronizers", message.Data.Get<string>("SourceDirectory"));

			// sync files between instances of Files HTTP Service
			else if (message.Type.IsEquals("Thumbnail#Sync") || message.Type.IsEquals("Attachment#Sync") || message.Type.IsEquals("Avatar#Sync"))
			{
				var node = message.Data.Get<string>("Node");
				if (!Global.NodeID.IsEquals(node))
					Handler.Synchronizer.SendSyncRequestAsync(node, message.Data.Get<string>("ServiceName"), message.Data.Get<string>("SystemID"), message.Data.Get<string>("Filename"), "true".IsEquals(message.Data.Get<string>("IsTemporary")), "true".IsEquals(message.Data.Get<string>("IsAvatar")), message.Data.Get<string>("CorrelationID")).Execute(false, _ => { }, UtilityService.GetRandomNumber(123, 234));
			}

			else if (message.Type.IsEquals("Sessions#Track#Disable"))
				Handler.TrackSessions = false;
			else if (message.Type.IsEquals("Sessions#Track#Enable"))
				Handler.TrackSessions = true;
		}

		static Task ProcessAPIGatewayCommunicateMessageAsync(CommunicateMessage message)
			=> message.Type.IsEquals("Service#RequestInfo")
				? Global.SendServiceInfoAsync("Communicates")
				: Task.CompletedTask;
		#endregion

	}
}