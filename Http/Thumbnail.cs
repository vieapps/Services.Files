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
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class ThumbnailHandler : AbstractHttpHandler
	{
		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (!context.Request.HttpMethod.IsEquals("GET") && !context.Request.HttpMethod.IsEquals("HEAD"))
				throw new InvalidRequestException();

			// prepare information
			ThumbnailInfo info;
			try
			{
				info = this.Prepare(context);
			}
			catch (Exception ex)
			{
				if (!context.Response.IsClientConnected)
					await context.WriteDataToOutputAsync(this.GenerateThumbnail(ex.Message), "image/jpeg");
				return;
			}

			// stop if the client is disconnected
			if (!context.Response.IsClientConnected)
				return;

			// check "If-Modified-Since" request to reduce traffict
			var eTag = "Thumbnail#" + context.Request.RawUrl.Trim().ToLower().GetMD5();
			FileInfo fileInfo = null;
			if (context.Request.Headers["If-Modified-Since"] != null)
			{
				// get file information
				fileInfo = new FileInfo(info.FilePath);

				// compare time of modification
				DateTime modifiedSince = context.Request.Headers["If-Modified-Since"].FromHttpDateTime();
				double diffSeconds = 1.0d;
				if (!modifiedSince.Equals(DateTimeService.CheckingDateTime))
					diffSeconds = (fileInfo.LastWriteTime - modifiedSince).TotalSeconds;
				bool isNotModified = diffSeconds < 1.0d;

				// compare entity tag
				bool isMatched = true;
				if (context.Request.Headers["If-None-Match"] != null)
					isMatched = context.Request.Headers["If-None-Match"].Equals(eTag);

				// add 304 (not modified) code to tell browser that the content is not modified
				if (isNotModified && isMatched)
				{
					context.Response.Cache.SetCacheability(HttpCacheability.Public);
					context.Response.StatusCode = (int)HttpStatusCode.NotModified;
					context.Response.StatusDescription = "Not Modified";
					context.Response.AppendHeader("ETag", "\"" + eTag + "\"");
					return;
				}
			}

			// check exist
			if (!File.Exists(info.FilePath))
			{
				Global.ShowError(context, 404, "Not Found", "FileNotFoundException", null, new FileNotFoundException(info.FilePath));
				return;
			}

			// perform actions
			var cts = new CancellationTokenSource();
			var generateTask = this.GenerateThumbnailAsync(info, cts.Token);
			var checkTask = this.CheckPermissionAsync(info, cts.Token);

			// stop if the client is disconnected
			if (!context.Response.IsClientConnected)
			{
				cts.Cancel();
				return;
			}

			// wait for the checking permission task is completed
			await checkTask;

			// no permission
			if (!checkTask.Result)
			{
				// stop if the client is disconnected
				cts.Cancel();
				if (!context.Response.IsClientConnected)
					return;

				// if has no right to download and un-authorized, then trasnfer to passport first
				if (!context.Request.IsAuthenticated && context.Request.QueryString["r"] == null)
				{
					// TO DO: redirect to passport
				}

				// generate thumbnail with error message
				else
					await context.WriteDataToOutputAsync(this.GenerateThumbnail("403 - Forbidden"), "image/jpeg");
			}

			// can view
			else
			{
				// stop if the client is disconnected
				if (!context.Response.IsClientConnected)
				{
					cts.Cancel();
					return;
				}

				// wait for the generate thumbnail task is completed
				await generateTask;

				// re-check
				if (!context.Response.IsClientConnected || generateTask.Result == null)
					return;

				// set cache policy at client-side
				if (fileInfo == null)
					fileInfo = new FileInfo(info.FilePath);

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
				await context.WriteDataToOutputAsync(generateTask.Result, "image/" + (info.AsPng ? "png" : "jpeg"), eTag, fileInfo.LastWriteTime.ToHttpString());
			}
		}

		#region Prepare information
		ThumbnailInfo Prepare(HttpContext context)
		{
			var requestUrl = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length);
			if (requestUrl.StartsWith("/"))
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
				+ (info.IsAttachment ? "-" + requestInfo[6] : (requestInfo.Length > 6 && requestInfo[6].Length.Equals(1) && !requestInfo[6].Equals("0") ? "-" + requestInfo[6] : "") + ".jpg");
			info.FilePath = Global.AttachmentFilesPath + info.SystemID + @"\" + info.Filename;

			info.Cropped = requestUrl.IsContains("--crop") || context.Request.QueryString["crop"] != null;
			info.CroppedPosition = requestUrl.IsContains("--crop-top") || (context.Request.QueryString["cropPos"] != null && context.Request.QueryString["cropPos"].IsEquals("top"))
				? "top"
				: requestUrl.IsContains("--crop-bottom") || (context.Request.QueryString["cropPos"] != null && context.Request.QueryString["cropPos"].IsEquals("bottom"))
					? "bottom"
					: "auto";

			info.UseAdditionalWatermark = requestUrl.IsContains("--btwm");

			return info;
		}
		#endregion

		#region Check permission
		Task<bool> CheckPermissionAsync(ThumbnailInfo info, CancellationToken cancellationToken)
		{
			if (!info.IsAttachment)
				return Task.FromResult(true);

			return Task.FromResult(true);
		}
		#endregion

		#region Generate thumbnail
		Task<byte[]> GenerateThumbnailAsync(ThumbnailInfo info, CancellationToken cancellationToken)
		{
			try
			{
				using (var image = this.GenerateThumbnail(info.FilePath, info.Width, info.Height, info.AsBig, info.AsPng, info.Cropped, info.CroppedPosition))
				{
					// add watermark
					cancellationToken.ThrowIfCancellationRequested();
					if (info.UseWatermark)
					{

					}

					// export image
					cancellationToken.ThrowIfCancellationRequested();
					using (var stream = new MemoryStream())
					{
						image.Save(stream, info.AsPng ? ImageFormat.Png : ImageFormat.Jpeg);
						return Task.FromResult(stream.ToArray());
					}
				}
			}
			catch (Exception ex)
			{
				return Task.FromException<byte[]>(ex);
			}
		}

		Bitmap GenerateThumbnail(string filePath, int width, int height, bool asBig, bool asPng, bool isCropped, string cropPosition)
		{
			using (var image = Image.FromFile(filePath) as Bitmap)
			{
				// clone original image
				if ((width < 1 && height < 1) || (width.Equals(image.Width) && height.Equals(image.Height)))
					return image.Clone() as Bitmap;

				// calculate width & height of the thumbnail image
				int thumbnailWidth = width, thumbnailHeight = height;

				// calculate size depend on width
				if (thumbnailHeight.Equals(0))
				{
					thumbnailHeight = (int)((image.Height * thumbnailWidth) / image.Width);
					if (thumbnailHeight < 1)
						thumbnailHeight = image.Height;
				}

				// calculate size depend on height
				else
				{
					if (thumbnailWidth.Equals(0))
					{
						thumbnailWidth = (int)((image.Width * thumbnailHeight) / image.Height);
						if (thumbnailWidth < 1)
							thumbnailWidth = image.Width;
					}
				}

				// generate cropped thumbnail
				if (isCropped)
					return this.GenerateCroppedThumbnail(image, thumbnailWidth, thumbnailHeight, cropPosition, asBig, asPng);

				// generate fixed thumbnail
				else
					return this.GenerateFixedThumbnail(image, thumbnailWidth, thumbnailHeight, asBig, asPng);
			}
		}

		Bitmap GenerateCroppedThumbnail(Bitmap image, int width, int height, string position, bool asBig, bool asPng)
		{
			using (var thumbnail = this.GenerateFixedThumbnail(image, width, (image.Height * width) / image.Width, asBig, asPng))
			{
				// if height is less than thumbnail image's height, then return thumbnail image
				if (thumbnail.Height <= height)
					return thumbnail.Clone() as Bitmap;

				// crop image
				int top = position.IsEquals("auto")
					? (thumbnail.Height - height) / 2
					: position.IsEquals("bottom")
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

		Bitmap GenerateFixedThumbnail(Bitmap image, int width, int height, bool asBig, bool asPng)
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
		#endregion

		#region Generate thumbnail with error message
		byte[] GenerateThumbnail(string text, int width = 300, int height = 100)
		{
			using (var bitmap = new Bitmap(width, height, PixelFormat.Format16bppRgb555))
			{
				using (var graphics = Graphics.FromImage(bitmap))
				{
					graphics.SmoothingMode = SmoothingMode.AntiAlias;
					graphics.Clear(Color.White);
					graphics.DrawString(text, new Font("Arial", 16, FontStyle.Bold), SystemBrushes.WindowText, new PointF(10, 40));
					using (var stream = new MemoryStream())
					{
						bitmap.Save(stream, ImageFormat.Png);
						return stream.ToArray();
					}
				}
			}
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
		/// Gets or sets position of the watermark (possible values: auto, top, left)
		/// </summary>
		public string Position { get; set; }

		/// <summary>
		/// Gets or sets offset of the watermark
		/// </summary>
		public Point Offset { get; set; }
	}
	#endregion

}