#region Related component
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class AvatarHandler : FileHttpHandler
	{
		ILogger Logger { get; set; }

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			this.Logger = Components.Utility.Logger.CreateLogger<AvatarHandler>();

			// show
			if (context.Request.Method.IsEquals("GET"))
				await this.ShowAvatarAsync(context, cancellationToken).ConfigureAwait(false);

			// upload new
			else if (context.Request.Method.IsEquals("POST"))
				await this.UpdateAvatarAsync(context, cancellationToken).ConfigureAwait(false);

			// unknown
			else
				throw new MethodNotAllowedException(context.Request.Method);
		}

		#region Show avatar image
		async Task ShowAvatarAsync(HttpContext context, CancellationToken cancellationToken)
		{
			try
			{
				// prepare
				var filename = context.GetRequestPathSegments()[1].Replace(".png", "");
				filename = filename.Url64Decode().ToArray('|').Last() + ".png";

				var fileInfo = new FileInfo(Path.Combine(Handler.UserAvatarFilesPath, filename));
				if (!fileInfo.Exists)
				{
					if (Global.IsDebugLogEnabled)
						context.WriteLogs(this.Logger, "Avatars", $"The file is not existed, then use default avatar ({fileInfo.FullName})");
					fileInfo = new FileInfo(Handler.DefaultUserAvatarFilePath);
				}

				// check request headers to reduce traffict
				var eTag = "Avatar#" + (fileInfo.Name + "-" + fileInfo.LastWriteTime.ToIsoString()).ToLower().GenerateUUID();
				if (eTag.IsEquals(context.Request.Headers["If-None-Match"].First()) && !context.Request.Headers["If-Modified-Since"].First().Equals(""))
				{
					context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", context.GetCorrelationID());
					if (Global.IsDebugLogEnabled)
						context.WriteLogs(this.Logger, "Avatars", $"Response to request with status code 304 to reduce traffic ({context.GetRequestUri()})");
					return;
				}

				// response
				context.SetResponseHeaders((int)HttpStatusCode.OK, new Dictionary<string, string>
				{
					{ "Cache-Control", "public" },
					{ "Expires", $"{DateTime.Now.AddDays(7).ToHttpString()}" },
					{ "X-CorrelationID", context.GetCorrelationID() }
				});
				await context.WriteAsync(fileInfo, "image/png", null, eTag, cancellationToken).ConfigureAwait(false);
				if (Global.IsDebugLogEnabled)
					context.WriteLogs(this.Logger, "Avatars", $"Response to request successful ({fileInfo.FullName} - {fileInfo.Length:#,##0} bytes)");
			}
			catch (Exception ex)
			{
				context.WriteLogs(this.Logger, "Avatars", $"Error occurred while processing [{context.GetRequestUri()}]", ex);
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
			}
		}
		#endregion

		#region Update avatar image (receive upload image from the client)
		async Task UpdateAvatarAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			if (context.User == null || !context.User.Identity.IsAuthenticated)
				throw new AccessDeniedException();

			// delete old file
			var filePath = Path.Combine(Handler.UserAvatarFilesPath, context.User.Identity.Name + ".png");
			try
			{
				if (File.Exists(filePath))
					File.Delete(filePath);
			}
			catch { }

			// base64 image
			if (!context.Request.Headers["x-as-base64"].First().Equals(""))
			{
				// parse
				var body = (await context.ReadTextAsync(cancellationToken).ConfigureAwait(false)).ToExpandoObject();
				var data = body.Get<string>("Data").ToArray();
				var content = data.Last().Base64ToBytes();

				// write to file
				await UtilityService.WriteBinaryFileAsync(filePath, content, cancellationToken).ConfigureAwait(false);
				if (Global.IsDebugLogEnabled)
					context.WriteLogs(this.Logger, "Avatars", $"New avatar (base64) has been uploaded ({filePath} - {content.Length:#,##0} bytes)");
			}

			// file
			else
				using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, TextFileReader.BufferSize, true))
				{
					await context.Request.Form.Files[0].CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
					if (Global.IsDebugLogEnabled)
						context.WriteLogs(this.Logger, "Avatars", $"New avatar (file) has been uploaded ({filePath} - {stream.Position:#,##0} bytes)");
				}

			// response
			var requestUri = context.GetRequestUri();
			var uri = requestUri.Scheme + "://" + requestUri.Host;
			if (requestUri.Port != 80 && requestUri.Port != 443)
				uri += $":{requestUri.Port}";
			uri += "/avatars/" + (DateTime.Now.ToIsoString() + "|" + context.User.Identity.Name).Url64Encode() + ".png";

			await context.WriteAsync(new JObject { { "Uri", uri } }, cancellationToken).ConfigureAwait(false);
			if (Global.IsDebugLogEnabled)
				context.WriteLogs(this.Logger, "Avatars", $"New URI: {uri}");
		}
		#endregion

	}
}