#region Related component
using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

		ICache Cache { get; } = Global.ServiceProvider.GetService<ICache>();

		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
			=> context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD")
				? this.ShowAsync(context, cancellationToken)
				: context.Request.Method.IsEquals("POST")
					? this.UpdateAsync(context, cancellationToken)
					: Task.FromException(new MethodNotAllowedException(context.Request.Method));

		#region Show the thumbnail image
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

			// prepare information
			var requestUrl = $"{requestUri}";
			var queryString = requestUri.ParseQuery();

			var pathSegments = requestUri.GetRequestPathSegments();

			var serviceName = pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "";
			var systemID = pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1] : "";
			var identifier = pathSegments.Length > 5 && pathSegments[5].IsValidUUID() ? pathSegments[5] : "";
			if (string.IsNullOrWhiteSpace(identifier) || (string.IsNullOrWhiteSpace(serviceName) && string.IsNullOrWhiteSpace(systemID)))
			{
				var ex = new InvalidRequestException();
				await Task.WhenAll(
					context.WriteAsync(ThumbnailHandler.Generate(ex.Message).ToBytes(), "image/jpeg; charset=utf-8", null, null, 0, "private", TimeSpan.Zero, cancellationToken),
					!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"Error occurred while preparing:{ex.Message}", ex)
				).ConfigureAwait(false);
				return;
			}

			var handlerName = pathSegments[0];
			var isPng = handlerName.IsEndsWith("pngs");
			var isBig = handlerName.IsEndsWith("bigs") || handlerName.IsEndsWith("bigpngs");
			var isAttachment = pathSegments.Length > 2 && pathSegments[2].Equals("1");
			if (!Int32.TryParse(pathSegments.Length > 3 ? pathSegments[3] : "", out int width))
				width = 0;
			if (!Int32.TryParse(pathSegments.Length > 4 ? pathSegments[4] : "", out int height))
				height = 0;
			var isCropped = requestUrl.IsContains("--crop") || queryString.ContainsKey("crop");
			var croppedPosition = requestUrl.IsContains("--crop-top") || "top".IsEquals(context.GetQueryParameter("cropPos")) ? "top" : requestUrl.IsContains("--crop-bottom") || "bottom".IsEquals(context.GetQueryParameter("cropPos")) ? "bottom" : "auto";
			var isUseAdditionalWatermark = queryString.ContainsKey("nw") ? false : requestUrl.IsContains("--btwm");

			// check exist
			var fileName = isAttachment
				? "-" + (pathSegments.Length > 6 ? pathSegments[6] : "")
				: (pathSegments.Length > 6 && pathSegments[6].Length.Equals(1) && !pathSegments[6].Equals("0") ? "-" + pathSegments[6] : "") + ".jpg";
			var filePath = Path.Combine(Handler.AttachmentFilesPath, serviceName != "" ? serviceName : systemID, identifier + fileName);

			var fileInfo = new FileInfo(filePath);
			if (!fileInfo.Exists)
			{
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
				return;
			}

			// check permission
			async Task<bool> gotRightsAsync(CancellationToken cancelToken)
			{
				if (!isAttachment)
					return true;

				var attachment = await Handler.GetAttachmentAsync(identifier, context.GetSession(), cancelToken).ConfigureAwait(false);
				return await context.CanDownloadAsync(attachment.ServiceName, attachment.SystemID, attachment.DefinitionID, attachment.ObjectID).ConfigureAwait(false);
			}

			// generate
			async Task<byte[]> generateAsync(CancellationToken token)
			{
				var masterKey = "Thumbnnail#" + filePath.ToLower().GenerateUUID();
				var detailKey = $"{masterKey}x{width}x{height}x{isPng}x{isBig}x{isCropped}x{croppedPosition}".ToLower();

				var thumbnail = await this.Cache.GetAsync<byte[]>(detailKey, token).ConfigureAwait(false);
				if (thumbnail != null)
					return thumbnail;

				using (var stream = UtilityService.CreateMemoryStream())
				{
					using (var image = this.Generate(filePath, width, height, isBig, isCropped, croppedPosition))
					{
						// add watermark

						// get thumbnail image
						image.Save(stream, isPng ? ImageFormat.Png : ImageFormat.Jpeg);
					}
					thumbnail = stream.ToBytes();
				}

				var keys = await this.Cache.GetAsync<HashSet<string>>(masterKey, token).ConfigureAwait(false) ?? new HashSet<string>();
				keys.Append(detailKey);

				await Task.WhenAll(
					this.Cache.SetAsync(masterKey, keys, 0, token),
					this.Cache.SetAsFragmentsAsync(detailKey, thumbnail, 0, token)
				).ConfigureAwait(false);

				return thumbnail;
			}

			// do the generate process
			try
			{
				var thumbnail = new byte[0];
				using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Global.CancellationTokenSource.Token, context.RequestAborted))
				{
					var generateThumbnailTask = generateAsync(cts.Token);
					if (await gotRightsAsync(cts.Token).ConfigureAwait(false))
					{
						thumbnail = await generateThumbnailTask.ConfigureAwait(false);
						context.SetResponseHeaders((int)HttpStatusCode.OK, new Dictionary<string, string>
						{
							{ "Cache-Control", "public" },
							{ "Expires", $"{DateTime.Now.AddDays(7).ToHttpString()}" },
							{ "X-CorrelationID", context.GetCorrelationID() }
						});
					}
					else
					{
						if (!context.User.Identity.IsAuthenticated)
							context.Redirect(context.GetPassportSessionAuthenticatorUrl());
						else
							throw new AccessDeniedException();
					}
				}
				await Task.WhenAll(
					context.WriteAsync(thumbnail, $"image/{(isPng ? "png" : "jpeg")}; charset=utf-8", null, eTag, fileInfo.LastWriteTime.ToUnixTimestamp(), "public", TimeSpan.FromDays(7), cancellationToken),
					!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"Successfully show a thumbnail image [{fileInfo.FullName}]")
				).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				await Task.WhenAll(
					context.WriteAsync(ThumbnailHandler.Generate(ex.Message).ToBytes(), "image/jpeg; charset=utf-8", null, null, 0, "private", TimeSpan.Zero, cancellationToken),
					!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"Error occurred while processing:{ex.Message}", ex)
				).ConfigureAwait(false);
			}
		}
		#endregion

		#region Generate thumbnail image
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
		#endregion

		#region Update thumbnails
		async Task UpdateAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var serviceName = context.GetParameter("service-name") ?? context.GetParameter("x-service-name");
			var systemID = context.GetParameter("system-id") ?? context.GetParameter("x-system-id");
			var definitionID = context.GetParameter("definition-id") ?? context.GetParameter("x-definition-id");
			var objectName = context.GetParameter("object-name") ?? context.GetParameter("x-object-name");
			var objectID = context.GetParameter("object-identity") ?? context.GetParameter("object-id") ?? context.GetParameter("x-object-id");
			var isTemporary = "true".IsEquals(context.GetParameter("x-temporary"));
			var isCreateNew = "true".IsEquals(context.GetParameter("x-create-new"));

			if (string.IsNullOrWhiteSpace(objectID))
				throw new InvalidRequestException("Invalid object identity");

			// check permissions
			var gotRights = false;
			if (!isTemporary)
			{
				if (!string.IsNullOrWhiteSpace(systemID) && !string.IsNullOrWhiteSpace(definitionID))
				{
					gotRights = isCreateNew
						? await context.CanContributeAsync(serviceName, systemID, definitionID, "").ConfigureAwait(false)
						: await context.CanEditAsync(serviceName, systemID, definitionID, objectID).ConfigureAwait(false);
				}
				else
				{
					gotRights = isCreateNew
						? await context.CanContributeAsync(serviceName, objectName, "").ConfigureAwait(false)
						: await context.CanEditAsync(serviceName, objectName, objectID).ConfigureAwait(false);
				}
			}
			else
				gotRights = !string.IsNullOrWhiteSpace(systemID) && !string.IsNullOrWhiteSpace(definitionID)
					? await context.CanEditAsync(serviceName, systemID, definitionID, objectID).ConfigureAwait(false)
					: await context.CanEditAsync(serviceName, objectName, objectID).ConfigureAwait(false);

			if (!gotRights)
				throw new AccessDeniedException();

			// limit size - default is 512 KB
			if (!Int32.TryParse(UtilityService.GetAppSetting("Limits:Thumbnail"), out var limitSize))
				limitSize = 512;

			// read upload file
			var contents = new List<byte[]>();
			var asBase64 = context.GetParameter("x-as-base64") != null;
			if (asBase64)
			{
				var body = (await context.ReadTextAsync(cancellationToken).ConfigureAwait(false)).ToExpandoObject();
				var data = body.Get<string>("Data");
				if (data.StartsWith("["))
					(data.ToJson() as JArray).ForEach(file =>
					{
						var content = (file as JValue).Value?.CastAs<string>()?.ToArray().Last().Base64ToBytes();
						if (content != null && content.Length > limitSize * 1024)
							content = null;
						contents.Add(content);
					});
				else
					contents.Add(data.ToArray().Last().Base64ToBytes());
			}
			else
				await context.Request.Form.Files.ForEachAsync(async (file, token) =>
				{
					if (file == null || file.Length < 1 || !file.ContentType.IsStartsWith("image/") || file.Length > limitSize * 1024)
						contents.Add(null);
					else
						using (var stream = file.OpenReadStream())
						{
							var content = new byte[file.Length];
							await stream.ReadAsync(content, 0, (int)file.Length, token).ConfigureAwait(false);
							contents.Add(content);
						}
				}, cancellationToken, true, false).ConfigureAwait(false);

			// save into disc
			var path = Path.Combine(Handler.AttachmentFilesPath, serviceName != "" ? serviceName : systemID);
			var pathTemp = Path.Combine(path, "temp");
			var pathTrash = Path.Combine(path, "trash");
			new[] { path, pathTemp, pathTrash }.ForEach(directory =>
			{
				if (!Directory.Exists(directory))
					Directory.CreateDirectory(directory);
			});

			var response = new JArray();
			var uri = $"{context.GetHostUrl()}/thumbnails/{(serviceName != "" ? serviceName : systemID)}/0/0/0";
			var title = (context.GetParameter("x-object-title") ?? UtilityService.NewUUID).GetANSIUri();
			await contents.ForEachAsync(async (content, index, token) =>
			{
				if (content != null)
				{
					var fileName = $"{objectID}{(index > 0 ? $"-{index}" : "")}.jpg";
					var filePath = Path.Combine(isTemporary ? pathTemp : path, fileName);
					if (File.Exists(filePath))
						File.Move(filePath, Path.Combine(pathTrash, fileName));
					await UtilityService.WriteBinaryFileAsync(filePath, content, token).ConfigureAwait(false);
					response.Add(new JValue($"{uri}/{objectID}/{index}/{DateTime.Now.ToString("HHmmss")}/{title}.jpg"));
				}
			}, cancellationToken, true, false).ConfigureAwait(false);

			// response
			await Task.WhenAll(
				context.WriteAsync(new JObject
				{
					{ "URIs", response }
				}, cancellationToken),
				!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync(this.Logger, "Http.Thumbnails", $"{contents.Count(content => content != null)} thumbnail image(s) has been uploaded - Mode:  {(asBase64 ? "base64" : "file")}")
			).ConfigureAwait(false);
		}
		#endregion

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