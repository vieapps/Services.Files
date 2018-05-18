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

using Microsoft.Extensions.Logging;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class Handler
	{
		readonly RequestDelegate _next;

		public Handler(RequestDelegate next) => this._next = next;

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
				await this._next.Invoke(context).ConfigureAwait(false);
			}
			catch (InvalidOperationException) { }
			catch (Exception ex)
			{
				Global.Logger.LogError($"Error occurred while invoking the next middleware: {ex.Message}", ex);
			}
		}

		internal async Task ProcessRequestAsync(HttpContext context)
		{
			// prepare
			context.Items["PipelineStopwatch"] = Stopwatch.StartNew();

			// authenticate
			if (context.User.Identity.IsAuthenticated)
				context.User = context.User.Identity is UserIdentity
					? new UserPrincipal(context.User.Identity as UserIdentity)
					: new UserPrincipal(context.User);

			// TO DO : automatic sign-in with authenticate ticket from passport
			// ......

			// request to favicon.ico file
			var requestPath = context.GetRequestPathSegments().First().ToLower();
			if (requestPath.IsEquals("favicon.ico"))
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());

			// request to static segments
			else if (Global.StaticSegments.Count > 0 && Global.StaticSegments.Contains(requestPath))
				await this.ProcessStaticRequestAsync(context).ConfigureAwait(false);

			// request to files
			else
			{
				if (Handler.Handlers.TryGetValue(requestPath, out Type type) && type != null)
					try
					{
						await (type.CreateInstance() as FileHttpHandler).ProcessRequestAsync(context,Global.CancellationTokenSource.Token).ConfigureAwait(false);
					}
					catch (OperationCanceledException) { }
					catch (Exception ex)
					{
						await Global.WriteLogsAsync(requestPath, $"Error occurred while processing with file: {ex.Message}", ex);
						context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
					}
				else
					context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
			}
		}

		internal async Task ProcessStaticRequestAsync(HttpContext context)
		{
			var requestUri = context.GetRequestUri();
			try
			{
				// prepare
				FileInfo fileInfo = null;
				var pathSegments = context.GetRequestPathSegments();
				var filePath = pathSegments[0].IsEquals("statics")
					? UtilityService.GetAppSetting("Path:StaticFiles", Handler.RootPath + "/data-files/statics")
					: Handler.RootPath;
				filePath += ("/" + string.Join("/", pathSegments)).Replace("/statics/", "/").Replace("//", "/").Replace(@"\", "/").Replace("/", Path.DirectorySeparatorChar.ToString());

				// headers to reduce traffic
				var eTag = "Static#" + $"{requestUri}".ToLower().GenerateUUID();
				if (eTag.IsEquals(context.Request.Headers["If-None-Match"].First()))
				{
					var isNotModified = true;
					var lastModifed = DateTime.Now;

					// last-modified
					if (!context.Request.Headers["If-Modified-Since"].First().Equals(""))
					{
						fileInfo = new FileInfo(filePath);
						if (fileInfo.Exists)
						{
							lastModifed = fileInfo.LastWriteTime;
							isNotModified = lastModifed <= context.Request.Headers["If-Modified-Since"].First().FromHttpDateTime();
						}
						else
							isNotModified = false;
					}

					// update header and stop
					if (isNotModified)
					{
						context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, lastModifed.ToUnixTimestamp(), "public", context.GetCorrelationID());
						if (Global.IsDebugLogEnabled)
							context.WriteLogs("StaticFiles", $"Response to request with status code 304 to reduce traffic ({filePath})");
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
				await context.WriteAsync(fileContent.ToArraySegment(), Global.CancellationTokenSource.Token).ConfigureAwait(false);
				if (Global.IsDebugLogEnabled)
					context.WriteLogs("StaticFiles", $"Response to request successful ({filePath} - {fileInfo.Length:#,##0} bytes)");
			}
			catch (Exception ex)
			{
				context.WriteLogs("StaticFiles", $"Error occurred while processing [{requestUri}]", ex);
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
					foreach (XmlNode node in nodes)
					{
						var info = node.ToJson();
						var keyName = info.Get<string>("key");
						var typeName = info.Get<string>("type");
						if (!string.IsNullOrWhiteSpace(keyName) && !string.IsNullOrWhiteSpace(typeName) && !Handler.Handlers.ContainsKey(keyName))
							try
							{
								var type = Type.GetType(typeName);
								if (type != null && type.CreateInstance() is FileHttpHandler)
									Handler.Handlers[keyName] = type;
							}
							catch { }
					}
		}

		internal static string RootPath { get; set; }

		static string _UserAvatarFilesPath = null;

		internal static string UserAvatarFilesPath => Handler._UserAvatarFilesPath ?? (Handler._UserAvatarFilesPath = UtilityService.GetAppSetting("Path:UserAvatars", Path.Combine(Handler.RootPath, "data-files", "user-avatars")));

		static string _DefaultUserAvatarFilePath = null;

		internal static string DefaultUserAvatarFilePath => Handler._DefaultUserAvatarFilePath ?? (Handler._DefaultUserAvatarFilePath = UtilityService.GetAppSetting("Path:DefaultUserAvatar", Path.Combine(Handler.UserAvatarFilesPath, "@default.png")));

		static string _AttachmentFilesPath = null;

		internal static string AttachmentFilesPath => Handler._AttachmentFilesPath ?? (Handler._AttachmentFilesPath = UtilityService.GetAppSetting("Path:Attachments", Path.Combine(Handler.RootPath, "data-files", "attachments")));

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


		internal static async Task UpdateCounterAsync(HttpContext context, Attachment attachment)
		{
			var session = Global.GetSession(context);
			await Task.Delay(0);
		}

		internal static string GetTransferToPassportUrl(HttpContext context = null, string url = null)
		{
			context = context ?? Global.CurrentHttpContext;
			url = url ?? $"{context.GetRequestUri()}";
			return Handler.UsersHttpUri + "validator"
				+ "?aut=" + (UtilityService.NewUUID.Left(5) + "-" + (context.User.Identity.IsAuthenticated ? "ON" : "OFF")).Encrypt(Global.EncryptionKey).ToBase64Url(true)
				+ "&uid=" + (context.User.Identity.IsAuthenticated ? (context.User.Identity as UserIdentity).ID : "").Encrypt(Global.EncryptionKey).ToBase64Url(true)
				+ "&uri=" + url.Encrypt(Global.EncryptionKey).ToBase64Url(true)
				+ "&rdr=" + UtilityService.GetRandomNumber().ToString().Encrypt(Global.EncryptionKey).ToBase64Url(true);
		}
		#endregion

		#region Helper: WAMP connections & real-time updaters
		internal static void OpenWAMPChannels(int waitingTimes = 6789)
		{
			var routerInfo = WAMPConnections.GetRouterInfo();
			Global.Logger.LogInformation($"Attempting to connect to WAMP router [{routerInfo.Item1}{(routerInfo.Item1.EndsWith("/") ? "" : "/")}{routerInfo.Item2}]");
			Global.OpenWAMPChannels(
				(sender, args) =>
				{
					Global.Logger.LogInformation($"Incomming channel to WAMP router is established - Session ID: {args.SessionId}");
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

		internal static Task<bool> CanUploadAsync(this HttpContext context, string serviceName, string systemID, string definitionID, string objectID)
			=> context.User != null && context.User.Identity != null && context.User.Identity is UserIdentity
				? (context.User.Identity as UserIdentity).CanContributeAsync(serviceName, systemID, definitionID, objectID)
				: Task.FromResult(false);

		internal static Task<bool> CanDownloadAsync(this HttpContext context, string serviceName, string systemID, string definitionID, string objectID)
			=> context.User != null && context.User.Identity != null && context.User.Identity is UserIdentity
				? (context.User.Identity as UserIdentity).CanDownloadAsync(serviceName, systemID, definitionID, objectID)
				: Task.FromResult(false);

		internal static Task<bool> CanDeleteAsync(this HttpContext context, string serviceName, string systemID, string definitionID, string objectID)
			=> context.User != null && context.User.Identity != null && context.User.Identity is UserIdentity
				? (context.User.Identity as UserIdentity).CanEditAsync(serviceName, systemID, definitionID, objectID)
				: Task.FromResult(false);

		internal static Task<bool> CanRestoreAsync(this HttpContext context, string serviceName, string systemID, string definitionID, string objectID)
			=> context.User != null && context.User.Identity != null && context.User.Identity is UserIdentity
				? (context.User.Identity as UserIdentity).CanEditAsync(serviceName, systemID, definitionID, objectID)
				: Task.FromResult(false);
	}
	#endregion

}