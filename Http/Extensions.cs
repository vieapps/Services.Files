#region Related components
using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	internal static partial class ServiceExtensions
	{
		public static bool IsDebugLogEnabled(this HttpContext context)
			=> Global.IsDebugLogEnabled || context.ContainsKey("x-logs");

		public static bool IsBypassCacheRequested(this HttpContext context)
			=> context.ContainsKey("x-no-cache") || context.ContainsKey("x-bypass-cache") || context.ContainsKey("x-force-cache");

		static bool IsReadable(this string mimeType)
			=> mimeType.IsStartsWith("image/") || mimeType.IsStartsWith("text/")
				|| mimeType.IsStartsWith("audio/") || mimeType.IsStartsWith("video/")
				|| mimeType.IsEquals("application/pdf") || mimeType.IsEquals("application/x-pdf")
				|| mimeType.IsEquals("application/json") || mimeType.IsEquals("application/javascript");

		public static bool IsReadable(this AttachmentInfo attachment)
			=> (attachment.ContentType ?? "").IsReadable();

		public static bool IsThumbnail(this string mimeType)
			=> mimeType.IsStartsWith("image/") && (mimeType.IsEndsWith("/jpeg") || mimeType.IsEndsWith("/png") || mimeType.IsEndsWith("/webp"));

		public static bool IsWebP(this AttachmentInfo attachment)
			=> (attachment.ContentType ?? "").IsEndsWith("image/webp") || (attachment.Filename ?? "").IsEndsWith(".webp");

		public static string GetContentDisposition(this AttachmentInfo attachment, bool alwaysUseDisposition = false)
			=> (alwaysUseDisposition || !attachment.IsReadable()) && !string.IsNullOrWhiteSpace(attachment.Filename) ? attachment.Filename : null;

		public static AttachmentInfo Fill(this AttachmentInfo attachment, JToken json)
		{
			if (json != null)
			{
				attachment.ID = json.Get<string>("ID");
				attachment.ServiceName = json.Get<string>("ServiceName");
				attachment.ObjectName = json.Get<string>("ObjectName");
				attachment.SystemID = json.Get<string>("SystemID");
				attachment.EntityInfo = json.Get<string>("EntityInfo");
				attachment.ObjectID = json.Get<string>("ObjectID");
				attachment.Filename = json.Get<string>("Filename");
				attachment.Size = json.Get<long>("Size");
				attachment.ContentType = json.Get<string>("ContentType");
				attachment.IsTemporary = json.Get<bool>("IsTemporary");
				if (!attachment.IsThumbnail)
				{
					attachment.IsShared = json.Get<bool>("IsShared");
					attachment.IsTracked = json.Get<bool>("IsTracked");
					attachment.Title = json.Get<string>("Title");
					attachment.Description = json.Get<string>("Description");
				}
			}
			return attachment;
		}

		public static AttachmentInfo Normalize(this AttachmentInfo attachment)
		{
			if (!string.IsNullOrWhiteSpace(attachment.Filename) && attachment.Filename.Length > 200)
			{
				var fileInfo = new FileInfo(attachment.Filename);
				attachment.Filename = fileInfo.Name.Left(fileInfo.Name.Length - fileInfo.Extension.Length).Left(167) + "-" + UtilityService.NewUUID + fileInfo.Extension;
			}
			return attachment;
		}

		public static JObject ToJson(this AttachmentInfo attachment, Action<JObject> onCompleted = null)
		{
			var json = new JObject
			{
				{ "ID", attachment.ID },
				{ "ServiceName", attachment.ServiceName?.ToLower() },
				{ "ObjectName", attachment.ObjectName?.ToLower() },
				{ "SystemID", attachment.SystemID?.ToLower() },
				{ "EntityInfo", attachment.EntityInfo },
				{ "ObjectID", attachment.ObjectID?.ToLower() },
				{ "Size", attachment.Size },
				{ "Filename", attachment.Filename },
				{ "ContentType", attachment.ContentType },
				{ "IsTemporary", attachment.IsTemporary },
				{ "IsShared", attachment.IsShared },
				{ "IsTracked", attachment.IsTracked },
				{ "Title", attachment.Title },
				{ "Description", attachment.Description }
			};
			onCompleted?.Invoke(json);
			return json;
		}

		public static Task WriteAsync(this HttpContext context, JToken json, Newtonsoft.Json.Formatting format, Dictionary<string, string> headers, CancellationToken cancellationToken)
			=> context.WriteAsync(json.ToBytes(format), "application/json", new Dictionary<string, string>(headers ?? []) { ["Cache-Control"] = context.GetHttpCacheControl(true) }, cancellationToken);

		public static Task WriteAsync(this HttpContext context, JToken json, Newtonsoft.Json.Formatting format, CancellationToken cancellationToken)
			=> context.WriteAsync(json, format, null, cancellationToken);

		#region Working with files & directories
		public static string GetFileName(this AttachmentInfo attachment)
			=> $"{(attachment.IsThumbnail ? "" : $"{attachment.ID}-")}{attachment.Filename.Replace("+", " ").Replace("%20", " ")}";

		public static string GetDirectoryPath(this AttachmentInfo attachment, bool isTemporary = false, string tempFilesPath = null)
			=> isTemporary || attachment.IsTemporary
				? tempFilesPath ?? Handler.TempFilesPath
				: Path.Combine(Handler.AttachmentFilesPath, string.IsNullOrWhiteSpace(attachment.SystemID) || !attachment.SystemID.IsValidUUID() ? attachment.ServiceName.ToLower() : attachment.SystemID.ToLower());

		public static string GetFilePath(this AttachmentInfo attachment, bool isTemporary = false, string tempFilesPath = null)
			=> Path.Combine(attachment.GetDirectoryPath(isTemporary, tempFilesPath), attachment.GetFileName());

		public static string GetTrashFilePath(this AttachmentInfo attachmentInfo)
			=> Path.Combine(attachmentInfo.GetDirectoryPath(), "trash", attachmentInfo.GetFileName());

		public static AttachmentInfo CopyFile(this AttachmentInfo attachment, ILogger logger = null, string objectName = null, string tempFilesPath = null)
		{
			var source = attachment.GetFilePath(true, tempFilesPath);
			var destination = attachment.PrepareDirectories().GetFilePath();

			var fileInfo = new FileInfo(source);
			if (fileInfo.Exists)
				try
				{
					fileInfo.CopyTo(destination, true);
					if (Global.IsDebugLogEnabled)
						Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Synchronizers", $"Successfully copy a file [{source} => {destination}]");
				}
				catch (Exception ex)
				{
					Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Synchronizers", $"Error occurred while copying a file\r\nFile: {source} => {destination}\r\nError: {ex.Message}", ex, Global.ServiceName, LogLevel.Error);
				}
			else if (Global.IsDebugLogEnabled)
				Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Synchronizers", $"Cannot copy a doesn't existing file of a legacy system [{source}]");

			return attachment;
		}

		public static AttachmentInfo DeleteFile(this AttachmentInfo attachment, bool isTemporary, ILogger logger = null, string objectName = null)
		{
			var fileInfo = new FileInfo(attachment.GetFilePath(isTemporary));
			if (fileInfo.Exists)
				try
				{
					fileInfo.Delete();
					if (Global.IsDebugLogEnabled)
						Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Uploads", $"Successfully delete a file [{fileInfo.FullName}]");
				}
				catch (Exception ex)
				{
					Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Uploads", $"Error occurred while deleting a file => {ex.Message}", ex, Global.ServiceName, LogLevel.Error);
				}
			return attachment;
		}

		public static AttachmentInfo MoveFile(this AttachmentInfo attachment, ILogger logger = null, string objectName = null, string correlationID = null, bool moveDestinationIntoTrashIfExists = false)
		{
			var source = attachment.GetFilePath(true);
			var destination = attachment.GetFilePath();
			var fileInfo = new FileInfo(source);
			if (fileInfo.Exists)
				try
				{
					if (moveDestinationIntoTrashIfExists && File.Exists(destination))
						attachment.MoveFileIntoTrash(logger, objectName);
					fileInfo.MoveTo(destination);
					if (Global.IsDebugLogEnabled)
						Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Uploads", $"Successfully move a file [{source} => {destination}]", null, Global.ServiceName, LogLevel.Debug, correlationID);
				}
				catch (Exception ex)
				{
					Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Uploads", $"Error occurred while moving a file\r\nFile: {source} => {destination}\r\nError: {ex.Message}", ex, Global.ServiceName, LogLevel.Error, correlationID);
				}
			return attachment;
		}

		public static AttachmentInfo MoveFileIntoTrash(this AttachmentInfo attachment, ILogger logger = null, string objectName = null, string correlationID = null, bool deleteOnUnsucces = true)
		{
			if (attachment.IsTemporary)
				return attachment;

			var source = attachment.GetFilePath();
			var destination = attachment.GetTrashFilePath();

			var fileInfo = new FileInfo(source);
			if (fileInfo.Exists)
				try
				{
					var trashDirectory = Path.Combine(attachment.GetDirectoryPath(), "trash");
					if (!Directory.Exists(trashDirectory))
						Directory.CreateDirectory(trashDirectory);

					if (File.Exists(destination))
						File.Delete(destination);

					fileInfo.MoveTo(destination);
					File.SetLastAccessTime(destination, DateTime.Now);
					if (Global.IsDebugLogEnabled)
						Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Uploads", $"Successfully move a file into trash [{source} => {destination}]", null, Global.ServiceName, LogLevel.Debug, correlationID);
				}
				catch (Exception ex)
				{
					Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Uploads", $"Error occurred while moving a file into trash\r\nFile: {source} => {destination}\r\nError: {ex.Message}", ex, Global.ServiceName, LogLevel.Error, correlationID);
					if (deleteOnUnsucces)
						return attachment.DeleteFile(false, logger, objectName);
				}

			return attachment;
		}

		public static AttachmentInfo PrepareDirectories(this AttachmentInfo attachment)
		{
			var path = attachment.GetDirectoryPath();
			new[] { path, Path.Combine(path, "trash") }.Where(directory => !Directory.Exists(directory)).ForEach(directory => Directory.CreateDirectory(directory));
			return attachment;
		}
		#endregion

		#region Working with meta info
		public static async Task<JToken> CreateAsync(this HttpContext context, AttachmentInfo attachment, CancellationToken cancellationToken = default)
		{
			var ticket = await Global.RpcGate.TryEnterAsync(cancellationToken).ConfigureAwait(false);
			if (ticket == null)
			{
				Global.Statistics.RpcRejected();
				throw new SystemBusyException();
			}
			Global.Statistics.RpcEntered();
			var stopwatch = Stopwatch.StartNew();
			try
			{
				using (ticket.Value)
				{
					var requestInfo = context.GetRequestInfo(attachment.IsThumbnail ? "Thumbnail" : "Attachment", "POST", new Dictionary<string, string>
					{
						{ "object-identity", attachment.ID },
						{ "x-object-title", attachment.Title }
					}, attachment.ToJson().AsString());
					return await context.CallServiceAsync(requestInfo, cancellationToken, Global.Logger, "Uploads").ConfigureAwait(false);
				}
			}
			catch (Exception)
			{
				throw;
			}
			finally
			{
				Global.Statistics.RpcCompleted(stopwatch);
			}
		}
		public static async Task<AttachmentInfo> GetAsync(this HttpContext context, string id, CancellationToken cancellationToken = default)
			=> new AttachmentInfo
			{
				IsThumbnail = false
			}.Fill(string.IsNullOrWhiteSpace(id) ? null : await context.CallServiceAsync(context.GetRequestInfo("Attachment", "GET", new Dictionary<string, string>
			{
				{ "object-identity", id }
			}), cancellationToken, Global.Logger, "Downloads").ConfigureAwait(false));

		public static async Task UpdateAsync(this HttpContext context, AttachmentInfo attachment, string type, CancellationToken cancellationToken = default)
		{
			if (!attachment.IsThumbnail && !attachment.IsTemporary && !string.IsNullOrWhiteSpace(attachment.ID))
			{
				var ticket = await Global.RpcGate.TryEnterAsync(cancellationToken).ConfigureAwait(false);
				if (ticket == null)
				{
					Global.Statistics.RpcRejected();
					throw new SystemBusyException();
				}
				Global.Statistics.RpcEntered();
				var stopwatch = Stopwatch.StartNew();
				try
				{
					using (ticket.Value)
					{
						await Task.WhenAll
						(
							context.CallServiceAsync(context.GetRequestInfo("Attachment", "GET", new Dictionary<string, string>
								{
								{ "object-identity", "counters" },
								{ "x-object-id", attachment.ID },
								{ "x-user-id", context.User.Identity.Name }
							}), cancellationToken, Global.Logger, "Downloads"),
							attachment.IsTracked
								? context.CallServiceAsync(context.GetRequestInfo("Attachment", "GET", new Dictionary<string, string>
									{
										{ "object-identity", "trackers" },
										{ "x-object-id", attachment.ID },
										{ "x-user-id", context.User.Identity.Name },
										{ "x-refer", context.GetReferUrl() },
										{ "x-origin", context.GetOriginUri()?.ToString() }
									}), cancellationToken, Global.Logger, "Downloads")
								: Task.CompletedTask,
								new CommunicateMessage(attachment.ServiceName)
								{
									Type = $"File#{type}",
									Data = new JObject
									{
										{ "x-object-id", attachment.ID },
										{ "x-user-id", context.User.Identity.Name },
										{ "x-refer", context.GetReferUrl() },
										{ "x-origin", context.GetOriginUri()?.ToString() }
									}
							}.PublishAsync(Global.Logger, "Downloads")
						).ConfigureAwait(false);
					}
				}
				catch (Exception)
				{
					throw;
				}
				finally
				{
					Global.Statistics.RpcCompleted(stopwatch);
				}
			}
		}

		public static async Task<bool> CanDownloadAsync(this HttpContext context, AttachmentInfo attachment, CancellationToken cancellationToken)
		{
			var ticket = await Global.RpcGate.TryEnterAsync(cancellationToken).ConfigureAwait(false);
			if (ticket == null)
			{
				Global.Statistics.RpcRejected();
				throw new SystemBusyException();
			}
			Global.Statistics.RpcEntered();
			var stopwatch = Stopwatch.StartNew();
			try
			{
				using (ticket.Value)
				{
					return await context.CanDownloadAsync(attachment.ServiceName, attachment.ObjectName, attachment.SystemID, attachment.EntityInfo, attachment.ObjectID, cancellationToken).ConfigureAwait(false);
				}
			}
			catch (Exception)
			{
				throw;
			}
			finally
			{
				Global.Statistics.RpcCompleted(stopwatch);
			}
		}

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
				["Node"] = Global.NodeID,
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
			return new RequestInfo(session, Global.ServiceName, objectName, verb, query, header, body, extra, context.GetCorrelationID());
		}
		#endregion

		#region Working with cache
		public static string GetCacheKey(this (string id, int index, string format, int width, int height, bool asBig) info, string prefix = "thumbnail")
			=> $"{(string.IsNullOrWhiteSpace(prefix) ? "thumbnail" : prefix)}#" + $"{info.id}@{info.index}:{info.format}:{info.width}:{info.height}:{info.asBig}".ToLower().GenerateUUID();

		public static string GetCacheKey(this AttachmentInfo attachment, string prefix)
			=> $"{(string.IsNullOrWhiteSpace(prefix) ? "file" : prefix)}#{attachment.ID}".ToLower();

		public static string GetCacheKey(this AttachmentInfo attachment, int index = -1, string format = null, int width = 0, int height = 0, bool asBig = true)
			=> (attachment.IsThumbnail ? attachment.ObjectID : attachment.ID, attachment.IsThumbnail ? index < 0 ? attachment.Filename.Length == 36 ? 0 : attachment.Filename.Right(5).Replace(".jpg", "").As<int>() : index : 0, format ?? "Jpeg", width, height, asBig).GetCacheKey();

		public static bool IsCacheableImage(this AttachmentInfo attachment, bool allowTemporary = false)
			=> (allowTemporary || !attachment.IsTemporary) && !string.IsNullOrWhiteSpace(attachment.ContentType) && attachment.ContentType.IsStartsWith("image/") && !attachment.ContentType.IsContains("icon") && !attachment.ContentType.IsContains("svg");

		public static async Task<List<string>> PrepareCacheAsync(this AttachmentInfo attachment, int index, string format, byte[] original, long lastModified, int width = 0, int height = 0, bool asBig = true)
		{
			if (original == null || original.Length < 1 || lastModified < 1)
			{
				var fileInfo = new FileInfo(attachment.GetFilePath());
				original = await fileInfo.ReadAsBinaryAsync(Global.CancellationToken).ConfigureAwait(false);
				lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
			}

			byte[] thumbnail;
			try
			{
				thumbnail = await original.GenerateAsync(format, width, height, asBig, attachment.IsWebP(), Global.CancellationToken).ConfigureAwait(false);
			}
			catch
			{
				thumbnail = await original.ConvertAsync(format, Global.CancellationToken).ConfigureAwait(false);
			}

			var cacheKey = attachment.GetCacheKey(index, format, width, height, asBig);
			var cacheKeys = attachment.IsThumbnail ? new List<string> { cacheKey, $"{cacheKey}:time" } : [];

			await Task.WhenAll
			(
				Global.Cache.SetAsFragmentsAsync(cacheKey, thumbnail, Global.CancellationToken),
				Global.Cache.SetAsync($"{cacheKey}:time", lastModified, Global.CancellationToken)
			).ConfigureAwait(false);

			if (!"WebP".IsEquals(format))
			{
				try
				{
					thumbnail = await original.GenerateAsync("WebP", width, height, asBig, attachment.IsWebP(), Global.CancellationToken).ConfigureAwait(false);
				}
				catch
				{
					thumbnail = await original.ConvertAsync("WebP", Global.CancellationToken).ConfigureAwait(false);
				}

				cacheKey = attachment.GetCacheKey(index, "WebP", width, height, asBig);
				cacheKeys = attachment.IsThumbnail ? [.. cacheKeys, cacheKey, $"{cacheKey}:time"] : cacheKeys;

				await Task.WhenAll
				(
					Global.Cache.SetAsFragmentsAsync(cacheKey, thumbnail, Global.CancellationToken),
					Global.Cache.SetAsync($"{cacheKey}:time", lastModified, Global.CancellationToken)
				).ConfigureAwait(false);

				if (attachment.IsThumbnail && width < 1 && Handler.PrepareCache)
				{
					cacheKeys = [.. cacheKeys, .. ServiceExtensions.Widths.Select(variant => attachment.GetCacheKey(index, "WebP", variant, 0, asBig)).SelectMany(key => new[] { key, $"{key}:time" })];
					await ServiceExtensions.Widths.ForEachAsync(async variant =>
					{
						try
						{
							thumbnail = await original.GenerateAsync("WebP", variant, 0, asBig, false, Global.CancellationToken).ConfigureAwait(false);
						}
						catch
						{
							thumbnail = await original.ConvertAsync("WebP", Global.CancellationToken).ConfigureAwait(false);
						}
						cacheKey = attachment.GetCacheKey(index, "WebP", variant, 0, asBig);
						await Task.WhenAll
						(
							Global.Cache.SetAsFragmentsAsync(cacheKey, thumbnail, Global.CancellationToken),
							Global.Cache.SetAsync($"{cacheKey}:time", lastModified, Global.CancellationToken)
						).ConfigureAwait(false);
					}, true, false).ConfigureAwait(false);
				}
			}

			await Global.Cache.AddSetMembersAsync($"{attachment.ObjectID}:images", cacheKeys, Global.CancellationToken).ConfigureAwait(false);
			return cacheKeys;
		}

		static List<int> Widths => [720, 1024, 1280];

		public static Task<List<string>> PrepareCacheAsync(this AttachmentInfo attachment)
			=> attachment.PrepareCacheAsync(-1, null, null, 0, 0, 0, true);

		public static async Task<List<string>> PrepareCacheAsync(this AttachmentInfo attachment, byte[] data, long lastModified = 0)
		{
			if (data == null || data.Length < 1 || lastModified <= 0)
			{
				using var fileStream = new FileStream(attachment.GetFilePath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, TextFileReader.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
				using var webpStream = await fileStream.ConvertAsync("WebP", !attachment.Filename.IsEndsWith(".png"), !attachment.Filename.IsEndsWith(".png") && ImageExtensions.ResizeBigWebpImage, Global.CancellationToken).ConfigureAwait(false);
				data = webpStream.ToBytes();
				lastModified = lastModified > 0 ? lastModified : File.GetLastWriteTimeUtc(attachment.GetFilePath()).ToUnixTimestamp();
			}

			var cacheKey = attachment.GetCacheKey("webp");
			var cacheKeys = new List<string> { cacheKey, $"{cacheKey}:time" };
			await Task.WhenAll
			(
				Global.Cache.SetAsFragmentsAsync(cacheKey, data, Global.CancellationToken),
				Global.Cache.SetAsync($"{cacheKey}:time", lastModified, Global.CancellationToken),
				Global.Cache.AddSetMembersAsync($"{attachment.ObjectID}:images", cacheKeys, Global.CancellationToken)
			).ConfigureAwait(false);
			return cacheKeys;
		}

		public static void SendPurgeCacheRequest(this AttachmentInfo attachment, Uri requestURI)
			=> new CommunicateMessage("APIGateway")
			{
				Type = "PurgeCache",
				Data = new JObject
				{
					["SystemID"] = attachment.SystemID,
					["URLs"] = new[] { requestURI.GetUrl() }.ToJArray()
				}
			}.Send();
		#endregion

		#region Session state
		public static void SendSessionState(this HttpContext context, string systemID = null, bool online = true)
		{
			if (Handler.TrackSessions)
				context.GetSession().SendSessionState($"{Global.ServiceName}.HTTP", $"{context.Request.Method} {context.GetRequestUrl()}", systemID, online, Handler.TrackStatistics, false, message => message.Data["Crawler"] = context.IsCrawlerbot(), null, context.GetCorrelationID());
			else if (Handler.TrackStatistics)
				context.GetSession().TrackStatistics(context.GetCorrelationID());
		}

		public static void SendSessionState(this RequestInfo requestInfo, string systemID = null, bool online = true)
		{
			if (Handler.TrackSessions)
				requestInfo.Session.SendSessionState($"{Global.ServiceName}.HTTP", $"{requestInfo.Verb} {requestInfo.GetURI()}", systemID, online, Handler.TrackStatistics, false, message => message.Data["Crawler"] = requestInfo.IsCrawlerbot(), null, requestInfo.CorrelationID);
			else if (Handler.TrackStatistics)
				requestInfo.TrackStatistics();
		}
		#endregion

	}

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

	public struct WatermarkInfo
	{
		public WatermarkInfo(string data, string position, Point offset)
		{
			this.Data = data;
			this.Position = position;
			this.Offset = offset;
		}
		public string Data { get; set; }
		public string Position { get; set; }
		public Point Offset { get; set; }
	}
}