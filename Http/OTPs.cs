#region Related component
using System;
using System.IO;
using System.Web;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Collections.Generic;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class OTPsHandler : AbstractHttpHandler
	{
		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			// check
			if (!context.Request.HttpMethod.IsEquals("GET"))
				throw new InvalidRequestException();

			// prepare
			var data = context.Request.QueryString["v"] ?? context.Request.QueryString["provisioning"];
			if (string.IsNullOrWhiteSpace(data))
				throw new InvalidRequestException();

			// generate
			try
			{
				var image = CryptoService.Decrypt(data.Base64UrlToBytes(), Base.AspNet.Global.EncryptionKey.GenerateEncryptionKey(), Base.AspNet.Global.EncryptionKey.GenerateEncryptionIV());
				context.Response.Cache.SetNoStore();
				context.Response.ContentType = "image/png";
				await context.Response.OutputStream.WriteAsync(image, 0, image.Length).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await Base.AspNet.Global.WriteLogsAsync("Error occurred while generating the OTP provisioning image", ex).ConfigureAwait(false);
				throw new InvalidRequestException(ex);
			}
		}
	}
}