#region Related component
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

		#region Configuration
		static string _FilesPath = null, _DefaultAvatarFilename = null;

		static string FilesPath
		{
			get
			{
				if (string.IsNullOrWhiteSpace(AvatarHandler._FilesPath))
				{
					AvatarHandler._FilesPath = UtilityService.GetAppSetting("UserAvatarsPath");
					if (string.IsNullOrWhiteSpace(AvatarHandler._FilesPath))
						AvatarHandler._FilesPath = HttpRuntime.AppDomainAppPath + @"\data-files\user-avatars";
					if (!AvatarHandler._FilesPath.EndsWith(@"\"))
						AvatarHandler._FilesPath += @"\";
				}
				return AvatarHandler._FilesPath;
			}
		}

		static string DefaultAvatarFilename
		{
			get
			{
				if (string.IsNullOrWhiteSpace(AvatarHandler._DefaultAvatarFilename))
				{
					AvatarHandler._DefaultAvatarFilename = UtilityService.GetAppSetting("DefaultUserAvatarFilename");
					if (string.IsNullOrWhiteSpace(AvatarHandler._DefaultAvatarFilename))
						AvatarHandler._DefaultAvatarFilename = "@default.png";
				}
				return AvatarHandler._DefaultAvatarFilename;
			}
		}
		#endregion

		#region Show avatar image
		async Task ShowAvatarAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			// prepare
			var request = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length);
			if (request.StartsWith("/"))
				request = request.Right(request.Length - 1);
			if (request.IndexOf("?") > 0)
				request = request.Left(request.IndexOf("?"));
			var info = request.ToArray('/', true);

			var filePath = AvatarHandler.FilesPath + "nothing";
			var eTag = "";
			try
			{
				info = info[1].Url64Decode().ToArray('|');
				filePath = AvatarHandler.FilesPath + info[1] + ".png";
				eTag = "Avatar#" + info[1].ToLower();
			}
			catch { }

			var fileInfo = new FileInfo(filePath);
			if (!fileInfo.Exists)
			{
				filePath = AvatarHandler.FilesPath + AvatarHandler.DefaultAvatarFilename;
				fileInfo = new FileInfo(filePath);
				eTag = "Avatar#Default";
			}

			// check request headers to reduce traffict
			if (!string.IsNullOrWhiteSpace(eTag) && context.Request.Headers["If-Modified-Since"] != null)
			{
				var modifiedSince = context.Request.Headers["If-Modified-Since"].FromHttpDateTime();
				var diffSeconds = 1.0d;
				if (!modifiedSince.Equals(DateTimeService.CheckingDateTime))
					diffSeconds = (fileInfo.LastWriteTime - modifiedSince).TotalSeconds;
				var isNotModified = diffSeconds < 1.0d;

				var isMatched = true;
				if (context.Request.Headers["If-None-Match"] != null)
					isMatched = context.Request.Headers["If-None-Match"].Equals(eTag);

				if (isNotModified && isMatched)
				{
					context.Response.Cache.SetCacheability(HttpCacheability.Public);
					context.Response.StatusCode = (int)HttpStatusCode.NotModified;
					context.Response.StatusDescription = "Not Modified";
					context.Response.AppendHeader("ETag", "\"" + eTag + "\"");
					return;
				}
			}

			// update cache policy of client
			if (!string.IsNullOrWhiteSpace(eTag))
			{
				context.Response.Cache.SetCacheability(HttpCacheability.Public);
				context.Response.Cache.SetExpires(DateTime.Now.AddDays(7));
				context.Response.Cache.SetSlidingExpiration(true);
				context.Response.Cache.SetOmitVaryStar(true);
				context.Response.Cache.SetValidUntilExpires(true);
				context.Response.Cache.SetLastModified(fileInfo.LastWriteTime);
				context.Response.Cache.SetETag(eTag);
			}

			// flush the file to output
			try
			{
				await context.WriteFileToOutputAsync(fileInfo, "image/png", eTag);
			}
			catch (FileNotFoundException ex)
			{
				Global.ShowError(context, 404, "Not Found [" + context.Request.RawUrl + "]", "FileNotFoundException", ex.StackTrace, ex.InnerException);
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