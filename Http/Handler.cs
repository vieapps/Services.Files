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
using System.Drawing.Imaging;
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
using MongoDB.Driver;

#endregion

namespace net.vieapps.Services.Files
{
	public class Handler
	{
		string LoadBalancerHealthCheckURL { get; } = UtilityService.GetAppSetting("LoadBalancer:HealthCheckURL", "/load-balancer-health-check");

		internal static Cache Cache { get; } = new Cache(UtilityService.GetAppSetting("Files:Cache:Name", "VIEApps-Services-Files"), Cache.Configuration.ExpirationTime, Cache.Configuration.Provider, Logger.GetLoggerFactory());

		public Handler(RequestDelegate _) { }

		public async Task Invoke(HttpContext context)
		{
			// CORS: allow origin
			context.Response.Headers["Access-Control-Allow-Origin"] = "*";

			// CORS: options
			if (context.Request.Method.IsEquals("OPTIONS"))
			{
				var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					["Access-Control-Allow-Methods"] = "HEAD,GET,POST,PUT,PATCH"
				};
				if (context.Request.Headers.TryGetValue("Access-Control-Request-Headers", out var requestHeaders))
					headers["Access-Control-Allow-Headers"] = requestHeaders;
				context.SetResponseHeaders((int)HttpStatusCode.OK, headers);
				await context.FlushAsync(Global.CancellationTokenSource.Token).ConfigureAwait(false);
			}

			// health check
			else if (context.Request.Path.Value.IsEquals(this.LoadBalancerHealthCheckURL))
				await context.WriteAsync("OK", "text/plain", null, 0, null, TimeSpan.Zero, null, Global.CancellationTokenSource.Token).ConfigureAwait(false);

			// requests of files
			else
				await this.ProcessRequestAsync(context).ConfigureAwait(false);
		}

		async Task ProcessRequestAsync(HttpContext context)
		{
			// prepare
			context.SetItem("PipelineStopwatch", Stopwatch.StartNew());
			var requestPath = context.GetRequestPathSegments(true).First();

			if (Global.IsVisitLogEnabled)
				await context.WriteVisitStartingLogAsync().ConfigureAwait(false);

			// request to favicon.ico file
			if (requestPath.IsEquals("favicon.ico"))
				await context.ProcessFavouritesIconFileRequestAsync().ConfigureAwait(false);

			// request to robots.txt file
			else if (requestPath.Equals("robots.txt"))
				await context.WriteAsync("User-agent: *\r\nDisallow: /File.ashx/\r\nDisallow: /Download.ashx/\r\nDisallow: /Thumbnails.ashx/\r\nDisallow: /Captcha.ashx/\r\nDisallow: /captchas/\r\nDisallow: /qrcodes/", "text/plain", null, 0, null, TimeSpan.Zero, null, Global.CancellationTokenSource.Token).ConfigureAwait(false);

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
			// prepare
			var requestPath = context.GetRequestPathSegments(true).First();
			if (requestPath.IsStartsWith("preload"))
			{
				await this.ProcessPreloadRequestAsync(context).ConfigureAwait(false);
				return;
			}

			if (!Handler.Handlers.TryGetValue(requestPath.Replace(StringComparison.OrdinalIgnoreCase, ".ashx", "s"), out var type))
			{
				context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
				return;
			}

			var header = context.Request.Headers.ToDictionary();
			var query = context.ParseQuery();
			var session = context.GetSession();

			// get authenticate token
			var authenticateToken = context.GetParameter("x-app-token") ?? context.GetParameter("x-passport-token") ?? context.GetParameter("x-temp-token");

			// normalize the Bearer token
			if (string.IsNullOrWhiteSpace(authenticateToken))
			{
				authenticateToken = context.GetHeaderParameter("authorization");
				authenticateToken = authenticateToken != null && authenticateToken.IsStartsWith("Bearer") ? authenticateToken.ToArray(" ").Last() : null;
			}

			// got authenticate token => update the session
			var performSignIn = !string.IsNullOrWhiteSpace(authenticateToken) && context.GetParameter("x-temp-token") == null && (context.GetParameter("x-passport-token") != null || context.GetParameter("x-authenticate") != null);
			var responseSignInAsJon = performSignIn && "json".IsEquals(context.GetParameter("x-response"));
			if (!string.IsNullOrWhiteSpace(authenticateToken))
				try
				{
					// authenticate (token is expired after 15 minutes)
					await context.UpdateWithAuthenticateTokenAsync(session, authenticateToken, 900, null, null, null, Global.Logger, "Http.Authentication", context.GetCorrelationID()).ConfigureAwait(false);
					await context.WriteLogsAsync(Global.Logger, "Http.Authentication", $"Successfully authenticate an user with token {session.ToJson().ToString(Newtonsoft.Json.Formatting.Indented)}");

					// perform sign-in (to create authenticate ticket cookie)
					if (performSignIn)
					{
						await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new UserPrincipal(session.User), new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);
						await context.WriteLogsAsync(Global.Logger, "Http.Authentication", $"Successfully create the authenticate ticket cookie for an user ({session.User.ID})").ConfigureAwait(false);
					}

					// just assign user information
					else
						context.User = new UserPrincipal(session.User);

					if (responseSignInAsJon)
					{
						await context.WriteAsync(new JObject { ["ID"] = session.User.ID }, Global.CancellationToken).ConfigureAwait(false);
						return;
					}
				}
				catch (Exception ex)
				{
					await context.WriteLogsAsync(Global.Logger, "Http.Authentication", $"Failure authenticate a token => {ex.Message}", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
					if (responseSignInAsJon)
					{
						context.WriteError(ex);
						return;
					}
				}

			// no authenticate token => update user of the session if already signed-in
			else if (context.IsAuthenticated())
				session.User = context.GetUser();

			// update session
			if (string.IsNullOrWhiteSpace(session.User.SessionID))
				session.SessionID = session.User.SessionID = UtilityService.NewUUID;
			else
				session.SessionID = session.User.SessionID;

			var appName = context.GetParameter("x-app-name");
			if (!string.IsNullOrWhiteSpace(appName))
				session.AppName = appName;

			var appPlatform = context.GetParameter("x-app-platform");
			if (!string.IsNullOrWhiteSpace(appPlatform))
				session.AppPlatform = appPlatform;

			var deviceID = context.GetParameter("x-device-id");
			if (!string.IsNullOrWhiteSpace(deviceID))
				session.DeviceID = deviceID;

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
						context.ShowError(wampDetails.Code, wampDetails.Message, wampDetails.Type, context.GetCorrelationID(), wampDetails.Stack + "\r\n\t" + ex.StackTrace, Global.IsDebugLogEnabled);
					}
					else
						context.ShowError(ex.GetHttpStatusCode(), ex.Message, ex.GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
				}
			}
		}

		#region  Global settings & helpers
		internal static Dictionary<string, Type> Handlers { get; } = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
		{
			{ "avatars", typeof(AvatarHandler) },
			{ "captchas", typeof(CaptchaHandler) },
			{ "downloads", typeof(DownloadHandler) },
			{ "files", typeof(FileHandler) },
			{ "one.file", typeof(FileHandler) },
			{ "temp.file", typeof(FileHandler) },
			{ "images", typeof(FileHandler) },
			{ "one.image", typeof(FileHandler) },
			{ "webp.image", typeof(WebpImageHandler) },
			{ "qrcodes", typeof(QRCodeHandler) },
			{ "vietqrs", typeof(VietQRHandler) },
			{ "thumbnails", typeof(ThumbnailHandler) },
			{ "thumbnailpngs", typeof(ThumbnailHandler) },
			{ "thumbnailwebps", typeof(ThumbnailHandler) },
			{ "thumbnailsmalls", typeof(ThumbnailHandler) },
			{ "thumbnailsmallpngs", typeof(ThumbnailHandler) },
			{ "thumbnailsmallwebps", typeof(ThumbnailHandler) }
		};

		internal static void PrepareHandlers()
		{
			if (ConfigurationManager.GetSection(UtilityService.GetAppSetting("Section:Handlers", "net.vieapps.services.files.http.handlers")) is AppConfigurationSectionHandler config && config.Section.SelectNodes("handler") is XmlNodeList handlers)
				handlers.ToList()
					.Select(static info => (Path: info.Attributes["path"]?.Value?.ToLower()?.Trim(), Type: info.Attributes["type"]?.Value))
					.Where(static info => !string.IsNullOrEmpty(info.Path) && !string.IsNullOrEmpty(info.Type))
					.Select(static info =>
					{
						var path = info.Path;
						while (path.StartsWith("/"))
							path = path.Right(path.Length - 1);
						while (path.EndsWith("/"))
							path = path.Left(path.Length - 1);
						return (Path: path, info.Type);
					})
					.Where(static info => !Handler.Handlers.ContainsKey(info.Path))
					.ForEach(static info =>
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

			Global.Logger.LogInformation($"Handlers:\r\n\t{Handler.Handlers.Select(static kvp => $"{kvp.Key} => {kvp.Value.GetTypeName()}").ToString("\r\n\t")}");
		}

		static string _UserAvatarFilesPath = null, _DefaultUserAvatarFilePath = null, _AttachmentFilesPath = null, _TempFilesPath = null, _RedirectToPassportOnUnauthorized = null, _NoSync = null, _NoThumbnailImageFilePath = null;

		internal static string UserAvatarFilesPath
			=> Handler._UserAvatarFilesPath ??= UtilityService.GetAppSetting("Path:UserAvatars", Path.Combine(Global.RootPath, "data-files", "user-avatars"));

		internal static string DefaultUserAvatarFilePath
			=> Handler._DefaultUserAvatarFilePath ??= UtilityService.GetAppSetting("Path:DefaultUserAvatar", Path.Combine(Handler.UserAvatarFilesPath, "@default.png"));

		internal static string AttachmentFilesPath
			=> Handler._AttachmentFilesPath ??= UtilityService.GetAppSetting("Path:Attachments", Path.Combine(Global.RootPath, "data-files", "attachments"));

		internal static string TempFilesPath
			=> Handler._TempFilesPath ??= UtilityService.GetAppSetting("Path:Temp", Path.Combine(Global.RootPath, "data-files", "temp"));

		internal static bool RedirectToPassportOnUnauthorized
			=> "true".IsEquals(Handler._RedirectToPassportOnUnauthorized ??= UtilityService.GetAppSetting("Files:RedirectToPassportOnUnauthorized", "true"));

		internal static bool NoSync
			=> "true".IsEquals(Handler._NoSync ??= UtilityService.GetAppSetting("Files:NoSync", "false"));

		internal static string NoThumbnailImageFilePath
			=> Handler._NoThumbnailImageFilePath ??= UtilityService.GetAppSetting("Path:NoThumbnailImage", Path.Combine(Handler.AttachmentFilesPath, "@no-image.png"));
		#endregion

		#region API Gateway Router
		internal static void Connect(List<Action<object, WampSessionCreatedEventArgs>> onIncomingConnectionEstablished = null, List<Action<object, WampSessionCreatedEventArgs>> onOutgoingConnectionEstablished = null, int waitingTimes = 6789)
		{
			Global.Logger.LogInformation($"Attempting to connect to API Gateway Router [{new Uri(Router.GetRouterStrInfo()).GetResolvedURI()}]");
			Global.Connect(
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
					Global.PrimaryInterCommunicateMessageUpdater = Router.IncomingChannel?.RealmProxy.Services
						.GetSubject<CommunicateMessage>($"messages.services.{Global.ServiceName.ToLower()}")
						.Subscribe(
							async message =>
							{
								try
								{
									if (!Global.NodeID.IsEquals(message.ExcludedNodeID))
									{
										if (Global.IsDebugLogEnabled)
											await Global.WriteLogsAsync(Global.Logger, $"Http.{Global.ServiceName}", $"Got an inter-communicate message\r\n{message?.ToJson().ToString(Global.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}", null, Global.ServiceName, LogLevel.Debug, message.Data?.Get<string>("CorrelationID")).ConfigureAwait(false);
										await Handler.ProcessInterCommunicateMessageAsync(message).ConfigureAwait(false);
									}
								}
								catch (Exception ex)
								{
									await Global.WriteLogsAsync(Global.Logger, $"Http.{Global.ServiceName}", $"Error occurred while processing an inter-communicate message: {ex.Message} => {message?.ToJson().ToString(Global.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}", ex, Global.ServiceName).ConfigureAwait(false);
								}
							},
							async exception => await Global.WriteLogsAsync(Global.Logger, $"Http.{Global.ServiceName}", $"Error occurred while fetching an inter-communicate message: {exception.Message}", exception).ConfigureAwait(false)
						);
					Global.SecondaryInterCommunicateMessageUpdater?.Dispose();
					Global.SecondaryInterCommunicateMessageUpdater = Router.IncomingChannel?.RealmProxy.Services
						.GetSubject<CommunicateMessage>("messages.services.apigateway")
						.Subscribe(
							async message =>
							{
								try
								{
									if (!Global.NodeID.IsEquals(message.ExcludedNodeID))
										await Handler.ProcessAPIGatewayCommunicateMessageAsync(message).ConfigureAwait(false);
								}
								catch (Exception ex)
								{
									await Global.WriteLogsAsync(Global.Logger, $"Http.{Global.ServiceName}", $"Error occurred while processing an inter-communicate message of API Gateway: {ex.Message} => {message?.ToJson().ToString(Global.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}", ex, Global.ServiceName).ConfigureAwait(false);
								}
							},
							async exception => await Global.WriteLogsAsync(Global.Logger, $"Http.{Global.ServiceName}", $"Error occurred while fetching an inter-communicate message of API Gateway: {exception.Message}", exception).ConfigureAwait(false)
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
					Global.PrimaryInterCommunicateMessageUpdater?.Dispose();
					Global.SecondaryInterCommunicateMessageUpdater?.Dispose();
					Global.Disconnect();
				}, TaskContinuationOptions.OnlyOnRanToCompletion)
				.ContinueWith(task =>
				{
					var ex = task.Exception?.InnerException ?? task.Exception;
					if (ex != null)
						Global.Logger.LogError($"Error occurred while disconnecting from API Gateway Router => {ex.Message}", ex);
				}, TaskContinuationOptions.OnlyOnRanToCompletion)
				.ConfigureAwait(false)
				.GetAwaiter()
				.GetResult();

		static IAsyncDisposable SynchronizerInstance { get; set; }

		static Synchronizer Synchronizer { get; } = new Synchronizer();

		internal static async Task RegisterSynchronizerAsync()
		{
			try
			{
				Handler.SynchronizerInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IUniqueService>(() => Handler.Synchronizer, RegistrationInterceptor.Create(Extensions.GetUniqueName($"{Global.ServiceName}.http"), WampInvokePolicy.Single)).ConfigureAwait(false);
			}
			catch
			{
				await Task.Delay(UtilityService.GetRandomNumber(456, 789)).ConfigureAwait(false);
				try
				{
					Handler.SynchronizerInstance = await Router.IncomingChannel.RealmProxy.Services.RegisterCallee<IUniqueService>(() => Handler.Synchronizer, RegistrationInterceptor.Create(Extensions.GetUniqueName($"{Global.ServiceName}.http"), WampInvokePolicy.Single)).ConfigureAwait(false);
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
					Global.Logger?.LogError($"Error occurred while unregistering the synchronizer: {ex.Message}", ex);
				}
				finally
				{
					Handler.SynchronizerInstance = null;
				}
		}
		#endregion

		async Task ProcessPreloadRequestAsync(HttpContext context)
		{
			try
			{
				if (!context.Request.Method.IsEquals("GET"))
					throw new MethodNotAllowedException();

				var request = context.GetQueryParameter("x-request").Url64Decode();
				if (!request.GetHMACSHA256(Global.ValidationKey).IsEquals(context.GetQueryParameter("x-signature")))
					throw new InvalidRequestException();

				var data = request.ToJson();
				var attachment = new AttachmentInfo
				{
					ID = data.Get("ID", UtilityService.NewUUID),
					ServiceName = data.Get<string>("ServiceName"),
					SystemID = data.Get<string>("SystemID"),
					ObjectID = data.Get<string>("ObjectID"),
					Filename = data.Get<string>("Filename"),
					ContentType = data.Get<string>("ContentType"),
					IsThumbnail = "Thumbnail".IsEquals(data.Get<string>("Type")),
					IsTemporary = false
				};

				var isDebugLogEnabled = Global.IsDebugLogEnabled || context.Request.Query.ContainsKey("x-logs");
				if (attachment.IsThumbnail && (context.Request.Query.ContainsKey("x-force-cache") || !await Global.Cache.ExistsAsync(attachment.GetCacheKey(), Global.CancellationToken).ConfigureAwait(false)))
					await Task.WhenAll
					(
						attachment.PrepareCacheAsync(),
						isDebugLogEnabled ? context.WriteLogsAsync(Global.Logger, $"Preloads", $"Preload a thumbnail [{attachment.GetCacheKey()} => {attachment.GetFilePath()}]") : Task.CompletedTask
					).ConfigureAwait(false);
				else if (!attachment.IsThumbnail && (context.Request.Query.ContainsKey("x-force-cache") || !await Global.Cache.ExistsAsync(attachment.GetCacheKey("file"), Global.CancellationToken).ConfigureAwait(false)))
					await Task.WhenAll
					(
						attachment.PrepareCacheAsync(attachment.ContentType.IsEndsWith("/webp")),
						isDebugLogEnabled ? context.WriteLogsAsync(Global.Logger, $"Preloads", $"Preload an attachment [{attachment.GetCacheKey("file")} => {attachment.GetFilePath()}]") : Task.CompletedTask
					).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await context.WriteLogsAsync(Global.Logger, $"Preloads", $"Error occurred while preloading => {ex.Message}", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
			}
			await context.WriteAsync(new JObject { ["ID"] = context.GetCorrelationID() }, Global.CancellationToken).ConfigureAwait(false);
		}

		static async Task ProcessInterCommunicateMessageAsync(CommunicateMessage message)
		{
			// refine thumbnail to rebuild info
			if (message.Type.IsEquals("Thumbnail#Refine"))
				try
				{
					var attachmentInfo = new AttachmentInfo { IsThumbnail = true }.Fill(message.Data);
					var fileInfo = new FileInfo(attachmentInfo.GetFilePath());
					if (fileInfo.Exists)
						await new CommunicateMessage(Global.ServiceName)
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
								{ "Filename", attachmentInfo.Filename },
								{ "Size", fileInfo.Length },
								{ "ContentType", "image/jpeg" },
								{ "IsTemporary", false },
								{ "IsShared", false },
								{ "IsTracked", false },
								{ "IsThumbnail", true },
								{ "Title", "" },
								{ "Description", "" },
								{ "LastModified", message.Data.Get<DateTime>("LastModified") },
								{ "LastModifiedID", message.Data.Get<string>("LastModifiedID") }
							}
						}.PublishAsync(Global.Logger, "Thumbnails").ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					Global.Logger.LogError("Cannot send an inter-communicate message to refine thumbnail image", ex);
				}

			// check no-sync
			if (Handler.NoSync)
				return;

			// move files into trash
			if (message.Type.IsEquals("Thumbnail#Delete") || message.Type.IsEquals("Attachment#Delete"))
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

			// sync files between instances of Files HTTP Service
			else if (message.Type.IsEquals("Thumbnail#Sync") || message.Type.IsEquals("Attachment#Sync"))
			{
				var node = message.Data.Get<string>("Node");
				if (!Global.NodeID.IsEquals(node))
					Handler.Synchronizer.SendSyncRequestAsync(node, message.Data.Get<string>("ServiceName"), message.Data.Get<string>("SystemID"), message.Data.Get<string>("Filename"), "true".IsEquals(message.Data.Get<string>("IsTemporary")), message.Data.Get<string>("CorrelationID")).Run();
			}

			// copy files from a legacy system
			else if (message.Type.IsEquals("Thumbnail#Copy") || message.Type.IsEquals("Attachment#Copy"))
				new AttachmentInfo
				{
					IsThumbnail = message.Type.IsEquals("Thumbnail#Copy")
				}.Fill(message.Data).CopyFile(Global.Logger, "Synchronizers", message.Data.Get<string>("SourceDirectory"));
		}

		static Task ProcessAPIGatewayCommunicateMessageAsync(CommunicateMessage message)
			=> message.Type.IsEquals("Service#RequestInfo")
				? Global.SendServiceInfoAsync($"Http.{Global.ServiceName}")
				: Task.CompletedTask;
	}
}