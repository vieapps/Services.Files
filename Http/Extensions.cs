#region Related components
using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;
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

		public static bool IsBypassCache(this HttpContext context)
			=> context.ContainsKey("x-force-cache") || context.ContainsKey("x-no-cache") || context.ContainsKey("x-bypass-cache");

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

		public static string ToString(this AttachmentInfo attachment, Action<JObject> onCompleted)
			=> attachment.ToJson(onCompleted).ToString(Newtonsoft.Json.Formatting.None);

		public static Task WriteAsync(this HttpContext context, JToken json, Newtonsoft.Json.Formatting format, Dictionary<string, string> headers, CancellationToken cancellationToken)
			=> context.WriteAsync(json.ToString(format), "application/json", new Dictionary<string, string>(headers ?? []) { ["Cache-Control"] = context.GetHttpCacheControl(true) }, cancellationToken);

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
		static ServiceExtensions()
		{
			SixLabors.ImageSharp.Configuration.Default.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
			{
				MaximumPoolSizeMegabytes = 128
			});
		}

		static IImageEncoder BmpEncoder { get; } = new SixLabors.ImageSharp.Formats.Bmp.BmpEncoder();

		static IImageEncoder PngEncoder { get; } = new SixLabors.ImageSharp.Formats.Png.PngEncoder();

		static IImageEncoder JpegEncoder { get; } = new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder();

		static IImageEncoder WebpEncoder { get; } = new SixLabors.ImageSharp.Formats.Webp.WebpEncoder();

		static IImageEncoder WebpAdvancedEncoder { get; } = new SixLabors.ImageSharp.Formats.Webp.WebpEncoder
		{
			FileFormat = SixLabors.ImageSharp.Formats.Webp.WebpFileFormatType.Lossy,
			Method = Enum.TryParse<SixLabors.ImageSharp.Formats.Webp.WebpEncodingMethod>(UtilityService.GetAppSetting("Files:WebP:Method", "Default"), out var method)
				? method
				: SixLabors.ImageSharp.Formats.Webp.WebpEncodingMethod.Default,
			Quality = 70,
			NearLosslessQuality = 60
		};

		static ResizeOptions WebpResizeOptions { get; } = new ResizeOptions
		{
			Mode = ResizeMode.Max,
			Size = new SixLabors.ImageSharp.Size(1920, 0),
			Sampler = KnownResamplers.Lanczos3
		};

		public static async Task<MemoryStream> ConvertAsync(this Stream imageStream, ImageFormat format, bool useAdvancedSettings, bool resizeBigWebpImage, CancellationToken cancellationToken)
		{
			imageStream.Seek(0, SeekOrigin.Begin);
			using var image = await SixLabors.ImageSharp.Image.LoadAsync(imageStream, cancellationToken).ConfigureAwait(false);
			if (useAdvancedSettings)
			{
				image.Mutate(context =>
				{
					context.AutoOrient();
					if (format == ImageFormat.Webp && image.Width > 1920 && resizeBigWebpImage)
						context.Resize(ServiceExtensions.WebpResizeOptions);
				});
				image.Metadata.ExifProfile = null;
				image.Metadata.IccProfile = null;
				image.Metadata.XmpProfile = null;
				image.Metadata.IptcProfile = null;
			}
			var outputStream = UtilityService.CreateMemoryStream();
			var encoder = format == ImageFormat.Webp
				? useAdvancedSettings ? ServiceExtensions.WebpAdvancedEncoder : ServiceExtensions.WebpEncoder
				: format == ImageFormat.Bmp ? ServiceExtensions.BmpEncoder : format == ImageFormat.Png ? ServiceExtensions.PngEncoder : ServiceExtensions.JpegEncoder;
			await image.SaveAsync(outputStream, encoder, cancellationToken).ConfigureAwait(false);
			outputStream.Seek(0, SeekOrigin.Begin);
			return outputStream;
		}

		public static bool ResizeBigWebpImage { get; } = "true".IsEquals(UtilityService.GetAppSetting("Files:WebP:ResizeBigWebpImage", "true"));

		public static Task<MemoryStream> ConvertAsync(this Stream imageStream, ImageFormat format, CancellationToken cancellationToken)
			=> imageStream.ConvertAsync(format, true, ServiceExtensions.ResizeBigWebpImage, cancellationToken);

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
			stream.Seek(0, SeekOrigin.Begin);
			return stream;
		}

		public static string Generator { get; } = UtilityService.GetAppSetting("Files:Generator", "System.Drawing");

		public static MemoryStream Generate(this Image image, int width, int height, bool asBig, string generator = null)
			=> "ImageSharp".IsEquals(generator ?? ServiceExtensions.Generator)
				? image.GenerateImageByImageSharp(width, height, asBig)
				: image.GenerateImageBySystemDrawing(width, height, asBig);

		public static MemoryStream GenerateImageBySystemDrawing(this Image image, int width, int height, bool asBig)
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

		public static MemoryStream GenerateImageByImageSharp(this Image image, int width, int height, bool asBig)
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
			using var imageStream = image.ToMemoryStream();
			using var img = SixLabors.ImageSharp.Image.Load(imageStream);
			var resizedImg = ProcessingExtensions.Clone(img, context => context.Resize(new ResizeOptions
			{
				Mode = ResizeMode.Max,
				Size = new SixLabors.ImageSharp.Size(width, height),
				Sampler = asBig ? KnownResamplers.Lanczos3 : KnownResamplers.Bicubic
			}));
			var outputStream = UtilityService.CreateMemoryStream();
			resizedImg.Save(outputStream, ServiceExtensions.JpegEncoder);
			outputStream.Seek(0, SeekOrigin.Begin);
			return outputStream;
		}

		public static async Task<byte[]> GenerateAsync(this byte[] bytes, ImageFormat format, int width, int height, bool asBig, bool isWebP, CancellationToken cancellationToken, string generator = null)
		{
			if (width > 0 || height > 0)
			{
				using var inputStream = bytes.ToMemoryStream();
				using var imageStream = await inputStream.ConvertAsync(ImageFormat.Bmp, cancellationToken).ConfigureAwait(false);
				using var image = Image.FromStream(imageStream);
				using var thumbnailStream = image.Generate(width, height, asBig, generator);
				using var outputStream = await thumbnailStream.ConvertAsync(format, cancellationToken).ConfigureAwait(false);
				return outputStream.ToBytes();
			}
			return isWebP && format == ImageFormat.Webp ? bytes : await bytes.ConvertAsync(format, cancellationToken).ConfigureAwait(false);
		}

		public static Task<byte[]> GenerateAsync(this Exception ex, int width, int height, CancellationToken cancellationToken, string generator = null)
			=> "ImageSharp".IsEquals(generator ?? ServiceExtensions.Generator)
				? ex.GenerateImageByImageSharpAsync(width, height, cancellationToken)
				: ex.GenerateImageBySystemDrawingAsync(width, height, cancellationToken);

		public static async Task<byte[]> GenerateImageBySystemDrawingAsync(this Exception ex, int width, int height, CancellationToken cancellationToken)
		{
			using var bitmap = new Bitmap(width, height, PixelFormat.Format16bppRgb555);
			using var graphics = Graphics.FromImage(bitmap);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.Clear(Color.White);
			graphics.DrawString(ex.Message, new Font("Arial", 16, FontStyle.Bold), SystemBrushes.WindowText, new PointF(10, 40));
			using var bitmapStream = bitmap.ToMemoryStream();
			using var outputStream = await bitmapStream.ConvertAsync(ImageFormat.Webp, cancellationToken).ConfigureAwait(false);
			outputStream.Seek(0, SeekOrigin.Begin);
			return outputStream.ToBytes();
		}

		public static async Task<byte[]> GenerateImageByImageSharpAsync(this Exception ex, int width, int height, CancellationToken cancellationToken)
		{
			using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height, SixLabors.ImageSharp.Color.White);
			var font = SixLabors.Fonts.SystemFonts.CreateFont("Arial", 16, SixLabors.Fonts.FontStyle.Bold);
			image.Mutate(context => context.DrawText(ex?.Message ?? "Unknown error", font, SixLabors.ImageSharp.Color.Black, new SixLabors.ImageSharp.PointF(20, 40)));
			using var outputStream = UtilityService.CreateMemoryStream();
			await image.SaveAsync(outputStream, ServiceExtensions.WebpAdvancedEncoder, cancellationToken).ConfigureAwait(false);
			outputStream.Seek(0, SeekOrigin.Begin);
			return outputStream.ToBytes();
		}
		#endregion

		#region Working with captchas
		public static MemoryStream Generate(this string code, bool isSmall = true, string generator = null)
			=> "ImageSharp".IsEquals(generator ?? ServiceExtensions.Generator)
				? code.GenerateCaptchaByImageSharp(isSmall)
				: code.GenerateCaptchaBySystemDrawing(isSmall);

		public static MemoryStream GenerateCaptchaBySystemDrawing(this string code, bool isSmall = true)
		{
			// prepare size
			var width = 220;
			var height = 48;
			if (isSmall)
			{
				width = 110;
				height = 24;
			}

			// create new graphic from the bitmap with random background color
			var backgroundColors = isSmall
				? new[] { Color.Orange, Color.Thistle, Color.LightSeaGreen, Color.Yellow, Color.YellowGreen, Color.NavajoWhite, Color.White }
				: [Color.Orange, Color.Thistle, Color.LightSeaGreen, Color.Violet, Color.Yellow, Color.YellowGreen, Color.NavajoWhite, Color.LightGray, Color.Tomato, Color.LightGreen, Color.White];

			using var securityBitmap = ServiceExtensions.CreateCaptchaBackroundBySystemDrawing(width, height, [backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)], backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)], backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)], backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)]]);
			using var securityGraph = Graphics.FromImage(securityBitmap);
			securityGraph.SmoothingMode = SmoothingMode.AntiAlias;

			// add noise texts (for big image)
			if (!isSmall)
			{
				// texts for the image
				var noiseTexts = new List<string> { "Winners never quit", "Quitters never win", "Don't be evil", "Keep moving", "Connecting People", "Information at your fingertips", "No sacrifice no victory", "No pain no gain", "Where do you want to go today?", "Make business easier", "Simplify business process" };
				var noiseText = noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)];
				noiseText += " " + noiseText + " " + noiseText + " " + noiseText;

				// write noise texts
				securityGraph.DrawString(noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)] + " - " + noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)], new Font("Verdana", 10, FontStyle.Underline), new System.Drawing.SolidBrush(Color.White), new PointF(0, 3));
				securityGraph.DrawString(noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)] + " - " + noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)], new Font("Verdana", 12, FontStyle.Bold), new System.Drawing.SolidBrush(Color.White), new PointF(5, 7));
				securityGraph.DrawString(noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)] + " - " + noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)], new Font("Arial", 11, FontStyle.Italic), new System.Drawing.SolidBrush(Color.White), new PointF(-5, 20));
				securityGraph.DrawString(noiseText, new System.Drawing.Font("Arial", 12, System.Drawing.FontStyle.Bold), new System.Drawing.SolidBrush(Color.White), new PointF(20, 28));
			}

			// add noise lines (for small image)
			else
			{
				// randrom index to make noise lines
				var randomIndex = UtilityService.GetRandomNumber(0, backgroundColors.Length);

				// first two lines
				var noisePen = new System.Drawing.Pen(new System.Drawing.SolidBrush(Color.Gray), 2);
				securityGraph.DrawLine(noisePen, new Point(width, randomIndex), new Point(randomIndex, height / 2 - randomIndex));
				securityGraph.DrawLine(noisePen, new Point(width / 3 - randomIndex, randomIndex), new Point(width / 2 + randomIndex, height - randomIndex));

				// second two lines
				noisePen = new System.Drawing.Pen(new System.Drawing.SolidBrush(Color.Yellow), 1);
				securityGraph.DrawLine(noisePen, new Point(((width / 4) * 3) - randomIndex, randomIndex), new Point(width / 3 + randomIndex, height - randomIndex));
				if (randomIndex % 2 == 1)
					securityGraph.DrawLine(noisePen, new Point(width - randomIndex * 2, randomIndex), new Point(randomIndex, height - randomIndex));
				else
					securityGraph.DrawLine(noisePen, new Point(randomIndex, randomIndex), new Point(width - randomIndex * 2, height - randomIndex));

				// third two lines
				randomIndex = UtilityService.GetRandomNumber(0, backgroundColors.Length);
				noisePen = new System.Drawing.Pen(new System.Drawing.SolidBrush(Color.Magenta), 1);
				securityGraph.DrawLine(noisePen, new Point(((width / 6) * 3) - randomIndex, randomIndex), new Point(width / 5 + randomIndex, height - randomIndex + 3));
				if (randomIndex % 2 == 1)
					securityGraph.DrawLine(noisePen, new Point(width - randomIndex * 2, randomIndex - 1), new Point(randomIndex, height - randomIndex - 3));
				else
					securityGraph.DrawLine(noisePen, new Point(randomIndex, randomIndex + 1), new Point(width - randomIndex * 2, height - randomIndex + 4));

				// fourth two lines
				randomIndex = UtilityService.GetRandomNumber(0, backgroundColors.Length);
				noisePen = new System.Drawing.Pen(new System.Drawing.SolidBrush(backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)]), 1);
				securityGraph.DrawLine(noisePen, new Point(((width / 10) * 3) - randomIndex, randomIndex), new Point(width / 6 + randomIndex, height - randomIndex + 3));
				if (randomIndex % 2 == 1)
					securityGraph.DrawLine(noisePen, new Point(width - randomIndex * 3, randomIndex - 2), new Point(randomIndex, height - randomIndex - 2));
				else
					securityGraph.DrawLine(noisePen, new Point(randomIndex, randomIndex + 2), new Point(width - randomIndex * 3, height - randomIndex + 2));
			}

			// put the security code into the image with random font and brush
			var fonts = new[] { "Verdana", "Arial", "Times New Roman", "Courier", "Courier New" };
			var brushs = new[]
			{
				new System.Drawing.SolidBrush(Color.Black),
				new System.Drawing.SolidBrush(Color.Blue),
				new System.Drawing.SolidBrush(Color.DarkBlue),
				new System.Drawing.SolidBrush(Color.DarkGreen),
				new System.Drawing.SolidBrush(Color.Magenta),
				new System.Drawing.SolidBrush(Color.Red),
				new System.Drawing.SolidBrush(Color.DarkRed),
				new System.Drawing.SolidBrush(Color.Black),
				new System.Drawing.SolidBrush(Color.Firebrick),
				new System.Drawing.SolidBrush(Color.DarkGreen),
				new System.Drawing.SolidBrush(Color.Green),
				new System.Drawing.SolidBrush(Color.DarkViolet)
			};

			if (isSmall)
			{
				var step = 0;
				for (var index = 0; index < code.Length; index++)
				{
					float x = (index * 7) + step + UtilityService.GetRandomNumber(-1, 9);
					float y = UtilityService.GetRandomNumber(-2, 0);

					var writtenCode = code.Substring(index, 1);
					if (writtenCode.Equals("I") || (UtilityService.GetRandomNumber() % 2 == 1 && !writtenCode.Equals("L")))
						writtenCode = writtenCode.ToLower();

					var addedX = UtilityService.GetRandomNumber(-3, 5);
					securityGraph.DrawString(writtenCode, new Font(fonts[UtilityService.GetRandomNumber(0, fonts.Length)], UtilityService.GetRandomNumber(13, 19), FontStyle.Bold), brushs[UtilityService.GetRandomNumber(0, brushs.Length)], new PointF(x + addedX, y));
					step += UtilityService.GetRandomNumber(13, 23);
				}
			}
			else
			{
				// write code
				var step = 0;
				for (int index = 0; index < code.Length; index++)
				{
					var font = fonts[UtilityService.GetRandomNumber(0, fonts.Length)];
					float x = 2 + step, y = 10;
					step += 9;
					float fontSize = 15;
					if (index > 1 && index < 4)
					{
						fontSize = 25;
						x -= 10;
						y -= 5;
					}
					else if (index > 3 && index < 6)
					{
						y -= UtilityService.GetRandomNumber(3, 5);
						fontSize += index;
						step += index / 5;
						if (index == 4)
						{
							if (UtilityService.GetRandomNumber() % 2 == 1)
								y += UtilityService.GetRandomNumber(8, 12);
							else if (UtilityService.GetRandomNumber() % 2 == 2)
							{
								y -= UtilityService.GetRandomNumber(2, 6);
								fontSize += UtilityService.GetRandomNumber(1, 4);
							}
						}
					}
					else if (index > 5)
					{
						x += UtilityService.GetRandomNumber(0, 4);
						y -= UtilityService.GetRandomNumber(0, 4);
						fontSize += index - 7;
						step += index / 3 + 1;
						if (index == 10)
						{
							if (UtilityService.GetRandomNumber() % 2 == 1)
								y += UtilityService.GetRandomNumber(7, 14);
							else if (UtilityService.GetRandomNumber() % 2 == 2)
							{
								y -= UtilityService.GetRandomNumber(1, 3);
								fontSize += UtilityService.GetRandomNumber(2, 5);
							}
						}
					}
					var writtenCode = code.Substring(index, 1);
					if (writtenCode.Equals("I") || (UtilityService.GetRandomNumber() % 2 == 1 && !writtenCode.Equals("L")))
						writtenCode = writtenCode.ToLower();
					securityGraph.DrawString(writtenCode, new Font(font, fontSize, FontStyle.Bold), brushs[UtilityService.GetRandomNumber(0, brushs.Length)], new PointF(x + 2, y + 2));
					securityGraph.DrawString(writtenCode, new Font(font, fontSize, FontStyle.Bold), brushs[UtilityService.GetRandomNumber(0, brushs.Length)], new PointF(x, y));
				}

				// fill it randomly with pixels
				int maxX = width, maxY = height, startX = 0, startY = 0;
				int random = UtilityService.GetRandomNumber(1, 100);
				if (random > 80)
				{
					maxX -= maxX / 3;
					maxY = maxY / 2;
				}
				else if (random > 60)
				{
					startX = maxX / 3;
					startY = maxY / 2;
				}
				else if (random > 30)
				{
					startX = maxX / 7;
					startY = maxY / 4;
					maxX -= maxX / 5;
					maxY -= maxY / 8;
				}

				for (int iX = startX; iX < maxX; iX++)
					for (int iY = startY; iY < maxY; iY++)
						if ((iX % 3 == 1) && (iY % 4 == 1))
							securityBitmap.SetPixel(iX, iY, Color.DarkGray);
			}

			// add random noise into image (use SIN)
			var divideTo = 64.0d + UtilityService.GetRandomNumber(1, 10);
			var distortion = UtilityService.GetRandomNumber(5, 11);
			if (isSmall)
				distortion = UtilityService.GetRandomNumber(1, 5);

			using var noisedBitmap = new Bitmap(width, height, PixelFormat.Format16bppRgb555);
			for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++)
				{
					int newX = (int)(x + (distortion * Math.Sin(Math.PI * y / divideTo)));
					if (newX < 0 || newX >= width)
						newX = 0;

					int newY = (int)(y + (distortion * Math.Cos(Math.PI * x / divideTo)));
					if (newY < 0 || newY >= height)
						newY = 0;

					noisedBitmap.SetPixel(x, y, securityBitmap.GetPixel(newX, newY));
				}

			// export the image
			return noisedBitmap.ToMemoryStream();
		}

		static Bitmap CreateCaptchaBackroundBySystemDrawing(int width, int height, Color[] backgroundColors)
		{
			// create element bitmaps
			int bmpWidth = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(5, 10), UtilityService.GetRandomNumber(20, width / 2));
			int bmpHeight = UtilityService.GetRandomNumber(height / 4, height / 2);
			if (height > 20)
				bmpHeight = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(1, 10), UtilityService.GetRandomNumber(12, height));
			using var bitmap1 = new Bitmap(bmpWidth, bmpHeight, PixelFormat.Format16bppRgb555);
			using (var graph = Graphics.FromImage(bitmap1))
			{
				graph.SmoothingMode = SmoothingMode.AntiAlias;
				graph.Clear(backgroundColors[0]);
			}

			bmpWidth = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(15, width / 3), UtilityService.GetRandomNumber(width / 3, width / 2));
			bmpHeight = UtilityService.GetRandomNumber(5, height / 3);
			if (height > 20)
				bmpHeight = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(5, height / 4), UtilityService.GetRandomNumber(height / 4, height / 2));
			using var bitmap2 = new Bitmap(bmpWidth, bmpHeight, PixelFormat.Format16bppRgb555);
			using (var graph = Graphics.FromImage(bitmap2))
			{
				graph.SmoothingMode = SmoothingMode.AntiAlias;
				graph.Clear(backgroundColors[1]);
			}

			bmpWidth = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(width / 4, width / 2), UtilityService.GetRandomNumber(width / 2, width));
			bmpHeight = UtilityService.GetRandomNumber(height / 2, height);
			if (height > 20)
				bmpHeight = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(height / 5, height / 2), UtilityService.GetRandomNumber(height / 2, height));
			using var bitmap3 = new Bitmap(bmpWidth, bmpHeight, PixelFormat.Format16bppRgb555);
			using (var graph = Graphics.FromImage(bitmap3))
			{
				graph.SmoothingMode = SmoothingMode.AntiAlias;
				graph.Clear(backgroundColors[2]);
			}

			using var backroundBitmap = new Bitmap(width, height, PixelFormat.Format16bppRgb555);
			using (var graph = Graphics.FromImage(backroundBitmap))
			{
				graph.SmoothingMode = SmoothingMode.AntiAlias;
				graph.Clear(backgroundColors[3]);
				graph.DrawImage(bitmap1, UtilityService.GetRandomNumber(0, width / 2), UtilityService.GetRandomNumber(0, height / 2));
				graph.DrawImage(bitmap2, UtilityService.GetRandomNumber(width / 5, width / 2), UtilityService.GetRandomNumber(height / 5, height / 2));
				graph.DrawImage(bitmap3, UtilityService.GetRandomNumber(width / 4, width / 3), UtilityService.GetRandomNumber(0, height / 3));
			}
			return backroundBitmap.Clone() as Bitmap;
		}

		public static MemoryStream GenerateCaptchaByImageSharp(this string code, bool isSmall = true)
		{
			int width = isSmall ? 110 : 220;
			int height = isSmall ? 24 : 48;

			var backgroundColors = isSmall
				? new[] { SixLabors.ImageSharp.Color.Orange, SixLabors.ImageSharp.Color.Thistle, SixLabors.ImageSharp.Color.LightSeaGreen, SixLabors.ImageSharp.Color.Yellow, SixLabors.ImageSharp.Color.YellowGreen, SixLabors.ImageSharp.Color.NavajoWhite, SixLabors.ImageSharp.Color.White }
				: [SixLabors.ImageSharp.Color.Orange, SixLabors.ImageSharp.Color.Thistle, SixLabors.ImageSharp.Color.LightSeaGreen, SixLabors.ImageSharp.Color.Violet, SixLabors.ImageSharp.Color.Yellow, SixLabors.ImageSharp.Color.YellowGreen, SixLabors.ImageSharp.Color.NavajoWhite, SixLabors.ImageSharp.Color.LightGray, SixLabors.ImageSharp.Color.Tomato, SixLabors.ImageSharp.Color.LightGreen, SixLabors.ImageSharp.Color.White];

			var fontColors = new[]
			{
				SixLabors.ImageSharp.Color.Black,
				SixLabors.ImageSharp.Color.Blue,
				SixLabors.ImageSharp.Color.DarkBlue,
				SixLabors.ImageSharp.Color.DarkGreen,
				SixLabors.ImageSharp.Color.Magenta,
				SixLabors.ImageSharp.Color.Red,
				SixLabors.ImageSharp.Color.DarkRed,
				SixLabors.ImageSharp.Color.Black,
				SixLabors.ImageSharp.Color.Firebrick,
				SixLabors.ImageSharp.Color.DarkGreen,
				SixLabors.ImageSharp.Color.DarkViolet
			};

			using var securityBitmap = ServiceExtensions.CreateCaptchaBackroundByImageSharp(width, height,
			[
				backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)],
				backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)],
				backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)],
				backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)]
			]);

			// noise lines (small)
			if (isSmall)
				securityBitmap.Mutate(context =>
				{
					for (int i = 0; i < 6; i++)
						context.DrawLine(SixLabors.ImageSharp.Color.Gray, UtilityService.GetRandomNumber(1, 2), new SixLabors.ImageSharp.PointF[] { new(UtilityService.GetRandomNumber(0, width), UtilityService.GetRandomNumber(0, height)), new(UtilityService.GetRandomNumber(0, width), UtilityService.GetRandomNumber(0, height)) });
				});

			// captcha characters
			var fontNames = new[] { "Verdana", "Arial", "Times New Roman", "Courier", "Courier New" };

			int step = 0;
			for (int index = 0; index < code.Length; index++)
			{
				var fontName = fontNames[UtilityService.GetRandomNumber(0, fontNames.Length)];
				SixLabors.Fonts.Font font;
				try
				{
					font = SixLabors.Fonts.SystemFonts.CreateFont(fontName, isSmall ? 16 : 20, SixLabors.Fonts.FontStyle.Bold);
				}
				catch
				{
					font = SixLabors.Fonts.SystemFonts.CreateFont(SixLabors.Fonts.SystemFonts.Collection.Families.First().Name, isSmall ? 16 : 20, SixLabors.Fonts.FontStyle.Bold);
				}

				float x = (index * 7) + step + UtilityService.GetRandomNumber(-1, 9);
				float y = UtilityService.GetRandomNumber(-2, 2);
				var color = fontColors[UtilityService.GetRandomNumber(0, fontColors.Length)];

				var writtenCode = code.Substring(index, 1);
				if (writtenCode.Equals("I") || (UtilityService.GetRandomNumber() % 2 == 1 && !writtenCode.Equals("L")))
					writtenCode = writtenCode.ToLower();

				securityBitmap.Mutate(context => context.DrawText(writtenCode, font, color, new SixLabors.ImageSharp.PointF(x, y)));
				step += UtilityService.GetRandomNumber(13, 23);
			}

			// pixel noise
			securityBitmap.ProcessPixelRows(accessor =>
			{
				for (int y = 0; y < height; y++)
				{
					var row = accessor.GetRowSpan(y);
					for (int x = 0; x < width; x++)
					{
						if ((x % 3 == 1) && (y % 4 == 1))
							row[x] = SixLabors.ImageSharp.Color.DarkGray;
					}
				}
			});

			// distortion (sin/cos)
			var divideTo = 64.0 + UtilityService.GetRandomNumber(1, 10);
			var distortion = isSmall
				? UtilityService.GetRandomNumber(1, 5)
				: UtilityService.GetRandomNumber(5, 11);

			var distorted = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
			for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++)
				{
					int newX = (int)(x + distortion * Math.Sin(Math.PI * y / divideTo));
					int newY = (int)(y + distortion * Math.Cos(Math.PI * x / divideTo));

					if (newX < 0 || newX >= width) newX = 0;
					if (newY < 0 || newY >= height) newY = 0;

					distorted[x, y] = securityBitmap[newX, newY];
				}

			var stream = UtilityService.CreateMemoryStream();
			distorted.Save(stream, ServiceExtensions.JpegEncoder);
			stream.Seek(0, SeekOrigin.Begin);
			return stream;
		}

		static SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> CreateCaptchaBackroundByImageSharp(int width, int height, SixLabors.ImageSharp.Color[] backgroundColors)
		{
			int bmpWidth = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(5, 10), UtilityService.GetRandomNumber(20, width / 2));
			int bmpHeight = UtilityService.GetRandomNumber(height / 4, height / 2);
			if (height > 20)
				bmpHeight = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(1, 10), UtilityService.GetRandomNumber(12, height));
			var bitmap1 = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(bmpWidth, bmpHeight, backgroundColors[0]);

			bmpWidth = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(15, width / 3), UtilityService.GetRandomNumber(width / 3, width / 2));
			bmpHeight = UtilityService.GetRandomNumber(5, height / 3);
			if (height > 20)
				bmpHeight = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(5, height / 4), UtilityService.GetRandomNumber(height / 4, height / 2));
			var bitmap2 = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(bmpWidth, bmpHeight, backgroundColors[1]);

			bmpWidth = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(width / 4, width / 2), UtilityService.GetRandomNumber(width / 2, width));
			bmpHeight = UtilityService.GetRandomNumber(height / 2, height);
			if (height > 20)
				bmpHeight = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(height / 5, height / 2), UtilityService.GetRandomNumber(height / 2, height));
			var bitmap3 = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(bmpWidth, bmpHeight, backgroundColors[2]);

			var background = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height, backgroundColors[3]);
			background.Mutate(context =>
			{
				context.DrawImage(bitmap1, new SixLabors.ImageSharp.Point(UtilityService.GetRandomNumber(0, width / 2), UtilityService.GetRandomNumber(0, height / 2)), 1f);
				context.DrawImage(bitmap2, new SixLabors.ImageSharp.Point(UtilityService.GetRandomNumber(width / 5, width / 2), UtilityService.GetRandomNumber(height / 5, height / 2)), 1f);
				context.DrawImage(bitmap3, new SixLabors.ImageSharp.Point(UtilityService.GetRandomNumber(width / 4, width / 3), UtilityService.GetRandomNumber(0, height / 3)), 1f);
			});

			bitmap1.Dispose();
			bitmap2.Dispose();
			bitmap3.Dispose();

			return background;
		}
		#endregion

		#region Working with cache
		public static string GetCacheKey(this (string id, int index, ImageFormat format, int width, int height, bool asBig) info, string prefix = "thumbnail")
			=> $"{(string.IsNullOrWhiteSpace(prefix) ? "thumbnail" : prefix)}#" + $"{info.id}@{info.index}:{info.format}:{info.width}:{info.height}:{info.asBig}".ToLower().GenerateUUID();

		public static string GetCacheKey(this AttachmentInfo attachment, string prefix)
			=> $"{(string.IsNullOrWhiteSpace(prefix) ? "file" : prefix)}#{attachment.ID}".ToLower();

		public static string GetCacheKey(this AttachmentInfo attachment, int index = -1, ImageFormat format = null, int width = 0, int height = 0, bool asBig = true)
			=> (attachment.IsThumbnail ? attachment.ObjectID : attachment.ID, attachment.IsThumbnail ? index < 0 ? attachment.Filename.Length == 36 ? 0 : attachment.Filename.Right(5).Replace(".jpg", "").As<int>() : index : 0, format ?? ImageFormat.Jpeg, width, height, asBig).GetCacheKey();

		public static bool IsCacheableImage(this AttachmentInfo attachment, bool allowTemporary = false)
			=> (allowTemporary || !attachment.IsTemporary) && !string.IsNullOrWhiteSpace(attachment.ContentType) && attachment.ContentType.IsStartsWith("image/") && !attachment.ContentType.IsContains("icon") && !attachment.ContentType.IsContains("svg");

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
				Global.Cache.SetAsFragmentsAsync(cacheKey, thumbnail, Global.CancellationToken),
				Global.Cache.SetAsync($"{cacheKey}:time", lastModified, Global.CancellationToken)
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
					Global.Cache.SetAsFragmentsAsync(cacheKey, thumbnail, Global.CancellationToken),
					Global.Cache.SetAsync($"{cacheKey}:time", lastModified, Global.CancellationToken)
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
				using var webpStream = await fileStream.ConvertAsync(ImageFormat.Webp, !attachment.Filename.IsEndsWith(".png"), !attachment.Filename.IsEndsWith(".png") && ServiceExtensions.ResizeBigWebpImage, Global.CancellationToken).ConfigureAwait(false);
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