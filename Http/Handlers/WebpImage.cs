#region Related component
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
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
			var stopwatch = Stopwatch.StartNew();
			var correlationID = context.GetCorrelationID();
			var requestURI = context.GetRequestUri();
			var pathSegments = requestURI.GetRequestPathSegments();
			var isDebugLogEnabled = Global.IsDebugLogEnabled || context.Request.Query.ContainsKey("x-logs");
			var processCache = context.GetParameter("x-no-cache") == null && context.GetParameter("x-force-cache") == null;

			var attachment = new AttachmentInfo
			{
				ID = pathSegments.Length > 2 && pathSegments[2].IsValidUUID() ? pathSegments[2].ToLower() : "",
				ServiceName = pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				SystemID = pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1].ToLower() : "",
				ContentType = "image/webp",
				Filename = pathSegments.Length > 3 && pathSegments[2].IsValidUUID() ? pathSegments[3].UrlDecode() : "",
				IsThumbnail = false
			};

			// validate the request
			if (string.IsNullOrWhiteSpace(attachment.ID) || string.IsNullOrWhiteSpace(attachment.Filename))
				throw new InvalidRequestException();

			// prepare entity tag and headers
			var eTag = $"webp#{attachment.ID.ToLower()}";
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["X-Cache"] = "None",
				["X-Node"] = Global.NodeID
			};

			// check "If-Modified-Since" request to reduce traffict
			var noneMatch = processCache ? context.GetHeaderParameter("If-None-Match") : null;
			var modifiedSince = processCache ? context.GetHeaderParameter("If-Modified-Since") ?? context.GetHeaderParameter("If-Unmodified-Since") : null;
			if (eTag.IsEquals(noneMatch) && modifiedSince != null)
			{
				headers["X-Cache"] = $"HTTP-304/{typeof(WebpImageHandler).Assembly.GetVersion(false)}";
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, modifiedSince.FromHttpDateTime().ToUnixTimestamp(), "public", correlationID, headers);
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Downloads", $"Response to request with status code 304 to reduce traffic [{eTag} => {requestURI}]").ConfigureAwait(false);
				return;
			}

			// get info & check permissions
			attachment = await context.GetAsync(attachment.ID, cancellationToken).ConfigureAwait(false);
			if (!await context.CanDownloadAsync(attachment, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// check exist
			FileInfo fileInfo = null;
			var cacheKey = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Images", "true")) && Global.Cache != null ? eTag : null;
			var hasCached = processCache && cacheKey != null && await Global.Cache.ExistsAsync(cacheKey, cancellationToken).ConfigureAwait(false);
			byte[] data;
			long lastModified;

			if (!hasCached)
			{
				attachment.Filename = attachment.Filename.IsEndsWith(".webp") && (attachment.Filename.IsContains(".png") || attachment.Filename.IsContains(".jpg") || attachment.Filename.IsContains(".gif") || attachment.Filename.IsContains(".bmp") || attachment.Filename.IsContains(".tiff"))
					? attachment.Filename.Left(attachment.Filename.Length - 5)
					: attachment.Filename;
				fileInfo = new FileInfo(attachment.GetFilePath());
				if (!fileInfo.Exists)
				{
					if (isDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Downloads", $"Not found: {requestURI} => {fileInfo.FullName}").ConfigureAwait(false);
					context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", correlationID);
					return;
				}
			}

			// prepare
			if (hasCached)
			{
				headers["X-Cache"] = $"HTTP-200/{typeof(WebpImageHandler).Assembly.GetVersion(false)}";
				lastModified = await Global.Cache.GetAsync<long>($"{cacheKey}:time", cancellationToken).ConfigureAwait(false);
				data = await Global.Cache.GetAsync<byte[]>(cacheKey, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				var stepwatch = Stopwatch.StartNew();
				lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
				data = await fileInfo.ReadAsBinaryAsync(cancellationToken).ConfigureAwait(false);
				var length = data.Length;
				data = await data.ConvertAsync(ImageFormat.Webp, cancellationToken).ConfigureAwait(false);
				stepwatch.Stop();
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Downloads", $"Prepare a WebP image successful - Execution times: {stepwatch.GetElapsedTimes()}\r\n- Info: {requestURI} => {fileInfo.Name}\r\n- Original length: {length:###,###,###,###,###,##0} bytes\r\n- WebP length: {data.Length:###,###,###,###,###,##0} bytes").ConfigureAwait(false);
				if (cacheKey != null)
					attachment.PrepareCacheAsync(true, data, lastModified).Run();
			}

			// flush the file to output stream
			await context.WriteAsync(data, "image/webp", null, eTag, lastModified, "public", TimeSpan.FromDays(366), headers, correlationID, cancellationToken).ConfigureAwait(false);

			// update counter & logs
			stopwatch.Stop();
			await Task.WhenAll
			(
				context.UpdateAsync(attachment, "Direct", cancellationToken),
				isDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Downloads", $"Successfully flush a WebP Image file ({requestURI}) - Execution times: {stopwatch.GetElapsedTimes()}") : Task.CompletedTask
			).ConfigureAwait(false);
		}
	}
}