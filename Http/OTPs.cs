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
			byte[] data = null;
			var offset = 0;
			var contentType = "image/png";
			try
			{
				// prepare
				var base64 = context.Request.QueryString["v"] ?? context.Request.QueryString["provisioning"];
				if (string.IsNullOrWhiteSpace(base64))
					throw new InvalidRequestException();

				data = CryptoService.Decrypt(base64.Base64UrlToBytes(), Base.AspNet.Global.EncryptionKey.GenerateEncryptionKey(), Base.AspNet.Global.EncryptionKey.GenerateEncryptionIV());
				var timestamp = new byte[8];
				Buffer.BlockCopy(data, 0, timestamp, 0, 8);
				if (DateTime.Now.ToUnixTimestamp() - BitConverter.ToInt64(timestamp, 0) > 60)
					throw new InvalidRequestException();
				else
					offset = 8;
			}
			catch (Exception ex)
			{
				await Base.AspNet.Global.WriteLogsAsync("Error occurred while generating the OTP provisioning image", ex).ConfigureAwait(false);
				data = ThumbnailHandler.GenerateErrorImage(ex.Message, 300, 300);
				contentType = "image/jpeg";
			}

			// generate
			context.Response.Cache.SetNoStore();
			context.Response.ContentType = contentType;
			await context.Response.OutputStream.WriteAsync(data, offset, data.Length - offset).ConfigureAwait(false);
		}
	}
}