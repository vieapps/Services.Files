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
			// prepare
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
			var requestUri = context.GetRequestUri();
			try
			{
				// prepare
				var fileName = requestUri.GetRequestPathSegments()[1];
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
						await context.WriteLogsAsync(this.Logger, "Avatars", $"Error occurred while parsing filename [{fileName}] => {ex.Message}", ex).ConfigureAwait(false);
					fileName = null;
				}

				FileInfo fileInfo = null;
				try
				{
					fileInfo = new FileInfo(Path.Combine(Handler.UserAvatarFilesPath, fileName ?? "@default.png"));
					if (!fileInfo.Exists)
					{
						if (Global.IsDebugLogEnabled)
							await context.WriteLogsAsync(this.Logger, "Avatars", $"The file is not existed ({fileInfo.FullName}, then use default avatar)").ConfigureAwait(false);
						fileInfo = new FileInfo(Handler.DefaultUserAvatarFilePath);
					}
				}
				catch (Exception ex)
				{
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Avatars", $"Error occurred while combine file-path ({Handler.UserAvatarFilesPath} - {fileName}) => {ex.Message}", ex).ConfigureAwait(false);
					fileInfo = new FileInfo(Handler.DefaultUserAvatarFilePath);
				}

				// check request headers to reduce traffict
				var eTag = "Avatar#" + (fileInfo.Name + "-" + fileInfo.LastWriteTime.ToIsoString()).ToLower().GenerateUUID();
				if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")) && context.GetHeaderParameter("If-Modified-Since") != null)
				{
					context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", context.GetCorrelationID());
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Avatars", $"Response to request with status code 304 to reduce traffic ({requestUri})").ConfigureAwait(false);
					return;
				}

				// response
				context.SetResponseHeaders((int)HttpStatusCode.OK, new Dictionary<string, string>
				{
					{ "Cache-Control", "public" },
					{ "Expires", $"{DateTime.Now.AddDays(7).ToHttpString()}" },
					{ "X-CorrelationID", context.GetCorrelationID() }
				});

				var contentType = fileInfo.Name.IsEndsWith(".png")
					? "png"
					: fileInfo.Name.IsEndsWith(".jpg") || fileInfo.Name.IsEndsWith(".jpeg")
						? "jpeg"
						: fileInfo.Name.IsEndsWith(".gif")
							? "gif"
							: "bmp";

				await Task.WhenAll(
					context.WriteAsync(fileInfo, $"image/{contentType}; charset=utf-8", null, eTag, cancellationToken),
					!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync(this.Logger, "Avatars", $"Response to request successful ({fileInfo.FullName} - {fileInfo.Length:#,##0} bytes)")
				).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await context.WriteLogsAsync(this.Logger, "Avatars", $"Error occurred while processing [{requestUri}]", ex).ConfigureAwait(false);
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
			}
		}
		#endregion

		#region Update avatar image (receive upload image from the client)
		async Task UpdateAvatarAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			if (!context.User.Identity.IsAuthenticated)
				throw new AccessDeniedException();

			var fileSize = 0;
			var fileExtension = "png";
			var filePath = Path.Combine(Handler.UserAvatarFilesPath, context.User.Identity.Name) + ".";

			// base64
			if (context.GetHeaderParameter("x-as-base64") != null)
			{
				// prepare
				var body = (await context.ReadTextAsync(cancellationToken).ConfigureAwait(false)).ToExpandoObject();
				var data = body.Get<string>("Data").ToArray();

				fileExtension = data.First().ToArray(";").First().ToArray(":").Last();
				fileExtension = fileExtension.IsEndsWith("png")
					? "png"
					: fileExtension.IsEndsWith("bmp")
						? "bmp"
						: fileExtension.IsEndsWith("gif")
							? "gif"
							: "jpg";
				filePath += fileExtension;

				var content = data.Last().Base64ToBytes();
				fileSize = content.Length;

				if (File.Exists(filePath))
					try
					{
						File.Delete(filePath);
					}
					catch { }

				// write to file
				await UtilityService.WriteBinaryFileAsync(filePath, content, cancellationToken).ConfigureAwait(false);
			}

			// file
			else
			{
				// prepare
				if (context.Request.Form.Files.Count < 1)
					throw new InvalidRequestException("No uploaded file is found");

				var file = context.Request.Form.Files[0];
				if (file == null || file.Length < 1)
					throw new InvalidRequestException("No uploaded file is found");

				fileSize = (int)file.Length;
				fileExtension = Path.GetExtension(file.FileName);
				filePath += fileExtension;

				if (File.Exists(filePath))
					try
					{
						File.Delete(filePath);
					}
					catch { }

				// write to file
				using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, TextFileReader.BufferSize, true))
				{
					await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
				}
			}

			// prepare uri
			var info = await context.CallServiceAsync(new RequestInfo(context.GetSession(), "Users", "Profile", "GET"), cancellationToken, this.Logger).ConfigureAwait(false);
			var uri = $"{context.GetHostUrl()}/avatars/{$"{UtilityService.NewUUID.Left(3)}|{context.User.Identity.Name}.{fileExtension}".Encrypt(Global.EncryptionKey).ToBase64Url(true)}/{info.Get<string>("Name").GetANSIUri()}.{fileExtension}";

			// response
			await Task.WhenAll(
				context.WriteAsync(new JObject
				{
					{ "Uri", uri }
				}, cancellationToken),
				!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync(this.Logger, "Avatars", $"New avatar of {info.Get<string>("Name")} ({info.Get<string>("ID")}) has been uploaded ({filePath} - {fileSize:#,##0} bytes)")
			).ConfigureAwait(false);
		}
		#endregion

	}
}