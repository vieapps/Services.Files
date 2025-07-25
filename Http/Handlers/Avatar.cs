#region Related component
using Microsoft.AspNetCore.Http;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

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
			var isDebugLogEnabled = context.IsDebugLogEnabled();

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
					if (isDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Avatars", $"Error occurred while parsing filename [{fileName}] => {ex.Message}", ex).ConfigureAwait(false);
					fileName = null;
				}

			FileInfo fileInfo;
			try
			{
				fileInfo = new FileInfo(Path.Combine(Handler.UserAvatarFilesPath, fileName ?? "@default.png"));
				if (!fileInfo.Exists)
				{
					if (isDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Avatars", $"The file is not existed ({fileInfo.FullName}, then use the default avatar)").ConfigureAwait(false);
					fileInfo = new FileInfo(Handler.DefaultUserAvatarFilePath);
				}
			}
			catch (Exception ex)
			{
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Avatars", $"Error occurred while combine file-path ({Handler.UserAvatarFilesPath} - {fileName}) => {ex.Message}", ex).ConfigureAwait(false);
				fileInfo = new FileInfo(Handler.DefaultUserAvatarFilePath);
			}

			// track session
			context.SendSessionState();

			// check request headers to reduce traffict
			var eTag = $"vieapps#{(fileInfo.Name + "-" + fileInfo.LastWriteTime.ToIsoString()).ToLower().GenerateUUID()}";
			if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")) && context.GetHeaderParameter("If-Modified-Since") != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", correlationID);
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Avatars", $"Response to request with status code 304 to reduce traffic ({requestUri})").ConfigureAwait(false);
				return;
			}

			// response
			await context.WriteAsync(fileInfo, fileInfo.GetMimeType(), null, eTag, fileInfo.LastWriteTime.ToUnixTimestamp(), "public", TimeSpan.FromDays(366), new Dictionary<string, string> { ["X-Node"] = Global.NodeID }, correlationID, cancellationToken).ConfigureAwait(false);
			if (isDebugLogEnabled)
				await context.WriteLogsAsync(this.Logger, "Avatars", $"Successfully show an avatar image [{requestUri} => {fileInfo.FullName} - {fileInfo.Length:###,##0} bytes]").ConfigureAwait(false);
		}

		async Task ReceiveAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			if (!context.User.Identity.IsAuthenticated)
				throw new AccessDeniedException();

			context.SendSessionState();

			var content = Array.Empty<byte>();
			var asBase64 = context.GetParameter("x-as-base64") != null;
			var isDebugLogEnabled = Global.IsDebugLogEnabled || context.GetParameter("x-logs") != null;

			// limit size
			if (!Int32.TryParse(UtilityService.GetAppSetting("Limits:Avatar"), out var limitSize))
				limitSize = 1024;

			// read content from base64 string
			if (asBase64)
			{
				content = (await context.ReadTextAsync(cancellationToken).ConfigureAwait(false)).ToJson().Get<string>("Data").ToArray().Last().Base64ToBytes();
				content = await content.ConvertAsync(ImageFormat.Webp, cancellationToken).ConfigureAwait(false);
				if (content.Length > limitSize * 1024)
				{
					await context.WriteLogsAsync(this.Logger, "Uploads", $"Limit size exceeded (base64) - Max allowed size: ${limitSize * 1024:###,###,###,##0} bytes - Actual size: ${content.Length:###,###,###,##0} bytes").ConfigureAwait(false);
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

				if (file.Length > limitSize * 1024)
				{
					await context.WriteLogsAsync(this.Logger, "Uploads", $"Limit size exceeded (file) - Max allowed size: ${limitSize * 1024:###,###,###,##0} bytes - Actual size: ${file.Length:###,###,###,##0} bytes").ConfigureAwait(false);
					context.SetResponseHeaders((int)HttpStatusCode.RequestEntityTooLarge, null, 0, "private", null);
					return;
				}

				using var stream = file.OpenReadStream();
				content = new byte[file.Length];
				await stream.ReadAsync(content, cancellationToken).ConfigureAwait(false);
				content = await content.ConvertAsync(ImageFormat.Webp, cancellationToken).ConfigureAwait(false);
			}

			// write into file of temporary directory
			var filename = context.User.Identity.Name + ".webp";
			await content.SaveAsBinaryAsync(Path.Combine(Handler.TempFilesPath, filename), cancellationToken).ConfigureAwait(false);

			// move file from temporary directory to official directory
			File.Move(Path.Combine(Handler.TempFilesPath, filename), Path.Combine(Handler.UserAvatarFilesPath, filename), true);

			// sync
			new CommunicateMessage(Global.ServiceName)
			{
				Type = "Avatar#Sync",
				ExcludedNodeID = Global.NodeID,
				Data = new JObject
				{
					{ "Node", Global.NodeID },
					{ "ServiceName", "Users" },
					{ "SystemID", null },
					{ "Filename", filename },
					{ "IsTemporary", false },
					{ "IsAvatar", true },
					{ "CorrelationID", context.GetCorrelationID() }
				}
			}.Send();
			if (isDebugLogEnabled)
				await context.WriteLogsAsync(this.Logger, "Synchronizers", $"Send an inter-communicate message to sync an avatar image ({filename})").ConfigureAwait(false);

			// response
			var profile = await context.CallServiceAsync(new RequestInfo(context.GetSession(), "Users", "Profile", "GET"), cancellationToken, this.Logger, "Avatars").ConfigureAwait(false);
			await context.WriteAsync(new JObject
			{
				{ "URI", $"{context.GetHostUrl()}/avatars/{$"{UtilityService.GetRandomNumber()}|{filename}".Encrypt(Global.EncryptionKey).ToBase64Url(true)}/{DateTime.Now:HHmmssfff}/{profile.Get("Name", "vieapps-ngx").GetANSIUri()}.webp" }
			}, cancellationToken).ConfigureAwait(false);
			if (isDebugLogEnabled)
				await context.WriteLogsAsync(this.Logger, "Uploads", $"New avatar of {profile.Get<string>("Name")} ({profile.Get<string>("ID")}) has been uploaded ({content.Length:###,##0} bytes)").ConfigureAwait(false);
		}
	}
}