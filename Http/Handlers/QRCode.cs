#region Related component
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
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
			var data = new ArraySegment<byte>([]);
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

				if (!query.TryGetValue("ecl", out var ecLevel))
					ecLevel = "M";

				if (!query.TryGetValue("i", out var image))
					image = "";

				// generate QR code
				using var chart = await new Uri($"https://quickchart.io/qr?text={value.UrlEncode()}&size={size}&ecLevel={ecLevel}{(string.IsNullOrWhiteSpace(image) ? "" : $"&centerImageUrl={image.UrlEncode()}")}&margin=1").SendHttpRequestAsync(cancellationToken).ConfigureAwait(false);
				data = (await chart.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)).ToArraySegment();

				stopwatch.Stop();
				if (Global.IsDebugLogEnabled)
					await Global.WriteLogsAsync(this.Logger, "Http.QRCodes", $"Generate QR Code successful: {value} - [Size: {size} - EC Level: {ecLevel}] - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
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
	}
}