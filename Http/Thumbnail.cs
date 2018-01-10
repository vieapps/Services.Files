#region Related component
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Web;

using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class ThumbnailHandler : AbstractHttpHandler
	{
		protected override async Task SendInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default(CancellationToken))
		{
			await Global.SendInterCommunicateMessageAsync(message).ConfigureAwait(false);
		}

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (context.Request.HttpMethod.IsEquals("GET") || context.Request.HttpMethod.IsEquals("HEAD"))
				await this.ShowAsync(context, cancellationToken).ConfigureAwait(false);
			else if (context.Request.HttpMethod.IsEquals("POST"))
				await this.UpdateAsync(context, cancellationToken).ConfigureAwait(false);
			else
				throw new MethodNotAllowedException(context.Request.HttpMethod);
		}

		#region Generate & show the thumbnail image
		async Task ShowAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare information
			ThumbnailInfo info;
			try
			{
				info = this.Prepare(context);
			}
			catch (Exception ex)
			{
				if (!context.Response.IsClientConnected)
					await context.WriteDataToOutputAsync(ThumbnailHandler.GenerateErrorImage(ex.Message), "image/jpeg", null, null, null, cancellationToken).ConfigureAwait(false);
				return;
			}

			// stop if the client is disconnected
			if (!context.Response.IsClientConnected)
				return;

			// check "If-Modified-Since" request to reduce traffict
			var eTag = "Thumbnail#" + context.Request.RawUrl.Trim().ToLower().GetMD5();
			if (context.Request.Headers["If-Modified-Since"] != null && eTag.Equals(context.Request.Headers["If-None-Match"]))
			{
				context.Response.Cache.SetCacheability(HttpCacheability.Public);
				context.Response.StatusCode = (int)HttpStatusCode.NotModified;
				context.Response.StatusDescription = "Not Modified";
				context.Response.Headers.Add("ETag", "\"" + eTag + "\"");
				return;
			}

			// check exist
			var fileInfo = new FileInfo(info.FilePath);
			if (!fileInfo.Exists)
			{
				context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", null);
				return;
			}

			// perform actions
			var cts = new CancellationTokenSource();
			var generateTask = this.GenerateThumbnailAsync(info, cts.Token);
			var checkTask = this.CheckPermissionAsync(context, info, cts.Token);

			// stop if the client is disconnected
			if (!context.Response.IsClientConnected)
			{
				cts.Cancel();
				return;
			}

			// permission
			try
			{
				// wait for completed
				await checkTask.ConfigureAwait(false);

				// no permission
				if (!checkTask.Result)
				{
					// stop the generating process
					cts.Cancel();

					// redirect or show error image
					if (context.Response.IsClientConnected)
					{
						// if has no right to download and un-authorized, then transfer to passport to re-authenticate
						if (!context.Request.IsAuthenticated && context.Request.QueryString["x-app-token"] == null && context.Request.QueryString["x-passport-token"] == null)
							context.Response.Redirect(Global.GetTransferToPassportUrl(context));

						// generate thumbnail with error message
						else
							await context.WriteDataToOutputAsync(ThumbnailHandler.GenerateErrorImage("403 - Forbidden"), "image/jpeg", null, null, null, cancellationToken).ConfigureAwait(false);
					}

					// stop process
					return;
				}
			}
			catch (Exception ex)
			{
				await context.WriteDataToOutputAsync(ThumbnailHandler.GenerateErrorImage(ex.Message), "image/jpeg", null, null, null, cancellationToken).ConfigureAwait(false);
				cts.Cancel();
				return;
			}

			// stop if the client is disconnected
			if (!context.Response.IsClientConnected)
			{
				cts.Cancel();
				return;
			}

			// generate the thumbnail image
			try
			{
				// wait for completed
				await generateTask.ConfigureAwait(false);

				// generate
				if (context.Response.IsClientConnected)
				{
					// set cache policy at client-side
					context.Response.Cache.SetCacheability(HttpCacheability.Public);
					context.Response.Cache.SetExpires(DateTime.Now.AddDays(365));
					context.Response.Cache.SetSlidingExpiration(true);
					if (context.Request.QueryString["crop"] != null)
						context.Response.Cache.VaryByParams["crop"] = true;
					if (context.Request.QueryString["cropPos"] != null)
						context.Response.Cache.VaryByParams["cropPos"] = true;
					if (info.UseWatermark && context.Request.QueryString["btwm"] != null)
						context.Response.Cache.VaryByParams["btwm"] = true;
					if (context.Request.QueryString["nw"] != null)
						context.Response.Cache.VaryByParams["nw"] = true;
					context.Response.Cache.SetOmitVaryStar(true);
					context.Response.Cache.SetValidUntilExpires(true);
					context.Response.Cache.SetLastModified(fileInfo.LastWriteTime);
					context.Response.Cache.SetETag(eTag);

					// flush thumbnail image to output stream
					await context.WriteDataToOutputAsync(generateTask.Result, "image/" + (info.AsPng ? "png" : "jpeg"), eTag, fileInfo.LastWriteTime.ToHttpString(), null, cancellationToken).ConfigureAwait(false);
				}
			}
			catch (Exception ex)
			{
				if (context.Response.IsClientConnected)
					await context.WriteDataToOutputAsync(ThumbnailHandler.GenerateErrorImage(ex.Message), "image/jpeg", null, null, null, cancellationToken).ConfigureAwait(false);
			}
		}
		#endregion

		#region Prepare information
		ThumbnailInfo Prepare(HttpContext context)
		{
			var requestUrl = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length);
			while (requestUrl.StartsWith("/"))
				requestUrl = requestUrl.Right(requestUrl.Length - 1);
			if (requestUrl.IndexOf("?") > 0)
				requestUrl = requestUrl.Left(requestUrl.IndexOf("?"));

			var requestInfo = requestUrl.ToArray('/', true);
			if (requestInfo.Length < 6 || !requestInfo[1].IsValidUUID() || !requestInfo[5].IsValidUUID())
				throw new InvalidRequestException();

			var info = new ThumbnailInfo(requestInfo[1], requestInfo[3].CastAs<int>(), requestInfo[4].CastAs<int>(), requestInfo[5])
			{
				AsPng = requestInfo[0].IsEndsWith("pngs"),
				AsBig = requestInfo[0].IsEndsWith("bigs") || requestInfo[0].IsEndsWith("bigpngs"),
				IsAttachment = requestInfo[2].Equals("1")
			};

			info.Filename = info.Identifier
				+ (info.IsAttachment
					? "-" + requestInfo[6]
					: (requestInfo.Length > 6 && requestInfo[6].Length.Equals(1) && !requestInfo[6].Equals("0") ? "-" + requestInfo[6] : "") + ".jpg");
			info.FilePath = Path.Combine(Global.AttachmentFilesPath, info.SystemID, info.Filename);

			info.Cropped = requestUrl.IsContains("--crop") || context.Request.QueryString["crop"] != null;
			info.CroppedPosition = requestUrl.IsContains("--crop-top") || "top".IsEquals(context.Request.QueryString["cropPos"])
				? "top"
				: requestUrl.IsContains("--crop-bottom") || "bottom".IsEquals(context.Request.QueryString["cropPos"])
					? "bottom"
					: "auto";

			info.UseAdditionalWatermark = context.Request.QueryString["nw"] != null
				? false
				: requestUrl.IsContains("--btwm");

			return info;
		}
		#endregion

		#region Check permission
		async Task<bool> CheckPermissionAsync(HttpContext context, ThumbnailInfo info, CancellationToken cancellationToken)
		{
			// always show thumbnail
			if (!info.IsAttachment)
				return true;

			// check permissions on attachment file
			try
			{
				var attachment = await Global.GetAttachmentAsync(info.Identifier, Global.GetSession(context), cancellationToken).ConfigureAwait(false);
				return await Global.CanDownloadAsync(attachment.ServiceName, attachment.SystemID, attachment.DefinitionID, attachment.ObjectID).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Base.AspNet.Global.WriteLogs("Error occurred while working with attachment & related privileges/permissions", ex);
				throw ex;
			}
		}
		#endregion

		#region Generate thumbnail
		Task<byte[]> GenerateThumbnailAsync(ThumbnailInfo info, CancellationToken cancellationToken)
		{
			return UtilityService.ExecuteTask(() =>
			{
				using (var image = this.GenerateThumbnail(info.FilePath, info.Width, info.Height, info.AsBig, info.Cropped, info.CroppedPosition))
				{
					// add watermark
					if (info.UseWatermark)
					{

					}

					// export image
					using (var stream = new MemoryStream())
					{
						image.Save(stream, info.AsPng ? ImageFormat.Png : ImageFormat.Jpeg);
						return stream.GetBuffer();
					}
				}
			}, cancellationToken);
		}

		Bitmap GenerateThumbnail(string filePath, int width, int height, bool asBig, bool isCropped, string cropPosition)
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
					? this.GenerateThumbnail(image, width, height, asBig, cropPosition)
					: this.GenerateThumbnail(image, width, height, asBig);
			}
		}

		Bitmap GenerateThumbnail(Bitmap image, int width, int height, bool asBig, string cropPosition)
		{
			using (var thumbnail = this.GenerateThumbnail(image, width, (image.Height * width) / image.Width, asBig))
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

		Bitmap GenerateThumbnail(Bitmap image, int width, int height, bool asBig)
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

		internal static byte[] GenerateErrorImage(string message, int width = 300, int height = 100, bool exportAsPng = false)
		{
			using (var bitmap = new Bitmap(width, height, PixelFormat.Format16bppRgb555))
			{
				using (var graphics = Graphics.FromImage(bitmap))
				{
					graphics.SmoothingMode = SmoothingMode.AntiAlias;
					graphics.Clear(Color.White);
					graphics.DrawString(message, new Font("Arial", 16, FontStyle.Bold), SystemBrushes.WindowText, new PointF(10, 40));
					using (var stream = new MemoryStream())
					{
						bitmap.Save(stream, exportAsPng ? ImageFormat.Png : ImageFormat.Jpeg);
						return stream.GetBuffer();
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