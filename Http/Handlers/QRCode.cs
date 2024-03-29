﻿#region Related component
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using QRCoder;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class QRCodeHandler : Services.FileHandler
	{
		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
			=> context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD")
				? this.ShowAsync(context, cancellationToken)
				: Task.FromException(new MethodNotAllowedException(context.Request.Method));

		async Task ShowAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// generate
			var data = new ArraySegment<byte>(Array.Empty<byte>());
			var size = 300;
			var stopwatch = Stopwatch.StartNew();

			try
			{
				// prepare
				var query = context.GetRequestUri().ParseQuery();
				var value = query.TryGetValue("v", out var cvalue) && !string.IsNullOrWhiteSpace(cvalue)
					? cvalue.ToBase64(false, true).Decrypt(Global.EncryptionKey)
					: query.TryGetValue("d", out var dvalue) ? dvalue : null;
				if (string.IsNullOrWhiteSpace(value))
					throw new InvalidRequestException();

				if (query.TryGetValue("t", out var tvalue))
				{
					var timestamp = tvalue.ToBase64(false, true).Decrypt(Global.EncryptionKey).CastAs<long>();
					if (DateTime.Now.ToUnixTimestamp() - timestamp > 90)
						throw new InvalidRequestException();
				}

				size = (query.TryGetValue("s", out var svalue) ? svalue : "300").CastAs<int>();

				if (!Enum.TryParse(query.TryGetValue("ecl", out string evalue) ? evalue : "M", out QRCodeGenerator.ECCLevel level))
					level = QRCodeGenerator.ECCLevel.M;

				// generate QR code using QRCoder
				if ("QRCoder".IsEquals(UtilityService.GetAppSetting("Files:QRCodeProvider")))
					data = this.Generate(value, size, level);

				// generate QR code using Google Chart APIs
				else
					try
					{
						using var googleChart = await new Uri($"https://chart.apis.google.com/chart?cht=qr&chs={size}x{size}&chl={value.UrlEncode()}").SendHttpRequestAsync(cancellationToken).ConfigureAwait(false);
						data = (await googleChart.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)).ToArraySegment();
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
			using var generator = new QRCodeGenerator();
			using var data = generator.CreateQrCode(value, level);
			using var code = new QRCode(data);
			using var bigBmp = code.GetGraphic(20);
			using var smallBmp = new Bitmap(size, size);
			using var graphics = Graphics.FromImage(smallBmp);
			graphics.SmoothingMode = SmoothingMode.HighQuality;
			graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
			graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
			graphics.DrawImage(bigBmp, new Rectangle(0, 0, size, size));
			using var stream = UtilityService.CreateMemoryStream();
			smallBmp.Save(stream, ImageFormat.Png);
			return stream.ToArraySegment();
		}
	}
}