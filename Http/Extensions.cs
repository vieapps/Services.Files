#region Related components
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	internal static class ServiceExtensions
	{
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

		public static string ToString(this AttachmentInfo attachment, Action<JObject> onCompleted)
			=> attachment.ToJson(onCompleted).ToString(Newtonsoft.Json.Formatting.None);

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
			var fileInfo = new FileInfo(source);
			if (fileInfo.Exists)
				try
				{
					var destination = attachment.PrepareDirectories().GetFilePath();
					fileInfo.CopyTo(destination, true);
					if (Global.IsDebugLogEnabled)
						Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Synchronizers", $"Successfully copy a file [{source} => {destination}]");
				}
				catch (Exception ex)
				{
					Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Synchronizers", $"Error occurred while copying a file => {ex.Message}", ex, Global.ServiceName, LogLevel.Error);
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
			var fileInfo = new FileInfo(source);
			if (fileInfo.Exists)
				try
				{
					var destination = attachment.GetFilePath();
					if (moveDestinationIntoTrashIfExists && File.Exists(destination))
						attachment.MoveFileIntoTrash(logger, objectName);
					fileInfo.MoveTo(destination);
					if (Global.IsDebugLogEnabled)
						Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Uploads", $"Successfully move a file [{source} => {destination}]", null, Global.ServiceName, LogLevel.Debug, correlationID);
				}
				catch (Exception ex)
				{
					Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Uploads", $"Error occurred while moving a file => {ex.Message}", ex, Global.ServiceName, LogLevel.Error, correlationID);
				}
			return attachment;
		}

		public static AttachmentInfo MoveFileIntoTrash(this AttachmentInfo attachment, ILogger logger = null, string objectName = null, string correlationID = null, bool deleteOnUnsucces = true)
		{
			if (attachment.IsTemporary)
				return attachment;

			var source = attachment.GetFilePath();
			var fileInfo = new FileInfo(source);
			if (fileInfo.Exists)
				try
				{
					var destination = attachment.GetTrashFilePath();
					if (File.Exists(destination))
						File.Delete(destination);
					fileInfo.MoveTo(destination);
					File.SetLastAccessTime(destination, DateTime.Now);
					if (Global.IsDebugLogEnabled)
						Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Uploads", $"Successfully move a file into trash [{source} => {destination}]", null, Global.ServiceName, LogLevel.Debug, correlationID);
				}
				catch (Exception ex)
				{
					Global.WriteLogs(logger ?? Global.Logger, objectName ?? "Uploads", $"Error occurred while moving a file into trash => {ex.Message}", ex, Global.ServiceName, LogLevel.Error, correlationID);
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
		public static Task<JToken> CreateAsync(this HttpContext context, AttachmentInfo attachment, CancellationToken cancellationToken = default)
			=> context.CallServiceAsync(context.GetRequestInfo(attachment.IsThumbnail ? "Thumbnail" : "Attachment", "POST", new Dictionary<string, string>
			{
				{ "object-identity", attachment.ID },
				{ "x-object-title", attachment.Title }
			}, attachment.ToString(null)), cancellationToken, Global.Logger, "Uploads");

		public static async Task<AttachmentInfo> GetAsync(this HttpContext context, string id, CancellationToken cancellationToken = default)
			=> new AttachmentInfo
			{
				IsThumbnail = false
			}.Fill(string.IsNullOrWhiteSpace(id) ? null : await context.CallServiceAsync(context.GetRequestInfo("Attachment", "GET", new Dictionary<string, string>
			{
				{ "object-identity", id }
			}), cancellationToken, Global.Logger, "Downloads").ConfigureAwait(false));

		public static Task UpdateAsync(this HttpContext context, AttachmentInfo attachment, string type, CancellationToken cancellationToken = default)
			=> attachment.IsThumbnail || attachment.IsTemporary || string.IsNullOrWhiteSpace(attachment.ID)
				? Task.CompletedTask
				: Task.WhenAll
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
				);

		public static Task<bool> CanDownloadAsync(this HttpContext context, AttachmentInfo attachment, CancellationToken cancellationToken = default)
			=> context.CanDownloadAsync(attachment.ServiceName, attachment.ObjectName, attachment.SystemID, attachment.EntityInfo, attachment.ObjectID, cancellationToken);

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

		#region Working with images
		public static async Task<MemoryStream> ConvertAsync(this MemoryStream imageStream, ImageFormat format, CancellationToken cancellationToken)
		{
			imageStream.Seek(0, SeekOrigin.Begin);
			using var image = await SixLabors.ImageSharp.Image.LoadAsync(imageStream, cancellationToken).ConfigureAwait(false);
			var outputStream = UtilityService.CreateMemoryStream();
			await image.SaveAsync(outputStream, format == ImageFormat.Webp ? new SixLabors.ImageSharp.Formats.Webp.WebpEncoder() : format == ImageFormat.Bmp ? new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder() : format == ImageFormat.Png ? new SixLabors.ImageSharp.Formats.Png.PngEncoder() : new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder(), cancellationToken).ConfigureAwait(false);
			outputStream.Seek(0, SeekOrigin.Begin);
			return outputStream;
		}

		public static async Task<byte[]> ConvertAsync(this byte[] bytes, ImageFormat format, CancellationToken cancellationToken)
		{
			using var inputStream = bytes.ToMemoryStream();
			using var outputStream = await inputStream.ConvertAsync(format, cancellationToken).ConfigureAwait(false);
			return outputStream.ToBytes();
		}

		public static MemoryStream ToMemoryStream(this Image image, ImageFormat format = null)
		{
			var stream = UtilityService.CreateMemoryStream();
			image.Save(stream, format ?? ImageFormat.Bmp);
			return stream;
		}

		static MemoryStream Generate(this Image image, int width, int height, bool asBig)
		{
			if (height < 1)
			{
				height = image.Height * width / image.Width;
				if (height < 1)
					height = image.Height;
			}
			else if (width < 1)
			{
				width = image.Width * height / image.Height;
				if (width < 1)
					width = image.Width;
			}
			if (asBig)
			{
				using var bitmap = new Bitmap(width, height);
				using var graphics = Graphics.FromImage(bitmap);
				graphics.SmoothingMode = SmoothingMode.HighQuality;
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
				graphics.DrawImage(image, new Rectangle(0, 0, width, height));
				return bitmap.ToMemoryStream();
			}
			using var thumbnail = image.GetThumbnailImage(width, height, null, IntPtr.Zero);
			return thumbnail.ToMemoryStream();
		}

		public static async Task<byte[]> GenerateAsync(this byte[] bytes, ImageFormat format, int width, int height, bool asBig, bool isWebP, CancellationToken cancellationToken)
		{
			if (width > 0 || height > 0)
			{
				using var inputStream = bytes.ToMemoryStream();
				using var imageStream = await inputStream.ConvertAsync(ImageFormat.Bmp, cancellationToken).ConfigureAwait(false);
				using var image = Image.FromStream(imageStream);
				using var thumbnailStream = image.Generate(width, height, asBig);
				using var outputStream = await thumbnailStream.ConvertAsync(format, cancellationToken).ConfigureAwait(false);
				return outputStream.ToBytes();
			}
			return isWebP && format == ImageFormat.Webp ? bytes : await bytes.ConvertAsync(format, cancellationToken).ConfigureAwait(false);
		}

		public static async Task<byte[]> GenerateAsync(this Exception ex, int width, int height, CancellationToken cancellationToken)
		{
			using var bitmap = new Bitmap(width, height, PixelFormat.Format16bppRgb555);
			using var graphics = Graphics.FromImage(bitmap);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.Clear(Color.White);
			graphics.DrawString(ex.Message, new Font("Arial", 16, FontStyle.Bold), SystemBrushes.WindowText, new PointF(10, 40));
			using var bitmapStream = bitmap.ToMemoryStream();
			using var outputStream = await bitmapStream.ConvertAsync(ImageFormat.Webp, cancellationToken).ConfigureAwait(false);
			return outputStream.ToBytes();
		}
		#endregion

		#region Working with image cache
		public static string GetCacheKey(this (string id, int index, ImageFormat format, int width, int height, bool asBig) info)
			=> "thumbnail#" + $"{info.id}@{info.index}:{info.format}:{info.width}:{info.height}:{info.asBig}".ToLower().GenerateUUID();

		public static string GetCacheKey(this AttachmentInfo attachment, int index = -1, ImageFormat format = null, int width = 0, int height = 0, bool asBig = true)
			=> (attachment.IsThumbnail ? attachment.ObjectID : attachment.ID, attachment.IsThumbnail ? index < 0 ? attachment.Filename.Length == 36 ? 0 : attachment.Filename.Right(5).Replace(".jpg", "").As<int>() : index : 0, format ?? ImageFormat.Jpeg, width, height, asBig).GetCacheKey();

		public static string GetCacheKey(this AttachmentInfo attachment, string prefix)
			=> $"{prefix ?? "file"}#{attachment.ID}".ToLower();

		public static async Task<List<string>> PrepareCacheAsync(this AttachmentInfo attachment, int index, ImageFormat format, byte[] original, long lastModified, int width = 0, int height = 0, bool asBig = true)
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
				Global.Cache.SetAsFragmentsAsync(cacheKey, thumbnail, 0, Global.CancellationToken),
				Global.Cache.SetAsync($"{cacheKey}:time", lastModified, 0, Global.CancellationToken)
			).ConfigureAwait(false);

			if (format != ImageFormat.Webp)
			{
				try
				{
					thumbnail = await original.GenerateAsync(ImageFormat.Webp, width, height, asBig, attachment.IsWebP(), Global.CancellationToken).ConfigureAwait(false);
				}
				catch
				{
					thumbnail = await original.ConvertAsync(ImageFormat.Webp, Global.CancellationToken).ConfigureAwait(false);
				}

				cacheKey = attachment.GetCacheKey(index, ImageFormat.Webp, width, height, asBig);
				cacheKeys = attachment.IsThumbnail ? [.. cacheKeys, cacheKey, $"{cacheKey}:time"] : cacheKeys;

				await Task.WhenAll
				(
					Global.Cache.SetAsFragmentsAsync(cacheKey, thumbnail, 0, Global.CancellationToken),
					Global.Cache.SetAsync($"{cacheKey}:time", lastModified, 0, Global.CancellationToken)
				).ConfigureAwait(false);

				if (attachment.IsThumbnail && width < 1 && Handler.PrepareCache)
				{
					cacheKeys = [.. cacheKeys, .. ServiceExtensions.Widths.Select(variant => attachment.GetCacheKey(index, ImageFormat.Webp, variant, 0, asBig)).SelectMany(key => new[] { key, $"{key}:time" })];
					await ServiceExtensions.Widths.ForEachAsync(async variant =>
					{
						try
						{
							thumbnail = await original.GenerateAsync(ImageFormat.Webp, variant, 0, asBig, false, Global.CancellationToken).ConfigureAwait(false);
						}
						catch
						{
							thumbnail = await original.ConvertAsync(ImageFormat.Webp, Global.CancellationToken).ConfigureAwait(false);
						}
						cacheKey = attachment.GetCacheKey(index, ImageFormat.Webp, variant, 0, asBig);
						await Task.WhenAll
						(
							Global.Cache.SetAsFragmentsAsync(cacheKey, thumbnail, 0, Global.CancellationToken),
							Global.Cache.SetAsync($"{cacheKey}:time", lastModified, 0, Global.CancellationToken)
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

		public static async Task<List<string>> PrepareCacheAsync(this AttachmentInfo attachment, bool isWebP, string prefix = "file", byte[] data = null, long lastModified = 0)
		{
			if (data == null || data.Length < 1 || lastModified < 1)
			{
				var fileInfo = new FileInfo(attachment.GetFilePath());
				data = await fileInfo.ReadAsBinaryAsync(Global.CancellationToken).ConfigureAwait(false);
				lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
			}

			var cacheKey = attachment.GetCacheKey(prefix ?? (isWebP ? "webp" : "file"));
			var cacheKeys = new List<string> { cacheKey, $"{cacheKey}:time" };
			await Task.WhenAll
			(
				Global.Cache.SetAsFragmentsAsync(cacheKey, data, Global.CancellationToken),
				Global.Cache.SetAsync($"{cacheKey}:time", lastModified, Global.CancellationToken)
			).ConfigureAwait(false);

			if (!isWebP && cacheKey.IsStartsWith("file#"))
			{
				data = await data.ConvertAsync(ImageFormat.Webp, Global.CancellationToken).ConfigureAwait(false);
				cacheKey = attachment.GetCacheKey("webp");
				cacheKeys = [.. cacheKeys, cacheKey, $"{cacheKey}:time"];
				await Task.WhenAll
				(
					Global.Cache.SetAsFragmentsAsync(cacheKey, data, 0, Global.CancellationToken),
					Global.Cache.SetAsync($"{cacheKey}:time", lastModified, 0, Global.CancellationToken)
				).ConfigureAwait(false);
			}

			await Global.Cache.AddSetMembersAsync($"{attachment.ObjectID}:images", cacheKeys, Global.CancellationToken).ConfigureAwait(false);
			return cacheKeys;
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