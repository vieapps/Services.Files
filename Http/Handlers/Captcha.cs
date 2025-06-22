#region Related component
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class CaptchaHandler : Services.FileHandler
	{
		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
		{
			if (context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD"))
			{
				var pathSegments = context.GetRequestPathSegments().Skip(1).ToArray();
				var code = pathSegments[0].Url64Decode();

				var isSmall = true;
				if (pathSegments.Length > 1)
					try
					{
						isSmall = !pathSegments[1].Url64Decode().IsStartsWith("big");
					}
					catch { }

				string decryptionKey = null;
				if (pathSegments.Length > 2 && !pathSegments[2].IsEndsWith(".webp"))
					try
					{
						decryptionKey = pathSegments[2].ToBase64(false, true).Decrypt(UtilityService.GetAppSetting("Keys:Captcha:Extra", CryptoService.DEFAULT_PASS_PHRASE)).ToArray(":").First();
					}
					catch { }

				try
				{
					code = code.Decrypt(decryptionKey ?? CaptchaService.EncryptionKey, true).ToArray('-').Last();
					var tempCode = "";
					var space = " ";
					var spaceP = "";
					if (code.Length <= 5 && !isSmall)
						spaceP = "  ";
					else if (isSmall)
						space = "";
					for (int index = 0; index < code.Length; index++)
						tempCode += spaceP + code[index].ToString() + space;
					code = tempCode;
				}
				catch
				{
					code = "I-n-valid";
				}

				using var inputStream = code.Generate(isSmall);
				using var outputStream = await inputStream.ConvertAsync(System.Drawing.Imaging.ImageFormat.Webp, cancellationToken).ConfigureAwait(false);
				await context.WriteAsync(outputStream, "image/webp", null, null, 0, "private, no-store, no-cache", TimeSpan.Zero, new System.Collections.Generic.Dictionary<string, string> { ["X-Node"] = Global.NodeID }, context.GetCorrelationID(), cancellationToken).ConfigureAwait(false);
			}
			else
				throw new MethodNotAllowedException(context.Request.Method);
		}
	}
}