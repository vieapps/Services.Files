#region Related component
#if NET5_0
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ImageProcessorCore;
using ImageProcessorCore.Plugins.WebP.Formats;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endif
#endregion

namespace net.vieapps.Services.Files
{
#if NET5_0
	public class WebpImageHandler : Services.FileHandler
	{
		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
			=> context.Request.Method.IsEquals("GET")
				? this.FlushAsync(context, cancellationToken)
				: Task.FromException(new MethodNotAllowedException(context.Request.Method));

		async Task FlushAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var requestUri = context.GetRequestUri();
			var pathSegments = requestUri.GetRequestPathSegments();

			var attachment = new AttachmentInfo
			{
				ID = pathSegments.Length > 2 && pathSegments[2].IsValidUUID() ? pathSegments[2] : "",
				ServiceName = pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				SystemID = pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				ContentType = "image/webp",
				Filename = pathSegments.Length > 3 && pathSegments[2].IsValidUUID() ? pathSegments[3].UrlDecode() : "",
				IsThumbnail = false
			};

			if (string.IsNullOrWhiteSpace(attachment.ID) || string.IsNullOrWhiteSpace(attachment.Filename))
				throw new InvalidRequestException();

			// check "If-Modified-Since" request to reduce traffict
			var eTag = "file#" + attachment.ID.ToLower();
			var noneMatch = context.GetHeaderParameter("If-None-Match");
			var modifiedSince = context.GetHeaderParameter("If-Modified-Since") ?? context.GetHeaderParameter("If-Unmodified-Since");
			if (eTag.IsEquals(noneMatch) && modifiedSince != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, modifiedSince.FromHttpDateTime().ToUnixTimestamp(), "public", context.GetCorrelationID());
				await context.FlushAsync(cancellationToken).ConfigureAwait(false);
				if (Global.IsDebugLogEnabled)
					context.WriteLogs(this.Logger, "Http.Downloads", $"Response to request with status code 304 to reduce traffic ({requestUri})");
				return;
			}

			// get info & check permissions
			attachment = await context.GetAsync(attachment.ID, cancellationToken).ConfigureAwait(false);
			if (!await context.CanDownloadAsync(attachment, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// check exist
			var cacheKey = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Images", "true")) && Global.Cache != null
				? $"WebpImage#{attachment.Filename.ToLower().GenerateUUID()}"
				: null;
			var hasCached = cacheKey != null && await Global.Cache.ExistsAsync(cacheKey, cancellationToken).ConfigureAwait(false);

			FileInfo fileInfo = null;
			if (!hasCached)
			{
				attachment.Filename = attachment.Filename.IsEndsWith(".webp") && (attachment.Filename.IsContains(".png") || attachment.Filename.IsContains(".jpg"))
					? attachment.Filename.Left(attachment.Filename.Length - 5)
					: attachment.Filename;
				fileInfo = new FileInfo(attachment.GetFilePath());
				if (!fileInfo.Exists)
				{
					if (Global.IsDebugLogEnabled)
						context.WriteLogs(this.Logger, "Http.Downloads", $"Not found: {requestUri} => {fileInfo.FullName}");
					context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());
					return;
				}
			}

			// flush the file to output stream, update counter & logs
			if (hasCached)
			{
				var lastModified = await Global.Cache.GetAsync<long>($"{cacheKey}:time", cancellationToken).ConfigureAwait(false);
				var bytes = await Global.Cache.GetAsync<byte[]>(cacheKey, cancellationToken).ConfigureAwait(false);
				using var stream = UtilityService.CreateMemoryStream(bytes);
				await context.WriteAsync(stream, "image/webp", attachment.IsReadable() ? null : attachment.Filename, eTag, lastModified, "public", TimeSpan.FromDays(366), null, context.GetCorrelationID(), cancellationToken).ConfigureAwait(false);
				if (Global.IsDebugLogEnabled)
					context.WriteLogs(this.Logger, "Http.Downloads", $"Successfully flush a cached WebP Image file ({requestUri})");
			}
			else
			{
				using var fileStream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true);
				using var imageFactory = new ImageFactory();
				using var imageStream = UtilityService.CreateMemoryStream();
				imageFactory.Load(fileStream);
				imageFactory.Quality = 100;
				imageFactory.Save(imageStream);
				await context.WriteAsync(imageStream, "image/webp", attachment.IsReadable() ? null : $"{attachment.Filename}.webp", eTag, fileInfo.LastWriteTime.ToUnixTimestamp(), "public", TimeSpan.FromDays(366), cancellationToken).ConfigureAwait(false);
				if (cacheKey != null)
					await Task.WhenAll
					(
						Global.Cache.SetAsFragmentsAsync(cacheKey, imageStream.ToBytes(), cancellationToken),
						Global.Cache.SetAsync($"{cacheKey}:time", fileInfo.LastWriteTime.ToUnixTimestamp(), cancellationToken),
						Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Update a WebP Image file into cache successful ({requestUri})") : Task.CompletedTask
					).ConfigureAwait(false);
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Successfully flush a WebP Image file [{requestUri} => {fileInfo.FullName}]").ConfigureAwait(false);
			}

			await context.UpdateAsync(attachment, attachment.IsReadable() ? "Direct" : "Download", cancellationToken).ConfigureAwait(false);
		}
	}
#endif
}