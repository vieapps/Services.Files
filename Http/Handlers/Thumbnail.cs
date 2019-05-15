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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Security;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class ThumbnailHandler : Services.FileHandler
	{
		public override ILogger Logger { get; } = Components.Utility.Logger.CreateLogger<ThumbnailHandler>();

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted))
				try
				{
					if (context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD"))
						await this.ShowAsync(context, cts.Token).ConfigureAwait(false);
					else if (context.Request.Method.IsEquals("POST"))
						await this.ReceiveAsync(context, cts.Token).ConfigureAwait(false);
					else
						throw new MethodNotAllowedException(context.Request.Method);
				}
				catch (OperationCanceledException) { }
				catch (Exception ex)
				{
					await context.WriteLogsAsync(this.Logger, $"Http.{(context.Request.Method.IsEquals("POST") ? "Uploads" : "Thumbnails")}", $"Error occurred while processing with a thumbnail image ({context.Request.Method} {context.GetReferUri()})", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
					var queryString = context.GetRequestUri().ParseQuery();
					if (context.Request.Method.IsEquals("POST"))
						context.WriteHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
					else
					{
						if (ex is AccessDeniedException && !context.User.Identity.IsAuthenticated && !queryString.ContainsKey("x-app-token") && !queryString.ContainsKey("x-passport-token"))
							context.Response.Redirect(context.GetPassportSessionAuthenticatorUrl());
						else
							context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
					}
				}
		}

		async Task ShowAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// check "If-Modified-Since" request to reduce traffict
			var requestUri = context.GetRequestUri();
			var eTag = "Thumbnail#" + $"{requestUri}".ToLower().GenerateUUID();
			if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")) && context.GetHeaderParameter("If-Modified-Since") != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", context.GetCorrelationID());
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"Response to request with status code 304 to reduce traffic ({requestUri})").ConfigureAwait(false);
				return;
			}

			// prepare
			var requestUrl = $"{requestUri}";
			var queryString = requestUri.ParseQuery();

			var pathSegments = requestUri.GetRequestPathSegments();

			var serviceName = pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "";
			var systemID = pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1] : "";
			var identifier = pathSegments.Length > 5 && pathSegments[5].IsValidUUID() ? pathSegments[5] : "";
			if (string.IsNullOrWhiteSpace(identifier) || (string.IsNullOrWhiteSpace(serviceName) && string.IsNullOrWhiteSpace(systemID)))
				throw new InvalidRequestException();

			var handlerName = pathSegments[0];
			var isPng = handlerName.IsEndsWith("pngs");
			var isBig = handlerName.IsEndsWith("bigs") || handlerName.IsEndsWith("bigpngs");
			var isThumbnail = pathSegments.Length > 2 && Int32.TryParse(pathSegments[2], out var isAttachment) && isAttachment == 0;
			if (!Int32.TryParse(pathSegments.Length > 3 ? pathSegments[3] : "", out int width))
				width = 0;
			if (!Int32.TryParse(pathSegments.Length > 4 ? pathSegments[4] : "", out int height))
				height = 0;
			var isCropped = requestUrl.IsContains("--crop") || queryString.ContainsKey("crop");
			var croppedPosition = requestUrl.IsContains("--crop-top") || "top".IsEquals(context.GetQueryParameter("cropPos")) ? "top" : requestUrl.IsContains("--crop-bottom") || "bottom".IsEquals(context.GetQueryParameter("cropPos")) ? "bottom" : "auto";
			var isUseAdditionalWatermark = queryString.ContainsKey("nw") ? false : requestUrl.IsContains("--btwm");

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

			// check exist
			var fileInfo = new FileInfo(attachment.GetFilePath());
			if (!fileInfo.Exists)
			{
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
				return;
			}

			// check permission
			async Task<bool> gotRightsAsync()
			{
				if (!isThumbnail)
				{
					attachment = await context.GetAsync(attachment.ID, cancellationToken).ConfigureAwait(false);
					return await context.CanDownloadAsync(attachment).ConfigureAwait(false);
				}
				return true;
			}

			// generate
			async Task<byte[]> generateAsync()
			{
				var masterKey = "Thumbnnail#" + fileInfo.FullName.ToLower().GenerateUUID();
				var detailKey = $"{masterKey}x{width}x{height}x{isPng}x{isBig}x{isCropped}x{croppedPosition}".ToLower();
				var cacheStorage = Global.ServiceProvider.GetService<ICache>();
				var useCache = cacheStorage != null && "true".IsEquals(UtilityService.GetAppSetting("Files:CacheThumbnails", "true"));
				var thumbnail = useCache ? await cacheStorage.GetAsync<byte[]>(detailKey, cancellationToken).ConfigureAwait(false) : null;
				if (thumbnail != null)
					return thumbnail;

				using (var stream = UtilityService.CreateMemoryStream())
				{
					using (var image = this.Generate(fileInfo.FullName, width, height, isBig, isCropped, croppedPosition))
					{
						// add watermark

						// get thumbnail image
						image.Save(stream, isPng ? ImageFormat.Png : ImageFormat.Jpeg);
					}
					thumbnail = stream.ToBytes();
				}

				if (useCache)
				{
					var keys = await cacheStorage.GetAsync<HashSet<string>>(masterKey, cancellationToken).ConfigureAwait(false) ?? new HashSet<string>();
					keys.Append(detailKey);
					await Task.WhenAll(
						cacheStorage.SetAsync(masterKey, keys, 0, cancellationToken),
						cacheStorage.SetAsFragmentsAsync(detailKey, thumbnail, 0, cancellationToken)
					).ConfigureAwait(false);
				}

				return thumbnail;
			}

			// generate the thumbnail image
			var generateTask = generateAsync();
			if (!await gotRightsAsync().ConfigureAwait(false))
				throw new AccessDeniedException();

			// flush the thumbnail image to output stream, update counter & logs
			context.SetResponseHeaders((int)HttpStatusCode.OK, $"image/{(isPng ? "png" : "jpeg")}", eTag, fileInfo.LastWriteTime.AddMinutes(-13).ToUnixTimestamp(), "public", TimeSpan.FromDays(7), context.GetCorrelationID());
			await context.WriteAsync(await generateTask.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
			await Task.WhenAll(
				context.UpdateAsync(attachment, "Direct", cancellationToken),
				Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"Successfully show a thumbnail image [{requestUri} => {fileInfo.FullName}]") : Task.CompletedTask
			).ConfigureAwait(false);
		}

		Bitmap Generate(string filePath, int width, int height, bool asBig, bool isCropped, string cropPosition)
		{
			using (var image = Image.FromFile(filePath) as Bitmap)
			{
				// clone original image
				if ((width < 1 && height < 1) || (width.Equals(image.Width) && height.Equals(image.Height)))
					return image.Clone() as Bitmap;

				// calculate size depend on width
				if (height < 1)
				{
					height = (int)((image.Height * width) / image.Width);
					if (height < 1)
						height = image.Height;
				}

				// calculate size depend on height
				else if (width < 1)
				{
					width = (int)((image.Width * height) / image.Height);
					if (width < 1)
						width = image.Width;
				}

				// generate thumbnail
				return isCropped
					? this.Generate(image, width, height, asBig, cropPosition)
					: this.Generate(image, width, height, asBig);
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
			var definitionID = context.GetParameter("x-definition-id");
			var objectID = context.GetParameter("x-object-id");
			var isTemporary = "true".IsEquals(context.GetParameter("x-temporary"));

			if (string.IsNullOrWhiteSpace(objectID))
				throw new InvalidRequestException("Invalid object identity");

			// check permissions
			var gotRights = isTemporary
				? !string.IsNullOrWhiteSpace(systemID) && !string.IsNullOrWhiteSpace(definitionID)
					? await context.CanContributeAsync(serviceName, systemID, definitionID, "").ConfigureAwait(false)
					: await context.CanContributeAsync(serviceName, objectName, "").ConfigureAwait(false)
				: !string.IsNullOrWhiteSpace(systemID) && !string.IsNullOrWhiteSpace(definitionID)
					? await context.CanEditAsync(serviceName, systemID, definitionID, objectID).ConfigureAwait(false)
					: await context.CanEditAsync(serviceName, objectName, objectID).ConfigureAwait(false);

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
				await context.Request.Form.Files.Take(7).ForEachAsync(async (file, index, token) =>
				{
					if (file != null && file.ContentType.IsStartsWith("image/") && file.Length > 0 && file.Length <= limitSize * 1024)
						using (var stream = file.OpenReadStream())
						{
							var thumbnail = new byte[file.Length];
							await stream.ReadAsync(thumbnail, 0, (int)file.Length, token).ConfigureAwait(false);
							thumbnails[index] = thumbnail;
						}
				}, cancellationToken, true, false).ConfigureAwait(false);
			}

			// save uploaded files & create meta info
			var attachments = new List<AttachmentInfo>();
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
				await thumbnails.ForEachAsync(async (thumbnail, index, token) =>
				{
					if (thumbnail != null)
					{
						// prepare
						var attachment = new AttachmentInfo
						{
							ID = UtilityService.NewUUID,
							ServiceName = serviceName,
							ObjectName = objectName,
							SystemID = systemID,
							DefinitionID = definitionID,
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
						await UtilityService.WriteBinaryFileAsync(attachment.GetFilePath(true), thumbnail, token).ConfigureAwait(false);

						// update attachment info
						attachments.Add(attachment);
					}
				}, cancellationToken, true, false).ConfigureAwait(false);

				// create meta info
				var response = new JArray();
				await attachments.ForEachAsync(async (attachment, token) => response.Add(await context.CreateAsync(attachment, token).ConfigureAwait(false)), cancellationToken, true, false).ConfigureAwait(false);

				// move files from temporary directory to official directory
				attachments.ForEach(attachment => attachment.PrepareDirectories().MoveFile(this.Logger, "Http.Uploads"));

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