#region Related component
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Drawing.Imaging;
using Microsoft.AspNetCore.Http;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using System.Collections.Generic;
#endregion

namespace net.vieapps.Services.Files
{
	public class VietQRHandler : Services.FileHandler
	{
		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
			=> context.Request.Method.IsEquals("GET")
				? this.ShowAsync(context, cancellationToken)
				: Task.FromException(new MethodNotAllowedException(context.Request.Method));

		async Task ShowAsync(HttpContext context, CancellationToken cancellationToken)
		{
			context.SendSessionState();
			var data = Array.Empty<byte>();
			var cacheControl = "public";
			var stopwatch = Stopwatch.StartNew();
			try
			{
				var segments = context.GetRequestPathSegments().Skip(1).ToList();
				var isBase64Url = segments.Last().IsEquals("pay-as-we-go.webp");
				var base64Url = isBase64Url ? segments.First().Url64Decode().ToJson() : null;
				var id = isBase64Url ? base64Url.Get<string>("id") : segments[0];
				var accountNumber = isBase64Url ? base64Url.Get<string>("accountNumber") : segments[1];
				var amount = isBase64Url ? base64Url.Get<string>("amount") : segments[2];
				var description = isBase64Url ? base64Url.Get<string>("description") : segments[3].Replace("--", " ");
				var accountName = isBase64Url ? base64Url.Get<string>("accountName") : segments[4].Replace("--", " ");
				using var vietQR = await new Uri($"https://img.vietqr.io/image/{id}-{accountNumber}-print.png?amount={amount}&addInfo={description}&accountName={accountName}").SendHttpRequestAsync(cancellationToken).ConfigureAwait(false);
				data = await vietQR.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
				data = await data.ConvertAsync(ImageFormat.Webp, cancellationToken).ConfigureAwait(false);
				stopwatch.Stop();
				if (context.IsDebugLogEnabled())
					await Global.WriteLogsAsync(this.Logger, "QRCodes", $"Generate VietQR successful - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await Global.WriteLogsAsync(this.Logger, "QRCodes", $"Error occurred while generating the VietQR: {ex.Message}", ex).ConfigureAwait(false);
				data = await ex.GenerateAsync(540, 540, cancellationToken).ConfigureAwait(false);
				cacheControl = "private, no-store, no-cache";
			}
			await context.WriteAsync(data, "image/webp", null, null, 0, cacheControl, TimeSpan.Zero, new Dictionary<string, string> { ["X-Node"] = Global.NodeID }, context.GetCorrelationID(), cancellationToken).ConfigureAwait(false);
		}
	}
}