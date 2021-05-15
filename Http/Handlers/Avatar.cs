#region Related component
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class AvatarHandler : Services.FileHandler
	{
		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
			=> context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD")
				? this.ShowAsync(context, cancellationToken)
				: context.Request.Method.IsEquals("POST")
					? this.ReceiveAsync(context, cancellationToken)
					: Task.FromException(new MethodNotAllowedException(context.Request.Method));

		async Task ShowAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var correlationID = context.GetCorrelationID();
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

			FileInfo fileInfo;
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
			var eTag = "avatar#" + (fileInfo.Name + "-" + fileInfo.LastWriteTime.ToIsoString()).ToLower().GenerateUUID();
			if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")) && context.GetHeaderParameter("If-Modified-Since") != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", correlationID);
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Avatars", $"Response to request with status code 304 to reduce traffic ({requestUri})").ConfigureAwait(false);
				return;
			}

			// response
			context.SetResponseHeaders((int)HttpStatusCode.OK, fileInfo.GetMimeType(), eTag, fileInfo.LastWriteTime.AddMinutes(-13).ToUnixTimestamp(), "public", TimeSpan.FromDays(366), correlationID);
			await context.WriteAsync(fileInfo, cancellationToken).ConfigureAwait(false);
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
			var content = Array.Empty<byte>();
			var asBase64 = context.GetParameter("x-as-base64") != null;

			// limit size - default is 1 MB
			if (!Int32.TryParse(UtilityService.GetAppSetting("Limits:Avatar"), out var limitSize))
				limitSize = 1024;

			// read content from base64 string
			if (asBase64)
			{
				var base64Data = (await context.ReadTextAsync(cancellationToken).ConfigureAwait(false)).ToJson().Get<string>("Data").ToArray();

				var extension = base64Data.First().ToArray(";").First().ToArray(":").Last();
				fileExtension = extension.IsEndsWith("png")
					? ".png"
					: extension.IsEndsWith("bmp")
						? ".bmp"
						: extension.IsEndsWith("gif")
							? ".gif"
							: ".jpg";

				content = base64Data.Last().Base64ToBytes();
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

				using var stream = file.OpenReadStream();
				content = new byte[file.Length];
				await stream.ReadAsync(content.AsMemory(0, fileSize), cancellationToken).ConfigureAwait(false);
			}

			// write into file of temporary directory
			await UtilityService.WriteBinaryFileAsync(Path.Combine(Handler.TempFilesPath, context.User.Identity.Name + fileExtension), content, false, cancellationToken).ConfigureAwait(false);

			// move file from temporary directory to official directory
			new[] { ".png", ".jpg" }.ForEach(extension =>
			{
				var filePath = Path.Combine(Handler.UserAvatarFilesPath, context.User.Identity.Name + extension);
				if (File.Exists(filePath))
					try
					{
						File.Delete(filePath);
					}
					catch { }
			});
			File.Move(Path.Combine(Handler.TempFilesPath, context.User.Identity.Name + fileExtension), Path.Combine(Handler.UserAvatarFilesPath, context.User.Identity.Name + fileExtension));

			// response
			var profile = await context.CallServiceAsync(new RequestInfo(context.GetSession(), "Users", "Profile", "GET"), cancellationToken, this.Logger, "Http.Avatars").ConfigureAwait(false);
			await context.WriteAsync(new JObject
			{
				{ "URI", $"{context.GetHostUrl()}/avatars/{$"{UtilityService.NewUUID.Left(3)}|{context.User.Identity.Name}{fileExtension}".Encrypt(Global.EncryptionKey).ToBase64Url(true)}/{profile.Get<string>("Name").GetANSIUri()}{fileExtension}" }
			}, cancellationToken).ConfigureAwait(false);
			if (Global.IsDebugLogEnabled)
				await context.WriteLogsAsync(this.Logger, "Http.Avatars", $"New avatar of {profile.Get<string>("Name")} ({profile.Get<string>("ID")}) has been uploaded ({fileSize:###,###,###,###,##0} bytes)").ConfigureAwait(false);
		}
	}
}