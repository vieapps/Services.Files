#region Related component
using System;
using System.IO;
using System.Net;
using System.Web;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class AvatarHandler : AbstractHttpHandler
	{
		protected override async Task SendInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default(CancellationToken))
		{
			await Global.SendInterCommunicateMessageAsync(message).ConfigureAwait(false);
		}

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (context.Request.HttpMethod.IsEquals("GET"))
				await this.ShowAvatarAsync(context, cancellationToken).ConfigureAwait(false);
			else if (context.Request.HttpMethod.IsEquals("POST"))
				await this.UpdateAvatarAsync(context, cancellationToken).ConfigureAwait(false);
			else
				throw new MethodNotAllowedException(context.Request.HttpMethod);
		}

		#region Show avatar image
		async Task ShowAvatarAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			FileInfo fileInfo = null;
			try
			{
				var info = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length);
				while (info.StartsWith("/"))
					info = info.Right(info.Length - 1);
				if (info.IndexOf("?") > 0)
					info = info.Left(info.IndexOf("?"));

				fileInfo = new FileInfo(Path.Combine(Global.UserAvatarFilesPath, info.ToArray('/', true)[1].Replace(".png", "").Url64Decode().ToArray('|').Last() + ".png"));
			}
			catch { }
			if (fileInfo == null || !fileInfo.Exists)
				fileInfo = new FileInfo(Global.DefaultUserAvatarFilePath);

			var eTag = "Avatar#" + (fileInfo.Name + "-" + fileInfo.LastWriteTime.ToIsoString()).ToLower().GetMD5();

			// check request headers to reduce traffict
			if (context.Request.Headers["If-Modified-Since"] != null && eTag.Equals(context.Request.Headers["If-None-Match"]))
			{
				context.Response.Cache.SetCacheability(HttpCacheability.Public);
				context.Response.StatusCode = (int)HttpStatusCode.NotModified;
				context.Response.StatusDescription = "Not Modified";
				context.Response.Headers.Add("ETag", "\"" + eTag + "\"");
				return;
			}

			// show
			try
			{
				// update cache policy of client
				context.Response.Cache.SetCacheability(HttpCacheability.Public);
				context.Response.Cache.SetExpires(DateTime.Now.AddDays(7));
				context.Response.Cache.SetSlidingExpiration(true);
				context.Response.Cache.SetOmitVaryStar(true);
				context.Response.Cache.SetValidUntilExpires(true);
				context.Response.Cache.SetLastModified(fileInfo.LastWriteTime);
				context.Response.Cache.SetETag(eTag);

				// flush the file to output
				await context.WriteFileToOutputAsync(fileInfo, "image/png", eTag, null, cancellationToken).ConfigureAwait(false);
			}
			catch (FileNotFoundException ex)
			{
				context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", ex.StackTrace);
			}
			catch (Exception)
			{
				throw;
			}
		}
		#endregion

		#region Update avatar image (receive upload image from the client)
		async Task UpdateAvatarAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// check
			if (context.User == null || !(context.User is UserPrincipal) || context.User.Identity.Name.Equals(""))
				throw new AccessDeniedException();

			// delete old file
			var filePath = Path.Combine(Global.UserAvatarFilesPath, context.User.Identity.Name + ".png");
			try
			{
				if (File.Exists(filePath))
					File.Delete(filePath);
			}
			catch { }

			// base64 image
			if (context.Request.Headers["x-as-base64"] != null)
			{
				// parse & write file
				var data = "";
				using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
				{
					var body = await reader.ReadToEndAsync().ConfigureAwait(false);
					data = body.ToExpandoObject().Get<string>("Data").ToArray().Last();
				}

				// write to file
				await UtilityService.ExecuteTask(() => File.WriteAllBytes(filePath, Convert.FromBase64String(data)), cancellationToken).ConfigureAwait(false);
			}

			// file
			else
				await UtilityService.ExecuteTask(() => context.Request.Files[0].SaveAs(filePath), cancellationToken).ConfigureAwait(false);

			// response
			await context.Response.Output.WriteAsync((new JObject()
			{
				{ "Uri", context.Request.Url.Scheme + "://" + context.Request.Url.Host + "/avatars/" + (DateTime.Now.ToIsoString() + "|" + context.User.Identity.Name).Url64Encode() + ".png" }
			}).ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
		}
		#endregion

	}
}