#region Related component
using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
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
			var data = new ArraySegment<byte>(Array.Empty<byte>());
			var stopwatch = Stopwatch.StartNew();
			try
			{
				var segments = context.GetRequestPathSegments().Skip(1).ToList();
				var isBase64Url = segments.Last().IsEquals("pay-as-we-go.jpg");
				var base64Url = isBase64Url ? segments.First().Url64Decode().ToJson() : null;
				var id = isBase64Url ? base64Url.Get<string>("id") : segments[0];
				var accountNumber = isBase64Url ? base64Url.Get<string>("accountNumber") : segments[1];
				var amount = isBase64Url ? base64Url.Get<string>("amount") : segments[2];
				var description = isBase64Url ? base64Url.Get<string>("description") : segments[3].Replace("--", " ");
				var accountName = isBase64Url ? base64Url.Get<string>("accountName") : segments[4].Replace("--", " ");
				using var vietqr = await new Uri($"https://img.vietqr.io/image/{id}-{accountNumber}-compact2.jpg?amount={amount}&addInfo={description}&accountName={accountName}").SendHttpRequestAsync(cancellationToken).ConfigureAwait(false);
				data = (await vietqr.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)).ToArraySegment();
				stopwatch.Stop();
				if (Global.IsDebugLogEnabled)
					await Global.WriteLogsAsync(this.Logger, "Http.VietQRs", $"Generate VietQR Code successful - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await Global.WriteLogsAsync(this.Logger, "Http.VietQRs", $"Error occurred while generating the VietQR Code: {ex.Message}", ex).ConfigureAwait(false);
				data = ThumbnailHandler.Generate(ex.Message, 540, 540, true);
			}
			context.SetResponseHeaders((int)HttpStatusCode.OK, "image/jpeg", null, 0, null, TimeSpan.Zero, context.GetCorrelationID());
			await context.WriteAsync(data, cancellationToken).ConfigureAwait(false);
		}
	}
}