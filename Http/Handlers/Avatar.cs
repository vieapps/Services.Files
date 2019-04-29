﻿#region Related component
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

		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
			=> context.Request.Method.IsEquals("GET")
				? this.ShowAvatarAsync(context, cancellationToken)
				: context.Request.Method.IsEquals("POST")
					? this.UpdateAvatarAsync(context, cancellationToken)
					: Task.FromException(new MethodNotAllowedException(context.Request.Method));

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
							await context.WriteLogsAsync(this.Logger, "Http.Avatars", $"The file is not existed ({fileInfo.FullName}, then use default avatar)").ConfigureAwait(false);
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

				var contentType = fileInfo.Name.IsEndsWith(".png")
					? "png"
					: fileInfo.Name.IsEndsWith(".jpg") || fileInfo.Name.IsEndsWith(".jpeg")
						? "jpeg"
						: fileInfo.Name.IsEndsWith(".gif")
							? "gif"
							: "bmp";

				await Task.WhenAll(
					context.WriteAsync(fileInfo, $"image/{contentType}; charset=utf-8", null, eTag, cancellationToken),
					!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync(this.Logger, "Http.Avatars", $"Response to request successful ({fileInfo.FullName} - {fileInfo.Length:#,##0} bytes)")
				).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await context.WriteLogsAsync(this.Logger, "Http.Avatars", $"Error occurred while processing [{requestUri}]", ex).ConfigureAwait(false);
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
			}
		}

		async Task UpdateAvatarAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			if (!context.User.Identity.IsAuthenticated)
				throw new AccessDeniedException();

			var fileSize = 0;
			var fileExtension = ".png";
			var filePath = Path.Combine(Handler.UserAvatarFilesPath, context.User.Identity.Name);
			var content = new byte[0];
			var asBase64 = context.GetHeaderParameter("x-as-base64") != null;

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
			}

			// read content from uploaded file of multipart/form-data
			else
			{
				// prepare
				var file = context.Request.Form.Files.Count > 0 ? context.Request.Form.Files[0] : null;
				if (file == null || file.Length < 1)
					throw new InvalidRequestException("No uploaded file is found");

				if (!file.ContentType.IsStartsWith("image/"))
					throw new InvalidRequestException("No uploaded image file is found");

				fileSize = (int)file.Length;
				fileExtension = Path.GetExtension(file.FileName);

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
			await UtilityService.WriteBinaryFileAsync(filePath += fileExtension, content, cancellationToken).ConfigureAwait(false);

			// response
			var info = await context.CallServiceAsync(new RequestInfo(context.GetSession(), "Users", "Profile", "GET"), cancellationToken, this.Logger, "Http.Avatars").ConfigureAwait(false);
			var response = new JObject
			{
				{ "URI", $"{context.GetHostUrl()}/avatars/{$"{UtilityService.NewUUID.Left(3)}|{context.User.Identity.Name}{fileExtension}".Encrypt(Global.EncryptionKey).ToBase64Url(true)}/{info.Get<string>("Name").GetANSIUri()}{fileExtension}" }
			};
			await Task.WhenAll(
				context.WriteAsync(response, cancellationToken),
				!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync(this.Logger, "Http.Avatars", $"New avatar of {info.Get<string>("Name")} ({info.Get<string>("ID")}) has been uploaded ({filePath} - {fileSize:#,##0} bytes)")
			).ConfigureAwait(false);
		}
	}
}