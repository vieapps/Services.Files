#region Related component
using System;
using System.IO;
using System.Net;
using System.Linq;
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
			var isDebugLogEnabled = Global.IsDebugLogEnabled || context.TryGetParameter("x-logs", out var _);
			var processCache = !context.TryGetParameter("x-no-cache", out var _) && !context.TryGetParameter("x-force-cache", out var _);

			var attachment = new AttachmentInfo
			{
				ID = pathSegments.Length > 2 && pathSegments[2].Length > 31 && pathSegments[2].Left(32).IsValidUUID() ? pathSegments[2].Left(32).ToLower() : "",
				ServiceName = pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				SystemID = pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1].ToLower() : "",
				Filename = pathSegments.Length > 3 ? pathSegments[3].UrlDecode() : pathSegments.Length > 2 && pathSegments[2].Length > 33 && pathSegments[2].Left(32).IsValidUUID() ? pathSegments[2].Right(pathSegments[2].Length - 33) : "",
				IsThumbnail = false
			};
			attachment.Filename = attachment.IsWebP() && !attachment.Filename.IsEndsWith(".webp")
				? attachment.Filename.Left(attachment.Filename.Length - new FileInfo(attachment.GetFilePath()).Extension.Length)
				: attachment.Filename;

			// validate the request
			if (string.IsNullOrWhiteSpace(attachment.ID) || string.IsNullOrWhiteSpace(attachment.Filename))
			{
				await context.WriteLogsAsync(this.Logger, "Downloads", $"Invalid request segments\r\nSegments:\r\n- {pathSegments.Select((segment, index) => $"{index}: {segment}").Join("\r\n- ")}").ConfigureAwait(false);
				throw new InvalidRequestException();
			}

			// prepare entity tag and headers
			var eTag = attachment.GetCacheKey(attachment.IsWebP() ? "file" : "webp");
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

			// check existed
			FileInfo fileInfo = null;
			var hasCached = Handler.IsCacheImages && processCache && await Global.Cache.ExistsAsync(eTag, cancellationToken).ConfigureAwait(false);
			byte[] data;
			long lastModified;

			if (!hasCached)
			{
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
				data = await Global.Cache.GetAsync<byte[]>(eTag, cancellationToken).ConfigureAwait(false);
				lastModified = await Global.Cache.GetAsync<long>($"{eTag}:time", cancellationToken).ConfigureAwait(false);
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Downloads", $"Cached of a WebP image was found [{eTag} => {requestURI}]").ConfigureAwait(false);
			}
			else
			{
				var stepwatch = Stopwatch.StartNew();
				data = await fileInfo.ReadAsBinaryAsync(cancellationToken).ConfigureAwait(false);
				if (!attachment.IsWebP())
				{
					var length = data.Length;
					data = await data.ConvertAsync(ImageFormat.Webp, cancellationToken).ConfigureAwait(false);
					stepwatch.Stop();
					if (isDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Downloads", $"Prepare a WebP image successful - Execution times: {stepwatch.GetElapsedTimes()}\r\n- Info: {requestURI} => {fileInfo.Name}\r\n- Original length: {length:###,###,###,##0} bytes\r\n- WebP length: {data.Length:###,###,###,##0} bytes").ConfigureAwait(false);
				}
				lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
				if (Handler.IsCacheImages)
					attachment.PrepareCacheAsync(true, attachment.IsWebP() ? "file" : "webp", data, lastModified).Run();
			}

			// flush the file to output stream
			await context.WriteAsync(data, "image/webp", null, eTag, lastModified, "public", TimeSpan.FromDays(366), headers, correlationID, cancellationToken).ConfigureAwait(false);

			// update counter & logs
			stopwatch.Stop();
			await Task.WhenAll
			(
				context.UpdateAsync(attachment, "Direct", cancellationToken),
				isDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Downloads", $"Successfully flush a WebP image file ({requestURI}) - Execution times: {stopwatch.GetElapsedTimes()}") : Task.CompletedTask
			).ConfigureAwait(false);
		}
	}
}