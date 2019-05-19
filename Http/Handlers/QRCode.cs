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
using System.Diagnostics;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using QRCoder;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class QRCodeHandler : Services.FileHandler
	{
		public override ILogger Logger { get; } = Components.Utility.Logger.CreateLogger<QRCodeHandler>();

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
		{
			if (context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD"))
				await this.ShowAsync(context, cancellationToken).ConfigureAwait(false);
			else
				throw new MethodNotAllowedException(context.Request.Method);
		}

		async Task ShowAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// generate
			var data = new ArraySegment<byte>(new byte[0]);
			var size = 300;
			var stopwatch = Stopwatch.StartNew();

			try
			{
				// prepare
				var query = context.GetRequestUri().ParseQuery();
				var value = query.ContainsKey("v") && !string.IsNullOrWhiteSpace(query["v"])
					? query["v"].ToBase64(false, true).Decrypt(Global.EncryptionKey)
					: query.ContainsKey("d") ? query["d"] : null;
				if (string.IsNullOrWhiteSpace(value))
					throw new InvalidRequestException();

				if (query.ContainsKey("t"))
				{
					var timestamp = query["t"].ToBase64(false, true).Decrypt(Global.EncryptionKey).CastAs<long>();
					if (DateTime.Now.ToUnixTimestamp() - timestamp > 90)
						throw new InvalidRequestException();
				}

				size = (query.ContainsKey("s") ? query["s"] : "300").CastAs<int>();

				if (!Enum.TryParse(query.ContainsKey("ecl") ? query["ecl"] : "M", out QRCodeGenerator.ECCLevel level))
					level = QRCodeGenerator.ECCLevel.M;

				// generate QR code using QRCoder
				if ("QRCoder".IsEquals(UtilityService.GetAppSetting("Files:QRCodeProvider")))
					data = this.Generate(value, size, level);

				// generate QR code using Google APIs
				else
					try
					{
						data = (await UtilityService.DownloadAsync($"https://chart.apis.google.com/chart?cht=qr&chs={size}x{size}&chl={value.UrlEncode()}", null, null, cancellationToken).ConfigureAwait(false)).ToArraySegment();
					}
					catch (Exception ex)
					{
						await Global.WriteLogsAsync(this.Logger, "Http.QRCodes", $"Error occurred while generating the QR Code by Google Chart APIs: {ex.Message}", ex).ConfigureAwait(false);
						data = this.Generate(value, size, level);
					}

				stopwatch.Stop();
				if (Global.IsDebugLogEnabled)
					await Global.WriteLogsAsync(this.Logger, "Http.QRCodes", $"Generate QR Code successful: {value} - [Size: {size} - ECC Level: {level}] - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await Global.WriteLogsAsync(this.Logger, "Http.QRCodes", $"Error occurred while generating the QR Code: {ex.Message}", ex).ConfigureAwait(false);
				data = ThumbnailHandler.Generate(ex.Message, size, size, true);
			}

			// display
			context.SetResponseHeaders((int)HttpStatusCode.OK, "image/png", null, 0, "private, no-store, no-cache", TimeSpan.Zero, context.GetCorrelationID());
			await context.WriteAsync(data, cancellationToken).ConfigureAwait(false);
		}

		ArraySegment<byte> Generate(string value, int size, QRCodeGenerator.ECCLevel level)
		{
			using (var generator = new QRCodeGenerator())
			using (var data = generator.CreateQrCode(value, level))
			using (var code = new QRCode(data))
			using (var bigBmp = code.GetGraphic(20))
			using (var smallBmp = new Bitmap(size, size))
			using (var graphics = Graphics.FromImage(smallBmp))
			{
				graphics.SmoothingMode = SmoothingMode.HighQuality;
				graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
				graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
				graphics.DrawImage(bigBmp, new Rectangle(0, 0, size, size));
				using (var stream = UtilityService.CreateMemoryStream())
				{
					smallBmp.Save(stream, ImageFormat.Png);
					return stream.ToArraySegment();
				}
			}
		}
	}
}