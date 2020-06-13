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
using WampSharp.V2.Realm;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class Handler
	{
		RequestDelegate Next { get; }

		string LoadBalancingHealthCheckUrl { get; } = UtilityService.GetAppSetting("HealthCheckUrl", "/load-balancing-health-check");

		public Handler(RequestDelegate next)
			=> this.Next = next;

		public async Task Invoke(HttpContext context)
		{
			// CORS: allow origin
			context.Response.Headers["Access-Control-Allow-Origin"] = "*";

			// CORS: options
			if (context.Request.Method.IsEquals("OPTIONS"))
			{
				var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					["Access-Control-Allow-Methods"] = "HEAD,GET,POST"
				};
				if (context.Request.Headers.TryGetValue("Access-Control-Request-Headers", out var requestHeaders))
					headers["Access-Control-Allow-Headers"] = requestHeaders;
				context.SetResponseHeaders((int)HttpStatusCode.OK, headers);
				await context.FlushAsync(Global.CancellationTokenSource.Token).ConfigureAwait(false);
			}

			// load balancing health check
			else if (context.Request.Path.Value.IsEquals(this.LoadBalancingHealthCheckUrl))
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
			if (!Handler.Handlers.TryGetValue(requestPath.Replace(StringComparison.OrdinalIgnoreCase, ".ashx", "s"), out var type))
			{
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
				return;
			}

			var header = context.Request.Headers.ToDictionary();
			var query = context.ParseQuery();
			var session = context.GetSession();

			// get authenticate token
			var authenticateToken = context.GetParameter("x-app-token") ?? context.GetParameter("x-passport-token");

			// normalize the Bearer token
			if (string.IsNullOrWhiteSpace(authenticateToken))
			{
				authenticateToken = context.GetHeaderParameter("authorization");
				authenticateToken = authenticateToken != null && authenticateToken.IsStartsWith("Bearer") ? authenticateToken.ToArray(" ").Last() : null;
			}

			// got authenticate token => update the session
			if (!string.IsNullOrWhiteSpace(authenticateToken))
				try
				{
					// authenticate (token is expired after 15 minutes)
					await context.UpdateWithAuthenticateTokenAsync(session, authenticateToken, 900, null, null, null, Global.Logger, "Http.Authentication", context.GetCorrelationID()).ConfigureAwait(false);
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync(Global.Logger, "Http.Authentication", $"Successfully authenticate an user with token {session.ToJson().ToString(Newtonsoft.Json.Formatting.Indented)}");

					// perform sign-in (to create authenticate ticket cookie) when the authenticate token its came from passport service
					if (context.GetParameter("x-passport-token") != null)
					{
						await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new UserPrincipal(session.User), new AuthenticationProperties { IsPersistent = false }).ConfigureAwait(false);
						if (Global.IsDebugLogEnabled)
							await context.WriteLogsAsync(Global.Logger, "Http.Authentication", $"Successfully create the authenticate ticket cookie for an user ({session.User.ID})");
					}

					// just assign user information
					else
						context.User = new UserPrincipal(session.User);
				}
				catch (Exception ex)
				{
					await context.WriteLogsAsync(Global.Logger, "Http.Authentication", $"Failure authenticate a token => {ex.Message}", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
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
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Global.CancellationTokenSource.Token, context.RequestAborted))
			{
				var handler = type.CreateInstance() as Services.FileHandler;
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
					await context.WriteLogsAsync(handler?.Logger, $"Http.{logName}", $"Error occurred => {context.Request.Method} {context.GetRequestUri()}", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);

					if (context.Request.Method.IsEquals("POST"))
						context.WriteError(handler?.Logger, ex, null, null, false);
					else
					{
						if (ex is AccessDeniedException && !context.IsAuthenticated() && Handler.RedirectToPassportOnUnauthorized && !query.ContainsKey("x-app-token") && !query.ContainsKey("x-passport-token"))
							context.Redirect(context.GetPassportSessionAuthenticatorUrl());
						else
						{
							if (ex is WampException)
							{
								var wampException = (ex as WampException).GetDetails();
								context.ShowHttpError(statusCode: wampException.Item1, message: wampException.Item2, type: wampException.Item3, correlationID: context.GetCorrelationID(), stack: wampException.Item4 + "\r\n\t" + ex.StackTrace, showStack: Global.IsDebugLogEnabled);
							}
							else
								context.ShowHttpError(statusCode: ex.GetHttpStatusCode(), message: ex.Message, type: ex.GetTypeName(true), correlationID: context.GetCorrelationID(), ex: ex, showStack: Global.IsDebugLogEnabled);
						}
					}
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
			{ "images", typeof(FileHandler) },
			{ "one.image", typeof(FileHandler) },
			{ "qrcodes", typeof(QRCodeHandler) },
			{ "thumbnails", typeof(ThumbnailHandler) },
			{ "thumbnailbigs", typeof(ThumbnailHandler) },
			{ "thumbnailpngs", typeof(ThumbnailHandler) },
			{ "thumbnailbigpngs", typeof(ThumbnailHandler) }
		};

		internal static void PrepareHandlers()
		{
			if (ConfigurationManager.GetSection(UtilityService.GetAppSetting("Section:Handlers", "net.vieapps.services.files.http.handlers")) is AppConfigurationSectionHandler config && config.Section.SelectNodes("handler") is XmlNodeList handlers)
				handlers.ToList()
					.Select(info => new Tuple<string, string>(info.Attributes["path"]?.Value?.ToLower()?.Trim(), info.Attributes["type"]?.Value))
					.Where(info => !string.IsNullOrEmpty(info.Item1) && !string.IsNullOrEmpty(info.Item2))
					.Select(info =>
					{
						var path = info.Item1;
						while (path.StartsWith("/"))
							path = path.Right(path.Length - 1);
						while (path.EndsWith("/"))
							path = path.Left(path.Length - 1);
						return new Tuple<string, string>(path, info.Item2);
					})
					.Where(info => !Handler.Handlers.ContainsKey(info.Item1))
					.ForEach(info =>
					{
						try
						{
							var type = AssemblyLoader.GetType(info.Item2);
							if (type != null && type.CreateInstance() is Services.FileHandler)
								Handler.Handlers[info.Item1] = type;
						}
						catch (Exception ex)
						{
							Global.Logger.LogError($"Cannot load a file handler ({info.Item2}) => {ex.Message}", ex);
						}
					});

			Global.Logger.LogInformation($"Handlers:\r\n\t{Handler.Handlers.Select(kvp => $"{kvp.Key} => {kvp.Value.GetTypeName()}").ToString("\r\n\t")}");
		}

		static string _UserAvatarFilesPath = null, _DefaultUserAvatarFilePath = null, _AttachmentFilesPath = null, _TempFilesPath = null, _RedirectToPassportOnUnauthorized = null;

		internal static string UserAvatarFilesPath
			=> Handler._UserAvatarFilesPath ?? (Handler._UserAvatarFilesPath = UtilityService.GetAppSetting("Path:UserAvatars", Path.Combine(Global.RootPath, "data-files", "user-avatars")));

		internal static string DefaultUserAvatarFilePath
			=> Handler._DefaultUserAvatarFilePath ?? (Handler._DefaultUserAvatarFilePath = UtilityService.GetAppSetting("Path:DefaultUserAvatar", Path.Combine(Handler.UserAvatarFilesPath, "@default.png")));

		internal static string AttachmentFilesPath
			=> Handler._AttachmentFilesPath ?? (Handler._AttachmentFilesPath = UtilityService.GetAppSetting("Path:Attachments", Path.Combine(Global.RootPath, "data-files", "attachments")));

		internal static string TempFilesPath
			=> Handler._TempFilesPath ?? (Handler._TempFilesPath = UtilityService.GetAppSetting("Path:Temp", Path.Combine(Global.RootPath, "data-files", "temp")));

		internal static string NodeName => Extensions.GetUniqueName(Global.ServiceName + ".http");

		internal static bool RedirectToPassportOnUnauthorized
			=> "true".IsEquals(Handler._RedirectToPassportOnUnauthorized ?? (Handler._RedirectToPassportOnUnauthorized = UtilityService.GetAppSetting("Files:RedirectToPassportOnUnauthorized", "true")));
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
					Global.PrimaryInterCommunicateMessageUpdater = Router.IncomingChannel.RealmProxy.Services
						.GetSubject<CommunicateMessage>($"messages.services.{Global.ServiceName.ToLower()}")
						.Subscribe(
							async message =>
							{
								try
								{
									await Handler.ProcessInterCommunicateMessageAsync(message).ConfigureAwait(false);
								}
								catch (Exception ex)
								{
									await Global.WriteLogsAsync(Global.Logger, $"Http.{Global.ServiceName}", $"Error occurred while processing an inter-communicate message: {ex.Message} => {message?.ToJson().ToString(Global.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}", ex, Global.ServiceName).ConfigureAwait(false);
								}
							},
							async exception => await Global.WriteLogsAsync(Global.Logger, $"Http.{Global.ServiceName}", $"Error occurred while fetching an inter-communicate message: {exception.Message}", exception).ConfigureAwait(false)
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
					await Global.RegisterServiceAsync($"Http.{Global.ServiceName}").ConfigureAwait(false);
				},
				waitingTimes,
				exception => Global.Logger.LogError($"Cannot connect to API Gateway Router in period of times => {exception.Message}", exception),
				exception => Global.Logger.LogError($"Error occurred while connecting to API Gateway Router => {exception.Message}", exception)
			);
		}

		internal static void Disconnect(int waitingTimes = 1234)
			=> Task.Run(async () => await Handler.UnregisterSynchronizerAsync().ConfigureAwait(false))
				.ContinueWith(_ =>
				{
					Global.UnregisterService($"Http.{Global.ServiceName}", waitingTimes);
					Global.PrimaryInterCommunicateMessageUpdater?.Dispose();
					Global.SecondaryInterCommunicateMessageUpdater?.Dispose();
					Global.Disconnect(waitingTimes);
				}, TaskContinuationOptions.OnlyOnRanToCompletion)
				.ConfigureAwait(false)
				.GetAwaiter()
				.GetResult();

		static Task ProcessInterCommunicateMessageAsync(CommunicateMessage message)
		{
			if ("true".IsEquals(UtilityService.GetAppSetting("Files:NoSync")))
				return Task.CompletedTask;

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
					Handler.Synchronizer.SendRequest(node, message.Data.Get<string>("ServiceName"), message.Data.Get<string>("SystemID"), message.Data.Get<string>("Filename"), "true".IsEquals(message.Data.Get<string>("IsTemporary")));
			}

			return Task.CompletedTask;
		}

		static Task ProcessAPIGatewayCommunicateMessageAsync(CommunicateMessage message)
			=> message.Type.IsEquals("Service#RequestInfo")
				? Global.SendServiceInfoAsync($"Http.{Global.ServiceName}")
				: Task.CompletedTask;

		static IAsyncDisposable SynchronizerInstance { get; set; }

		static Synchronizer Synchronizer { get; } = new Synchronizer();

		internal static async Task RegisterSynchronizerAsync()
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

		public string EntityInfo { get; set; }

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
			=> !string.IsNullOrWhiteSpace(attachmentInfo.ContentType)
				&& (attachmentInfo.ContentType.IsStartsWith("image/") || attachmentInfo.ContentType.IsStartsWith("text/")
					|| attachmentInfo.ContentType.IsStartsWith("audio/") || attachmentInfo.ContentType.IsStartsWith("video/")
					|| attachmentInfo.ContentType.IsEquals("application/pdf") || attachmentInfo.ContentType.IsEquals("application/x-pdf")
					|| attachmentInfo.ContentType.IsStartsWith("application/x-shockwave-flash"));

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
			new[] { path, Path.Combine(path, "trash") }.Where(directory => !Directory.Exists(directory)).ForEach(directory => Directory.CreateDirectory(directory));
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
				attachmentInfo.EntityInfo = json.Get<string>("EntityInfo");
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

		public static Task<JToken> CreateAsync(this HttpContext context, AttachmentInfo attachmentInfo, CancellationToken cancellationToken = default)
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
				{ "EntityInfo", attachmentInfo.EntityInfo },
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

		public static async Task<AttachmentInfo> GetAsync(this HttpContext context, string id, CancellationToken cancellationToken = default)
			=> new AttachmentInfo
			{
				IsThumbnail = false
			}.Fill(string.IsNullOrWhiteSpace(id) ? null : await context.CallServiceAsync(context.GetRequestInfo("Attachment", "GET", new Dictionary<string, string>
			{
				{ "object-identity", id }
			}), cancellationToken, Global.Logger, "Http.Downloads").ConfigureAwait(false));

		public static Task UpdateAsync(this HttpContext context, AttachmentInfo attachmentInfo, string type, CancellationToken cancellationToken = default)
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

		public static Task<bool> CanDownloadAsync(this HttpContext context, AttachmentInfo attachmentInfo, CancellationToken cancellationToken = default)
			=> context.CanDownloadAsync(attachmentInfo.ServiceName, attachmentInfo.ObjectName, attachmentInfo.SystemID, attachmentInfo.EntityInfo, attachmentInfo.ObjectID, cancellationToken);

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