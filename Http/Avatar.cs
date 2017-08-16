﻿#region Related component
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.IO;
using System.Net;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Caching;
#endregion

namespace net.vieapps.Services.Files
{
	public class AvatarHandler : AbstractHttpHandler
	{
		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			switch (context.Request.HttpMethod)
			{
				case "GET":
					await this.ShowAvatarAsync(context, cancellationToken);
					break;

				case "POST":
					await this.UpdateAvatarAsync(context, cancellationToken);
					break;

				default:
					throw new InvalidRequestException();
			}
		}

		#region Show avatar image
		async Task ShowAvatarAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
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

				fileInfo = new FileInfo(Global.UserAvatarFilesPath + info.ToArray('/', true).RemoveAt(0).First().Url64Decode().ToArray('|').Last() + ".png");
			}
			catch { }
			if (fileInfo == null || !fileInfo.Exists)
				fileInfo = new FileInfo(Global.UserAvatarFilesPath + Global.DefaultUserAvatarFilename);

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
				await context.WriteFileToOutputAsync(fileInfo, "image/png", eTag, null, cancellationToken);
			}
			catch (FileNotFoundException ex)
			{
				Global.ShowError(context, (int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", ex.StackTrace, ex.InnerException);
			}
			catch (Exception)
			{
				throw;
			}
		}
		#endregion

		#region Update avatar image (receive upload image from client)
		async Task UpdateAvatarAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			await Task.Delay(0);
		}
		#endregion

	}
}