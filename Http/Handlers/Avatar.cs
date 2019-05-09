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
	public class AvatarHandler : Services.FileHandler
	{
		public override ILogger Logger { get; } = Components.Utility.Logger.CreateLogger<AvatarHandler>();

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted))
				try
				{
					if (context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD"))
						await this.ShowAsync(context, cts.Token).ConfigureAwait(false);
					else if (context.Request.Method.IsEquals("POST"))
						await this.ReceiveAsync(context, cts.Token).ConfigureAwait(false);
					else
						throw new MethodNotAllowedException(context.Request.Method);
				}
				catch (OperationCanceledException) { }
				catch (Exception ex)
				{
					await context.WriteLogsAsync(this.Logger, $"Http.{(context.Request.Method.IsEquals("POST") ? "Uploads" : "Avatars")}", $"Error occurred while processing with an avatar image ({context.GetReferUri()})", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
					context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
				}
		}

		async Task ShowAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var requestUri = context.GetRequestUri();
			var pathSegments = requestUri.GetRequestPathSegments();
			var fileName = pathSegments.Length > 1 ? pathSegments[1] : null;

			if (fileName != null)
				try
				{
					if (fileName.IndexOf(".") > 0)
					{
						fileName = fileName.Left(fileName.IndexOf("."));
						fileName = fileName.Url64Decode().ToArray('|').Last();
					}
					else
						fileName = fileName.ToBase64(false, true).Decrypt(Global.EncryptionKey).ToArray('|').Last();
				}
				catch (Exception ex)
				{
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Http.Avatars", $"Error occurred while parsing filename [{fileName}] => {ex.Message}", ex).ConfigureAwait(false);
					fileName = null;
				}

			FileInfo fileInfo = null;
			try
			{
				fileInfo = new FileInfo(Path.Combine(Handler.UserAvatarFilesPath, fileName ?? "@default.png"));
				if (!fileInfo.Exists)
				{
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Http.Avatars", $"The file is not existed ({fileInfo.FullName}, then use the default avatar)").ConfigureAwait(false);
					fileInfo = new FileInfo(Handler.DefaultUserAvatarFilePath);
				}
			}
			catch (Exception ex)
			{
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Avatars", $"Error occurred while combine file-path ({Handler.UserAvatarFilesPath} - {fileName}) => {ex.Message}", ex).ConfigureAwait(false);
				fileInfo = new FileInfo(Handler.DefaultUserAvatarFilePath);
			}

			// check request headers to reduce traffict
			var eTag = "Avatar#" + (fileInfo.Name + "-" + fileInfo.LastWriteTime.ToIsoString()).ToLower().GenerateUUID();
			if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")) && context.GetHeaderParameter("If-Modified-Since") != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", context.GetCorrelationID());
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Avatars", $"Response to request with status code 304 to reduce traffic ({requestUri})").ConfigureAwait(false);
				return;
			}

			// response
			context.SetResponseHeaders((int)HttpStatusCode.OK, new Dictionary<string, string>
			{
				{ "Cache-Control", "public" },
				{ "Expires", $"{DateTime.Now.AddDays(7).ToHttpString()}" },
				{ "X-CorrelationID", context.GetCorrelationID() }
			});
			await context.WriteAsync(fileInfo, $"{fileInfo.GetMimeType()}; charset=utf-8", null, eTag, cancellationToken).ConfigureAwait(false);
			if (Global.IsDebugLogEnabled)
				await context.WriteLogsAsync(this.Logger, "Http.Avatars", $"Successfully show an avatar image [{requestUri} => {fileInfo.FullName} - {fileInfo.Length:###,###,###,###,##0} bytes]").ConfigureAwait(false);
		}

		async Task ReceiveAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			if (!context.User.Identity.IsAuthenticated)
				throw new AccessDeniedException();

			var fileSize = 0;
			var fileExtension = ".png";
			var filePath = Path.Combine(Handler.UserAvatarFilesPath, context.User.Identity.Name);
			var content = new byte[0];
			var asBase64 = context.GetParameter("x-as-base64") != null;

			// limit size - default is 1 MB
			if (!Int32.TryParse(UtilityService.GetAppSetting("Limits:Avatar"), out var limitSize))
				limitSize = 1024;

			// read content from base64 string
			if (asBase64)
			{
				var body = (await context.ReadTextAsync(cancellationToken).ConfigureAwait(false)).ToExpandoObject();
				var data = body.Get<string>("Data").ToArray();

				var extension = data.First().ToArray(";").First().ToArray(":").Last();
				fileExtension = extension.IsEndsWith("png")
					? ".png"
					: extension.IsEndsWith("bmp")
						? ".bmp"
						: extension.IsEndsWith("gif")
							? ".gif"
							: ".jpg";

				content = data.Last().Base64ToBytes();
				fileSize = content.Length;

				if (fileSize > limitSize * 1024)
				{
					context.SetResponseHeaders((int)HttpStatusCode.RequestEntityTooLarge, null, 0, "private", null);
					return;
				}
			}

			// read content from uploaded file of multipart/form-data
			else
			{
				// prepare
				var file = context.Request.Form.Files.Count > 0 ? context.Request.Form.Files[0] : null;
				if (file == null || file.Length < 1 || !file.ContentType.IsStartsWith("image/"))
					throw new InvalidRequestException("No uploaded image file is found");

				fileSize = (int)file.Length;
				fileExtension = Path.GetExtension(file.FileName);

				if (fileSize > limitSize * 1024)
				{
					context.SetResponseHeaders((int)HttpStatusCode.RequestEntityTooLarge, null, 0, "private", null);
					return;
				}

				using (var stream = file.OpenReadStream())
				{
					content = new byte[file.Length];
					await stream.ReadAsync(content, 0, fileSize).ConfigureAwait(false);
				}
			}

			// write into file on the disc
			new[] { ".png", ".jpg" }.ForEach(extension =>
			{
				if (File.Exists(filePath + extension))
					try
					{
						File.Delete(filePath + extension);
					}
					catch { }
			});
			await UtilityService.WriteBinaryFileAsync(filePath + fileExtension, content, cancellationToken).ConfigureAwait(false);

			// response
			var profile = await context.CallServiceAsync(new RequestInfo(context.GetSession(), "Users", "Profile", "GET"), cancellationToken, this.Logger, "Http.Avatars").ConfigureAwait(false);
			await context.WriteAsync(new JObject
			{
				{ "URI", $"{context.GetHostUrl()}/avatars/{$"{UtilityService.NewUUID.Left(3)}|{context.User.Identity.Name}{fileExtension}".Encrypt(Global.EncryptionKey).ToBase64Url(true)}/{profile.Get<string>("Name").GetANSIUri()}{fileExtension}" }
			}, cancellationToken).ConfigureAwait(false);
			if (Global.IsDebugLogEnabled)
				await context.WriteLogsAsync(this.Logger, "Http.Avatars", $"New avatar of {profile.Get<string>("Name")} ({profile.Get<string>("ID")}) has been uploaded ({filePath} - {fileSize:###,###,###,###,##0} bytes)").ConfigureAwait(false);
		}
	}
}