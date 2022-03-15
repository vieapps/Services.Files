#region Related component
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using ImageProcessorCore;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class WebpImageHandler : Services.FileHandler
	{
		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
			=> context.Request.Method.IsEquals("GET")
				? this.ShowAsync(context, cancellationToken)
				: Task.FromException(new MethodNotAllowedException(context.Request.Method));

		async Task ShowAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var correlationID = context.GetCorrelationID();
			var requestUri = context.GetRequestUri();
			var pathSegments = requestUri.GetRequestPathSegments();

			var attachment = new AttachmentInfo
			{
				ID = pathSegments.Length > 2 && pathSegments[2].IsValidUUID() ? pathSegments[2].ToLower() : "",
				ServiceName = pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				SystemID = pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1].ToLower() : "",
				ContentType = "image/webp",
				Filename = pathSegments.Length > 3 && pathSegments[2].IsValidUUID() ? pathSegments[3].UrlDecode() : "",
				IsThumbnail = false
			};

			if (string.IsNullOrWhiteSpace(attachment.ID) || string.IsNullOrWhiteSpace(attachment.Filename))
				throw new InvalidRequestException();

			// check "If-Modified-Since" request to reduce traffict
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Cache"] = "None" };
			var eTag = $"webp#{attachment.ID.ToLower()}";
			var noneMatch = context.GetHeaderParameter("If-None-Match");
			var modifiedSince = context.GetHeaderParameter("If-Modified-Since") ?? context.GetHeaderParameter("If-Unmodified-Since");
			if (eTag.IsEquals(noneMatch) && modifiedSince != null)
			{
				headers["X-Cache"] = "SVC-304";
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, modifiedSince.FromHttpDateTime().ToUnixTimestamp(), "public", correlationID, headers);
				await context.FlushAsync(cancellationToken).ConfigureAwait(false);
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Response to request with status code 304 to reduce traffic ({requestUri})").ConfigureAwait(false);
				return;
			}

			// get info & check permissions
			attachment = await context.GetAsync(attachment.ID, cancellationToken).ConfigureAwait(false);
			if (!await context.CanDownloadAsync(attachment, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// check exist
			var cacheKey = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Images", "true")) && Global.Cache != null
				? eTag
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
						await context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Not found: {requestUri} => {fileInfo.FullName}").ConfigureAwait(false);
					context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", correlationID);
					return;
				}
			}

			// flush the file to output stream, update counter & logs
			if (hasCached)
			{
				headers["X-Cache"] = "SVC-200";
				var lastModified = await Global.Cache.GetAsync<long>($"{cacheKey}:time", cancellationToken).ConfigureAwait(false);
				using var stream = (await Global.Cache.GetAsync<byte[]>(cacheKey, cancellationToken).ConfigureAwait(false)).ToMemoryStream();
				await Task.WhenAll
				(
					context.WriteAsync(stream, "image/webp", attachment.IsReadable() ? null : attachment.Filename, eTag, lastModified, "public", TimeSpan.FromDays(366), headers, correlationID, cancellationToken),
					Global.IsDebugLogEnabled ? context .WriteLogsAsync(this.Logger, "Http.Downloads", $"Successfully flush a cache of WebP Image file ({requestUri})") : Task.CompletedTask
				).ConfigureAwait(false);
			}
			else
			{
				var lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
				using var fileStream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true);
				using var imageFactory = new ImageFactory();
				using var imageStream = UtilityService.CreateMemoryStream();
				imageFactory.Load(fileStream);
				imageFactory.Quality = 100;
				imageFactory.Save(imageStream);
				await context.WriteAsync(imageStream, "image/webp", attachment.IsReadable() ? null : $"{attachment.Filename}.webp", eTag, lastModified, "public", TimeSpan.FromDays(366), headers, correlationID, cancellationToken).ConfigureAwait(false);
				await Task.WhenAll
				(
					cacheKey != null
						? Task.WhenAll
						(
							Global.Cache.SetAsFragmentsAsync(cacheKey, imageStream.ToBytes(), 0, cancellationToken),
							Global.Cache.SetAsync($"{cacheKey}:time", lastModified, 0, cancellationToken),
							Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Update a WebP Image file into cache successful ({requestUri})") : Task.CompletedTask
						) : Task.CompletedTask,
					Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Successfully flush a WebP Image file [{requestUri} => {fileInfo.FullName}]") : Task.CompletedTask
				).ConfigureAwait(false);
			}

			await context.UpdateAsync(attachment, attachment.IsReadable() ? "Direct" : "Download", cancellationToken).ConfigureAwait(false);
		}
	}
}