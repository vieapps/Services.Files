#region Related component
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Collections.Generic;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class ThumbnailHandler : FileHttpHandler
	{
		ILogger Logger { get; set; }

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			this.Logger = Components.Utility.Logger.CreateLogger<ThumbnailHandler>();

			if (context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD"))
				await this.ShowAsync(context, cancellationToken).ConfigureAwait(false);
			else if (context.Request.Method.IsEquals("POST"))
				await this.UpdateAsync(context, cancellationToken).ConfigureAwait(false);
			else
				throw new MethodNotAllowedException(context.Request.Method);
		}

		#region Show the thumbnail image
		async Task ShowAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// check "If-Modified-Since" request to reduce traffict
			var requestUri = context.GetRequestUri();
			var eTag = "Thumbnail#" + $"{requestUri}".ToLower().GetMD5();
			if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")) && context.GetHeaderParameter("If-Modified-Since") != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", context.GetCorrelationID());
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Response to request with status code 304 to reduce traffic ({requestUri})").ConfigureAwait(false);
				return;
			}

			// prepare information
			var requestUrl = $"{requestUri}";
			var queryString = requestUri.ParseQuery();
			ThumbnailInfo info;

			try
			{
				var requestInfo = requestUri.GetRequestPathSegments();

				if (requestInfo.Length < 6 || !requestInfo[1].IsValidUUID() || !requestInfo[5].IsValidUUID())
					throw new InvalidRequestException();

				info = new ThumbnailInfo(requestInfo[1], requestInfo[3].CastAs<int>(), requestInfo[4].CastAs<int>(), requestInfo[5])
				{
					AsPng = requestInfo[0].IsEndsWith("pngs"),
					AsBig = requestInfo[0].IsEndsWith("bigs") || requestInfo[0].IsEndsWith("bigpngs"),
					IsAttachment = requestInfo[2].Equals("1")
				};

				info.Filename = info.Identifier + (info.IsAttachment ? "-" + requestInfo[6] : (requestInfo.Length > 6 && requestInfo[6].Length.Equals(1) && !requestInfo[6].Equals("0") ? "-" + requestInfo[6] : "") + ".jpg");
				info.FilePath = Path.Combine(Handler.AttachmentFilesPath, info.SystemID, info.Filename);
				info.Cropped = requestUrl.IsContains("--crop") || queryString.ContainsKey("crop");
				info.CroppedPosition = requestUrl.IsContains("--crop-top") || "top".IsEquals(context.GetQueryParameter("cropPos"))
					? "top"
					: requestUrl.IsContains("--crop-bottom") || "bottom".IsEquals(context.GetQueryParameter("cropPos"))
						? "bottom"
						: "auto";

				info.UseAdditionalWatermark = queryString.ContainsKey("nw")
					? false
					: requestUrl.IsContains("--btwm");
			}
			catch (Exception ex)
			{
				await Task.WhenAll(
					context.WriteAsync(ThumbnailHandler.Generate(ex.Message).ToBytes(), "image/jpeg; charset=utf-8", null, null, 0, "private", TimeSpan.Zero, cancellationToken),
					!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync(this.Logger, "Thumbnails", $"Error occurred while preparing:{ex.Message}", ex)
				).ConfigureAwait(false);
				return;
			}

			// check exist
			var fileInfo = new FileInfo(info.FilePath);
			if (!fileInfo.Exists)
			{
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
				return;
			}

			// check permission
			async Task<bool> gotRightsAsync(CancellationToken cancelToken)
			{
				if (!info.IsAttachment)
					return true;

				var attachment = await Handler.GetAttachmentAsync(info.Identifier, context.GetSession(), cancelToken).ConfigureAwait(false);
				return await context.CanDownloadAsync(attachment.ServiceName, attachment.SystemID, attachment.DefinitionID, attachment.ObjectID).ConfigureAwait(false);
			}

			// generate
			async Task<byte[]> generateAsync(CancellationToken cancelToken)
			{
				var masterKey = "Thumbnnail#" + info.FilePath.ToLower().GenerateUUID();
				var detailKey = $"{masterKey}x{info.Width}x{info.Height}x{info.AsPng}x{info.AsBig}x{info.Cropped}x{info.CroppedPosition}".ToLower();

				var thumbnail = await FileHttpHandler.Cache.GetAsync<byte[]>(detailKey, cancelToken).ConfigureAwait(false);
				if (thumbnail != null)
					return thumbnail;

				using (var stream = UtilityService.CreateMemoryStream())
				{
					using (var image = this.Generate(info.FilePath, info.Width, info.Height, info.AsBig, info.Cropped, info.CroppedPosition))
					{
						// add watermark
						if (info.UseWatermark)
						{

						}

						// get thumbnail image
						image.Save(stream, info.AsPng ? ImageFormat.Png : ImageFormat.Jpeg);
					}
					thumbnail = stream.ToBytes();
				}

				var keys = await FileHttpHandler.Cache.GetAsync<HashSet<string>>(masterKey, cancelToken).ConfigureAwait(false) ?? new HashSet<string>();
				keys.Append(detailKey);

				await Task.WhenAll(
					FileHttpHandler.Cache.SetAsync(masterKey, keys, 0, cancelToken),
					FileHttpHandler.Cache.SetAsFragmentsAsync(detailKey, thumbnail, 0, cancelToken)
				).ConfigureAwait(false);

				return thumbnail;
			}

			// do the generate process
			try
			{
				var thumbnnail = new byte[0];
				using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Global.CancellationTokenSource.Token, context.RequestAborted))
				{
					var generateThumbnailTask = generateAsync(cts.Token);
					if (await gotRightsAsync(cts.Token).ConfigureAwait(false))
					{
						thumbnnail = await generateThumbnailTask.ConfigureAwait(false);
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
							context.Response.Redirect(context.GetTransferToPassportUrl());
						else
							throw new AccessDeniedException();
					}
				}
				await Task.WhenAll(
					context.WriteAsync(thumbnnail, $"image/{(info.AsPng ? "png" : "jpeg")}; charset=utf-8", null, eTag, fileInfo.LastWriteTime.ToUnixTimestamp(), "public", TimeSpan.FromDays(7), cancellationToken),
					!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync(this.Logger, "Thumbnails", $"Successfully show thumbnail image [{fileInfo.FullName}]")
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
					!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync(this.Logger, "Thumbnails", $"Error occurred while processing:{ex.Message}", ex)
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

		#region Update thumbnails (receive uploaded images from the client)
		Task UpdateAsync(HttpContext context, CancellationToken cancellationToken)
		{
			throw new NotImplementedException();
		}
		#endregion

	}

	#region Thumbnail info	 
	public struct ThumbnailInfo
	{
		public ThumbnailInfo(string systemID, int width, int height, string identifier)
		{
			this.SystemID = systemID;
			this.Width = width;
			this.Height = height;
			this.Identifier = identifier;
			this.AsPng = false;
			this.AsBig = false;
			this.IsAttachment = false;
			this.Filename = "";
			this.FilePath = "";
			this.Cropped = false;
			this.CroppedPosition = "auto";
			this.UseWatermark = false;
			this.WatermarkInfo = new WatermarkInfo();
			this.UseAdditionalWatermark = false;
			this.AdditionalWatermarkInfo = new WatermarkInfo();
		}

		public string SystemID { get; set; }

		public int Width { get; set; }

		public int Height { get; set; }

		public string Identifier { get; set; }

		public bool AsPng { get; set; }

		public bool AsBig { get; set; }

		public bool IsAttachment { get; set; }

		public string Filename { get; set; }

		public string FilePath { get; set; }

		public bool Cropped { get; set; }

		public string CroppedPosition { get; set; }

		public bool UseWatermark { get; set; }

		public WatermarkInfo WatermarkInfo { get; set; }

		public bool UseAdditionalWatermark { get; set; }

		public WatermarkInfo AdditionalWatermarkInfo { get; set; }
	}
	#endregion

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