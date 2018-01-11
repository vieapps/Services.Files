#region Related component
using System;
using System.IO;
using System.Web;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Diagnostics;

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
				var value = !string.IsNullOrWhiteSpace(context.Request.QueryString["v"])
					? context.Request.QueryString["v"].ToBase64(false, true).Decrypt(Base.AspNet.Global.EncryptionKey)
					: context.Request.QueryString["d"];
				if (string.IsNullOrWhiteSpace(value))
					throw new InvalidRequestException();

				if (!string.IsNullOrWhiteSpace(context.Request.QueryString["t"]))
				{
					var timestamp = context.Request.QueryString["t"].ToBase64(false, true).Decrypt(Base.AspNet.Global.EncryptionKey).CastAs<long>();
					if (DateTime.Now.ToUnixTimestamp() - timestamp > 60)
						throw new InvalidRequestException();
				}

				var size = (context.Request.QueryString["s"] ?? "300").CastAs<int>();
				if (!Enum.TryParse(context.Request.QueryString["ecl"] ?? "M", out QRCodeGenerator.ECCLevel level))
					level = QRCodeGenerator.ECCLevel.M;

#if DEBUG || QRCODELOGS
				var stopwatch = new Stopwatch();
				stopwatch.Start();
#endif

				// generate QR code using QRCoder
				if ("QRCoder".IsEquals(UtilityService.GetAppSetting("QRCode:Provider")))
					data = this.GenerateQRCode(value, size, level);

				// generate QR code using Google APIs
				else
					try
					{
						data = await UtilityService.DownloadAsync($"https://chart.apis.google.com/chart?cht=qr&chs={size}x{size}&chl={value.UrlEncode()}").ConfigureAwait(false);
					}
					catch
					{
						data = this.GenerateQRCode(value, size, level);
					}
#if DEBUG || QRCODELOGS
				stopwatch.Stop();
				await Base.AspNet.Global.WriteLogsAsync($"Generate QR Code successful: {value} - [Size: {size} - ECC Level: {level}] - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
				context.Response.AppendHeader("x-qr-code-value", value);
#endif
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
			await context.Response.FlushAsync().ConfigureAwait(false);
		}

		byte[] GenerateQRCode(string value, int size, QRCodeGenerator.ECCLevel level)
		{
			using (var generator = new QRCodeGenerator())
			using (var data = generator.CreateQrCode(value, level))
			using (var code = new QRCode(data))
			using (var big = code.GetGraphic(20))
			using (var small = new Bitmap(size, size))
			using (var graphics = Graphics.FromImage(small))
			{
				graphics.SmoothingMode = SmoothingMode.HighQuality;
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
				graphics.DrawImage(big, new Rectangle(0, 0, size, size));
				using (var stream = new MemoryStream())
				{
					small.Save(stream, ImageFormat.Png);
					return stream.GetBuffer();
				}
			}
		}
	}
}