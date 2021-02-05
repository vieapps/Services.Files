#region Related component
using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using ImageProcessorCore;
using net.vieapps.Components.Security;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class ThumbnailHandler : Services.FileHandler
	{
		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
			=> context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD")
				? this.ShowAsync(context, cancellationToken)
				: context.Request.Method.IsEquals("POST")
					? this.ReceiveAsync(context, cancellationToken)
					: Task.FromException(new MethodNotAllowedException(context.Request.Method));

		async Task ShowAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// check "If-Modified-Since" request to reduce traffict
			var requestUri = context.GetRequestUri();
			var useCache = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Thumbnails", "true")) && Global.Cache != null;
			var cacheKey = $"{requestUri}".ToLower().GenerateUUID();
			var lastModified = useCache ? await Global.Cache.GetAsync<string>($"{cacheKey}:time", cancellationToken).ConfigureAwait(false) : null;
			var eTag = $"thumbnail#{cacheKey}";
			var noneMatch = context.GetHeaderParameter("If-None-Match");
			var modifiedSince = context.GetHeaderParameter("If-Modified-Since") ?? context.GetHeaderParameter("If-Unmodified-Since");
			if (eTag.IsEquals(noneMatch) && modifiedSince != null && lastModified != null && modifiedSince.FromHttpDateTime() >= lastModified.FromHttpDateTime())
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, lastModified.FromHttpDateTime().ToUnixTimestamp(), "public", context.GetCorrelationID());
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"Response to request with status code 304 to reduce traffic ({requestUri})").ConfigureAwait(false);
				return;
			}

			// prepare
			var requestUrl = $"{requestUri}";
			var queryString = requestUri.ParseQuery();

			var isNoThumbnailImage = requestUri.PathAndQuery.IsStartsWith("/thumbnail") && (requestUri.PathAndQuery.IsEndsWith("/no-image.png") || requestUri.PathAndQuery.IsEndsWith("/no-image.jpg"));
			var pathSegments = requestUri.GetRequestPathSegments();

			var serviceName = pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "";
			var systemID = pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1].ToLower() : "";
			var identifier = pathSegments.Length > 5 && pathSegments[5].IsValidUUID() ? pathSegments[5].ToLower() : "";
			if (!isNoThumbnailImage && (string.IsNullOrWhiteSpace(identifier) || (string.IsNullOrWhiteSpace(serviceName) && string.IsNullOrWhiteSpace(systemID))))
				throw new InvalidRequestException();

			var handlerName = pathSegments[0];
			var format = handlerName.IsEndsWith("pngs") || (isNoThumbnailImage && Handler.NoThumbnailImageFilePath.IsEndsWith(".png")) || context.GetQueryParameter("asPng") != null || context.GetQueryParameter("transparent") != null
				? "PNG"
				: handlerName.IsEndsWith("webps")
					? "WEBP"
					: "JPG";
			var isBig = handlerName.IsStartsWith("thumbnaibig");
			var isThumbnail = isNoThumbnailImage || (pathSegments.Length > 2 && Int32.TryParse(pathSegments[2], out var isAttachment) && isAttachment == 0);
			if (!Int32.TryParse(pathSegments.Length > 3 ? pathSegments[3] : "", out var width))
				width = 0;
			if (!Int32.TryParse(pathSegments.Length > 4 ? pathSegments[4] : "", out var height))
				height = 0;
			var isCropped = requestUrl.IsContains("--crop") || queryString.ContainsKey("crop");
			var croppedPosition = requestUrl.IsContains("--crop-top") || "top".IsEquals(context.GetQueryParameter("cropPos")) ? "top" : requestUrl.IsContains("--crop-bottom") || "bottom".IsEquals(context.GetQueryParameter("cropPos")) ? "bottom" : "auto";
			//var isUseAdditionalWatermark = queryString.ContainsKey("nw") ? false : requestUrl.IsContains("--btwm");

			var attachment = new AttachmentInfo
			{
				ID = identifier,
				ServiceName = serviceName,
				SystemID = systemID,
				IsThumbnail = isThumbnail,
				Filename = isThumbnail
					? identifier + (pathSegments.Length > 6 && Int32.TryParse(pathSegments[6], out var index) && index > 0 ? $"-{index}" : "") + ".jpg"
					: pathSegments.Length > 6 ? pathSegments[6].UrlDecode() : "",
				IsTemporary = false,
				IsTracked = false
			};
			if ("webp".IsEquals(format) && pathSegments[2].Equals("1") && attachment.Filename.IsEndsWith(".webp"))
				attachment.Filename = attachment.Filename.Left(attachment.Filename.Length - 5);

				// check existed
			var isCached = false;
			var hasCached = useCache && await Global.Cache.ExistsAsync(cacheKey, cancellationToken).ConfigureAwait(false);

			FileInfo fileInfo = null;
			if (!hasCached)
			{
				fileInfo = new FileInfo(isNoThumbnailImage ? Handler.NoThumbnailImageFilePath : attachment.GetFilePath());
				if (!fileInfo.Exists)
				{
					context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
					return;
				}
			}

			// check permission
			async Task<bool> gotRightsAsync()
			{
				if (!isThumbnail)
				{
					attachment = await context.GetAsync(attachment.ID, cancellationToken).ConfigureAwait(false);
					return await context.CanDownloadAsync(attachment, cancellationToken).ConfigureAwait(false);
				}
				return true;
			}

			// generate
			async Task<byte[]> getAsync()
			{
				var thumbnail = useCache ? await Global.Cache.GetAsync<byte[]>(cacheKey, cancellationToken).ConfigureAwait(false) : null;
				if (thumbnail != null)
				{
					isCached = true;
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"Cached thumbnail was found ({requestUri})").ConfigureAwait(false);
				}
				return thumbnail;
			}

			async Task<byte[]> generateAsync()
			{
				byte[] thumbnail;

				// generate
				try
				{
					using (var stream = UtilityService.CreateMemoryStream())
					{
						using (var image = this.Generate(fileInfo.FullName, width, height, isBig, isCropped, croppedPosition))
						{
							// add watermark

							// get thumbnail image
							image.Save(stream, "png".IsEquals(format) ? ImageFormat.Png : ImageFormat.Jpeg);
						}
						thumbnail = stream.ToBytes();
					}
				}

				// read the whole file when got error
				catch (Exception ex)
				{
					await context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"Error occurred while generating thumbnail using Bitmap/Graphics => {ex.Message}", ex).ConfigureAwait(false);
					thumbnail = await UtilityService.ReadBinaryFileAsync(fileInfo, cancellationToken).ConfigureAwait(false);
				}

				// update cache
				if (useCache)
					await Task.WhenAll
					(
						Global.Cache.AddSetMemberAsync($"{(attachment.SystemID.IsValidUUID() ? attachment.SystemID : attachment.ServiceName)}:Thumbnails", $"{attachment.ObjectID}:Thumbnails", cancellationToken),
						Global.Cache.AddSetMemberAsync($"{attachment.ObjectID}:Thumbnails", cacheKey, cancellationToken),
						Global.Cache.SetAsFragmentsAsync(cacheKey, thumbnail, 1440, cancellationToken),
						Global.Cache.SetAsync($"{cacheKey}:time", fileInfo.LastWriteTime.ToHttpString(), 1440, cancellationToken),
						Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"Update a thumbnail image into cache successful [{requestUri} => {fileInfo.FullName}]") : Task.CompletedTask
					).ConfigureAwait(false);

				return thumbnail;
			}

			// prepare the thumbnail image
			var generateTask = hasCached ? getAsync() : generateAsync();
			if (!await gotRightsAsync().ConfigureAwait(false))
				throw new AccessDeniedException();

			// flush the thumbnail image to output stream, update counter & logs
			var bytes = await generateTask.ConfigureAwait(false);
			lastModified = lastModified ?? fileInfo.LastWriteTime.ToHttpString();
			var lastModifiedTime = lastModified.FromHttpDateTime().ToUnixTimestamp();

			if ("webp".IsEquals(format))
			{
				if (isCached)
					using (var stream = UtilityService.CreateMemoryStream(bytes))
					{
						await context.WriteAsync(stream, "image/webp", null, eTag, lastModifiedTime, "public", TimeSpan.FromDays(366), null, context.GetCorrelationID(), cancellationToken).ConfigureAwait(false);
					}
				else
					using (var imageFactory = new ImageFactory())
					using (var stream = UtilityService.CreateMemoryStream())
					{
						imageFactory.Load(bytes);
						imageFactory.Quality = 100;
						imageFactory.Save(stream);
						await context.WriteAsync(stream, "image/webp", null, eTag, lastModifiedTime, "public", TimeSpan.FromDays(366), null, context.GetCorrelationID(), cancellationToken).ConfigureAwait(false);
						if (useCache)
							await Task.WhenAll
							(
								Global.Cache.SetAsFragmentsAsync(cacheKey, stream.ToBytes(), cancellationToken),
								Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"Re-update a thumbnail image with WebP image format into cache successful ({requestUri})") : Task.CompletedTask
							).ConfigureAwait(false);
					}
			}
			else
				using (var stream = UtilityService.CreateMemoryStream(bytes))
				{
					await context.WriteAsync(stream, $"image/{("png".IsEquals(format) ? "png" : "jpeg")}", null, eTag, lastModifiedTime, "public", TimeSpan.FromDays(366), null, context.GetCorrelationID(), cancellationToken).ConfigureAwait(false);
				}

			await Task.WhenAll
			(
				context.UpdateAsync(attachment, "Direct", cancellationToken),
				Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"Successfully show a thumbnail image ({requestUri})") : Task.CompletedTask
			).ConfigureAwait(false);
		}

		Bitmap Generate(string filePath, int width, int height, bool asBig, bool isCropped, string cropPosition)
		{
			using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, TextFileReader.BufferSize))
			{
				using (var image = Image.FromStream(stream) as Bitmap)
				{
					// clone original image
					if ((width < 1 && height < 1) || (width.Equals(image.Width) && height.Equals(image.Height)))
						return image.Clone() as Bitmap;

					// calculate size depend on width
					if (height < 1)
					{
						height = image.Height * width / image.Width;
						if (height < 1)
							height = image.Height;
					}

					// calculate size depend on height
					else if (width < 1)
					{
						width = image.Width * height / image.Height;
						if (width < 1)
							width = image.Width;
					}

					// generate thumbnail
					return isCropped
						? this.Generate(image, width, height, asBig, cropPosition)
						: this.Generate(image, width, height, asBig);
				}
			}
		}

		Bitmap Generate(Bitmap image, int width, int height, bool asBig, string cropPosition)
		{
			using (var thumbnail = this.Generate(image, width, (image.Height * width) / image.Width, asBig))
			{
				// if height is less than thumbnail image's height, then return thumbnail image
				if (thumbnail.Height <= height)
					return thumbnail.Clone() as Bitmap;

				// crop image
				int top = cropPosition.IsEquals("auto")
					? (thumbnail.Height - height) / 2
					: cropPosition.IsEquals("bottom")
						? thumbnail.Height - height
						: 0;
				using (var cropped = new Bitmap(width, height))
				{
					using (var graphics = Graphics.FromImage(cropped))
					{
						graphics.DrawImage(thumbnail, new Rectangle(0, 0, width, height), new Rectangle(0, top, width, height), GraphicsUnit.Pixel);
					}
					return cropped.Clone() as Bitmap;
				}
			}
		}

		Bitmap Generate(Bitmap image, int width, int height, bool asBig)
		{
			// get and return normal thumbnail
			if (!asBig)
				return image.GetThumbnailImage(width, height, null, IntPtr.Zero) as Bitmap;

			// get and return big thumbnail (set resolution of original thumbnail)
			else
				using (var thumbnail = new Bitmap(width, height))
				{
					using (var graphics = Graphics.FromImage(thumbnail))
					{
						graphics.SmoothingMode = SmoothingMode.HighQuality;
						graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
						graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
						graphics.DrawImage(image, new Rectangle(0, 0, width, height));
					}
					return thumbnail.Clone() as Bitmap;
				}
		}

		internal static ArraySegment<byte> Generate(string message, int width = 300, int height = 100, bool exportAsPng = false)
		{
			using (var bitmap = new Bitmap(width, height, PixelFormat.Format16bppRgb555))
			{
				using (var graphics = Graphics.FromImage(bitmap))
				{
					graphics.SmoothingMode = SmoothingMode.AntiAlias;
					graphics.Clear(Color.White);
					graphics.DrawString(message, new Font("Arial", 16, FontStyle.Bold), SystemBrushes.WindowText, new PointF(10, 40));
					using (var stream = UtilityService.CreateMemoryStream())
					{
						bitmap.Save(stream, exportAsPng ? ImageFormat.Png : ImageFormat.Jpeg);
						return stream.ToArraySegment();
					}
				}
			}
		}

		async Task ReceiveAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var stopwatch = Stopwatch.StartNew();
			var serviceName = context.GetParameter("x-service-name");
			var objectName = context.GetParameter("x-object-name");
			var systemID = context.GetParameter("x-system-id");
			var entityInfo = context.GetParameter("x-entity");
			var objectID = context.GetParameter("x-object-id");
			var isTemporary = "true".IsEquals(context.GetParameter("x-temporary"));

			if (string.IsNullOrWhiteSpace(objectID))
				throw new InvalidRequestException("Invalid object identity");

			// check permissions
			var gotRights = isTemporary
				? await context.CanContributeAsync(serviceName, objectName, systemID, entityInfo, "", cancellationToken).ConfigureAwait(false)
				: await context.CanEditAsync(serviceName, objectName, systemID, entityInfo, objectID, cancellationToken).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// limit size - default is 512 KB
			if (!Int32.TryParse(UtilityService.GetAppSetting("Limits:Thumbnail"), out var limitSize))
				limitSize = 512;

			// read upload file
			var thumbnails = new List<byte[]>();
			var asBase64 = context.GetParameter("x-as-base64") != null;
			if (asBase64)
			{
				var base64Data = (await context.ReadTextAsync(cancellationToken).ConfigureAwait(false)).ToJson()["Data"];
				if (base64Data is JArray)
					(base64Data as JArray).Take(7).ForEach(data =>
					{
						var thumbnail = (data as JValue).Value.ToString().ToArray().Last().Base64ToBytes();
						if (thumbnail != null && thumbnail.Length <= limitSize * 1024)
						{
							thumbnails.Add(thumbnail);
						}
						else
							thumbnails.Add(null);
					});
				else
					thumbnails.Add((base64Data as JValue).Value.ToString().ToArray().Last().Base64ToBytes());
			}
			else
			{
				for (var index = 0; index < context.Request.Form.Files.Count && index < 7; index++)
					thumbnails.Add(null);
				await context.Request.Form.Files.Take(7).ForEachAsync(async (file, index, _) =>
				{
					if (file != null && file.ContentType.IsStartsWith("image/") && file.Length > 0 && file.Length <= limitSize * 1024)
						using (var stream = file.OpenReadStream())
						{
							var thumbnail = new byte[file.Length];
							await stream.ReadAsync(thumbnail, 0, (int)file.Length, cancellationToken).ConfigureAwait(false);
							thumbnails[index] = thumbnail;
						}
				}, cancellationToken, true, false).ConfigureAwait(false);
			}

			// save uploaded files & create meta info
			var attachments = new List<AttachmentInfo>();
			var useCache = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Thumbnails", "true")) && Global.Cache != null;
			try
			{
				// save uploaded files into disc
				var title = "";
				try
				{
					title = context.GetParameter("x-object-title")?.Url64Decode()?.GetANSIUri() ?? UtilityService.NewUUID;
				}
				catch
				{
					title = UtilityService.NewUUID;
				}
				await thumbnails.ForEachAsync(async (thumbnail, index, _) =>
				{
					if (thumbnail != null)
					{
						// prepare
						var attachment = new AttachmentInfo
						{
							ID = context.GetParameter("x-attachment-id") ?? UtilityService.NewUUID,
							ServiceName = serviceName,
							ObjectName = objectName,
							SystemID = systemID,
							EntityInfo = entityInfo,
							ObjectID = objectID,
							Size = thumbnail.Length,
							Filename = $"{objectID}{(index > 0 ? $"-{index}" : "")}.jpg",
							ContentType = "image/jpeg",
							IsShared = false,
							IsTracked = false,
							IsTemporary = isTemporary,
							Title = title,
							Description = "",
							IsThumbnail = true
						};

						// save file into temporary directory
						await UtilityService.WriteBinaryFileAsync(attachment.GetFilePath(true), thumbnail, false, cancellationToken).ConfigureAwait(false);

						// update attachment info
						attachments.Add(attachment);
					}
				}, cancellationToken, true, false).ConfigureAwait(false);

				// create meta info
				var response = new JArray();
				var cacheKeys = new List<string>();
				await attachments.ForEachAsync(async (attachment, token) =>
				{
					response.Add(await context.CreateAsync(attachment, token).ConfigureAwait(false));
					if (useCache)
					{
						var keys = await Global.Cache.GetSetMembersAsync($"{attachment.ObjectID}:Thumbnails", cancellationToken).ConfigureAwait(false);
						if (keys != null && keys.Count > 0)
							cacheKeys = cacheKeys.Concat(new[] { $"{attachment.ObjectID}:Thumbnails" }).Concat(keys).ToList();
					}
				}, cancellationToken, true, false).ConfigureAwait(false);

				// clear cache
				if (useCache && cacheKeys.Count > 0)
					await Global.Cache.RemoveAsync(cacheKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), cancellationToken).ConfigureAwait(false);

				// move files from temporary directory to official directory
				attachments.ForEach(attachment => attachment.PrepareDirectories().MoveFile(this.Logger, "Http.Uploads", true));

				// response
				await context.WriteAsync(response, cancellationToken).ConfigureAwait(false);
				stopwatch.Stop();
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Uploads", $"{thumbnails.Count(thumbnail => thumbnail != null)} thumbnail image(s) has been uploaded - Mode:  {(asBase64 ? "base64" : "file")} - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
			}
			catch (Exception)
			{
				attachments.ForEach(attachment => attachment.DeleteFile(true, this.Logger, "Http.Uploads"));
				throw;
			}
		}
	}

	#region Watermark
	public struct WatermarkInfo
	{
		public WatermarkInfo(string data, string position, Point offset)
		{
			this.Data = data;
			this.Position = position;
			this.Offset = offset;
		}

		/// <summary>
		/// Gets or sets data of the watermark
		/// </summary>
		public string Data { get; set; }

		/// <summary>
		/// Gets or sets position of the watermark (possible values: auto, top, bottom)
		/// </summary>
		public string Position { get; set; }

		/// <summary>
		/// Gets or sets offset of the watermark
		/// </summary>
		public Point Offset { get; set; }
	}
	#endregion

}