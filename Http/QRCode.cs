#region Related component
using System;
using System.IO;
using System.Web;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;

using QRCoder;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class QRCodeHandler : AbstractHttpHandler
	{
		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			// check
			if (!context.Request.HttpMethod.IsEquals("GET"))
				throw new InvalidRequestException();

			// generate
			byte[] data = null;
			try
			{
				// prepare
				var value = context.Request.QueryString["v"];
				if (!string.IsNullOrWhiteSpace(value))
					value = value.FromBase64Url().Decrypt(Base.AspNet.Global.EncryptionKey);
				else
					value = context.Request.QueryString["d"];
				if (string.IsNullOrWhiteSpace(value))
					throw new InvalidRequestException();

				var timestamp = context.Request.QueryString["t"];
				if (!string.IsNullOrWhiteSpace(timestamp))
				{
					timestamp = timestamp.FromBase64Url().Decrypt(Base.AspNet.Global.EncryptionKey);
					if (DateTime.Now.ToUnixTimestamp() - timestamp.CastAs<long>() > 60)
						throw new InvalidRequestException();
				}

				var size = (context.Request.QueryString["s"] ?? "300").CastAs<int>();
				if (Enum.TryParse(context.Request.QueryString["ecl"] ?? "M", out QRCodeGenerator.ECCLevel level))
					level = QRCodeGenerator.ECCLevel.M;

				// generate QR code using QRCoder
				if ("QRCoder".IsEquals(UtilityService.GetAppSetting("QRCode:Provider")))
					data = this.GenerateQRCode(value, size, level);

				// generate QR code using Google Chart
				else
					try
					{
						data = await UtilityService.DownloadAsync($"https://chart.apis.google.com/chart?cht=qr&chs={size}x{size}&chl={value}").ConfigureAwait(false);
					}
					catch
					{
						data = this.GenerateQRCode(value, size, level);
					}
			}
			catch (Exception ex)
			{
				await Base.AspNet.Global.WriteLogsAsync("Error occurred while generating the QR Code", ex).ConfigureAwait(false);
				data = ThumbnailHandler.GenerateErrorImage(ex.Message, 300, 300, true);
			}

			// display
			context.Response.Cache.SetNoStore();
			context.Response.ContentType = "image/png";
			await context.Response.OutputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
		}

		byte[] GenerateQRCode(string value, int size, QRCodeGenerator.ECCLevel level)
		{
			using (var generator = new QRCodeGenerator())
			using (var data = generator.CreateQrCode(value, level))
			using (var code = new QRCode(data))
			using (var bigImage = code.GetGraphic(20))
			using (var smallImage = new Bitmap(size, size))
			using (var graphics = Graphics.FromImage(smallImage))
			{
				graphics.SmoothingMode = SmoothingMode.HighQuality;
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
				graphics.DrawImage(bigImage, new Rectangle(0, 0, size, size));
				using (var stream = new MemoryStream())
				{
					smallImage.Save(stream, ImageFormat.Png);
					return stream.GetBuffer();
				}
			}
		}
	}
}