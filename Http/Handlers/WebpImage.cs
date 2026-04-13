#region Related component
using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using Microsoft.AspNetCore.Http;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
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
			var stepwatch = Stopwatch.StartNew();
			var correlationID = context.GetCorrelationID();
			var requestURI = context.GetRequestUri();
			var isDebugLogEnabled = context.IsDebugLogEnabled();
			var isBypassCacheRequested = context.IsBypassCache();
			var processCache = !isBypassCacheRequested;

			var pathSegments = requestURI.GetRequestPathSegments();
			pathSegments = pathSegments.Length > 2 && pathSegments[1].IsEquals(pathSegments[2]) ? pathSegments.Take(0, 1).Concat(pathSegments.Skip(2)).ToArray() : pathSegments;
			pathSegments = pathSegments.Length > 2 && pathSegments[2].IsStartsWith("image=") ? pathSegments.Take(0, 2).Concat(pathSegments.Skip(3)).ToArray() : pathSegments;

			var identifier = pathSegments.Length > 2 && pathSegments[2].Length > 31 && pathSegments[2].Left(32).IsValidUUID() ? pathSegments[2].Left(32).ToLower() : "";
			var attachment = new AttachmentInfo
			{
				ID = identifier,
				ServiceName = pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				SystemID = pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1].ToLower() : "",
				Filename = pathSegments.Length > 3 ? pathSegments[3].UrlDecode() : pathSegments.Length > 2 && pathSegments[2].Length > 33 && pathSegments[2].Left(32).IsEquals(identifier) ? pathSegments[2].Right(pathSegments[2].Length - 33).UrlDecode() : "",
				IsThumbnail = false
			};

			FileInfo fileInfo = null;
			if (attachment.Filename.IsEndsWith(".webp"))
			{
				fileInfo = new FileInfo(attachment.Filename.Left(attachment.Filename.Length - 5));
				attachment.Filename = fileInfo.Extension != null && fileInfo.Extension != "" && fileInfo.Extension != "." ? fileInfo.Name : attachment.Filename;
			}

			// validate the request
			if (string.IsNullOrWhiteSpace(attachment.ID) || string.IsNullOrWhiteSpace(attachment.Filename))
			{
				await context.WriteLogsAsync(this.Logger, "Downloads", $"Invalid request segments\r\nOriginal:\r\n- {requestURI.GetRequestPathSegments().Select((segment, index) => $"{index}: {segment}").Join("\r\n- ")}\r\nNormalized:\r\n- {pathSegments.Select((segment, index) => $"{index}: {segment}").Join("\r\n- ")}").ConfigureAwait(false);
				throw new InvalidRequestException();
			}

			// track
			context.SendSessionState(attachment.SystemID);

			// prepare entity tag and headers
			stepwatch.Restart();
			var cacheKey = attachment.GetCacheKey(attachment.IsWebP() ? "file" : "webp");
			var eTag = $"vieapps#{(attachment.IsWebP() ? cacheKey.Replace("file#", "") : cacheKey.GenerateUUID())}";
			var isInL1Cache = Global.Cache.UseL1Cache & Global.Cache.ExistsInL1Cache(cacheKey);
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["X-Cache"] = "None",
				["X-Node"] = Global.NodeID,
				["X-Correlation-ID"] = correlationID
			};

			// check "If-Modified-Since" request to reduce traffict
			var noneMatch = processCache ? context.GetHeaderParameter("If-None-Match") : null;
			var modifiedSince = processCache ? context.GetHeaderParameter("If-Modified-Since") ?? context.GetHeaderParameter("If-Unmodified-Since") : null;
			if (eTag.IsEquals(noneMatch) && modifiedSince != null)
			{
				headers["X-Cache"] = (isInL1Cache ? "L1-" : "") + "HTTP-304";
				context.UpdateServerTiming("ngxCache", stepwatch.ElapsedMilliseconds);
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, modifiedSince.FromHttpDateTime().ToUnixTimestamp(), "public", correlationID, headers);
				if (Global.Cache.UseL1Cache)
					Global.Statistics.L1Hit304();
				else
				{
					Global.Statistics.L1Miss();
					Global.Statistics.L2Hit304();
				}
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Downloads", $"Response to request with status code 304 to reduce traffic [{eTag} => {requestURI}]").ConfigureAwait(false);
				return;
			}

			// get info & check permissions
			attachment = await context.GetAsync(attachment.ID, cancellationToken).ConfigureAwait(false);
			if (!await context.CanDownloadAsync(attachment, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// track
			context.UpdateServerTiming("ngxPrepare", stepwatch.ElapsedMilliseconds);
			if (isDebugLogEnabled)
				await context.WriteLogsAsync(this.Logger, "Downloads", $"Start flush a WebP image => {requestURI}\r\nInfo: {attachment.ToJson()}").ConfigureAwait(false);

			// check cache
			stepwatch.Restart();
			var hasCached = Handler.IsCacheImages && processCache && !attachment.IsWebP() && await Global.Cache.ExistsAsync(cacheKey, cancellationToken).ConfigureAwait(false);
			byte[] data = null;
			long lastModified = 0;
			var contentType = "image/webp";

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
				if (Global.Cache.UseL1Cache)
					Global.Statistics.L1Miss();
				Global.Statistics.L2Miss();
			}

			// prepare
			if (hasCached)
			{
				headers["X-Cache"] = (isInL1Cache ? "L1-" : "") + "HTTP-200";
				data = await Global.Cache.GetAsync<byte[]>(cacheKey, cancellationToken).ConfigureAwait(false);
				lastModified = await Global.Cache.GetAsync<long>($"{cacheKey}:time", cancellationToken).ConfigureAwait(false);
				context.UpdateServerTiming("ngxCache", stepwatch.ElapsedMilliseconds);
				if (Global.Cache.UseL1Cache && Global.Cache.ExistsInL1Cache(cacheKey))
					Global.Statistics.L1Hit200();
				else
				{
					Global.Statistics.L1Miss();
					Global.Statistics.L2Hit200();
				}
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Caches", $"Cached of a WebP image was found [{cacheKey} => {requestURI}]").ConfigureAwait(false);
			}
			else if (!attachment.IsWebP())
			{
				stepwatch.Restart();
				lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
				data = await fileInfo.ReadAsBinaryAsync(cancellationToken).ConfigureAwait(false);
				var length = data.Length;
				context.UpdateServerTiming("ngxRead", stepwatch.ElapsedMilliseconds);

				try
				{
					stepwatch.Restart();
					using var inputStream = data.ToMemoryStream();
					using var outputStream = await inputStream.ConvertAsync(ImageFormat.Webp, !attachment.Filename.IsEndsWith(".png"), !attachment.Filename.IsEndsWith(".png") && !context.ContainsKey("x-no-resize") && ServiceExtensions.ResizeBigWebpImage, cancellationToken).ConfigureAwait(false);
					data = outputStream.ToBytes();
					await context.WriteLogsAsync(this.Logger, "Downloads", $"Convert to WebP image successful - Execution times: {stepwatch.GetElapsedTimes()}\r\n- Info: {requestURI} => {fileInfo.Name}\r\n- Original length: {length:###,###,###,##0} bytes\r\n- WebP length: {data.Length:###,###,###,##0} bytes").ConfigureAwait(false);
					context.UpdateServerTiming("ngxGenerate", stepwatch.ElapsedMilliseconds);
					if (Handler.IsCacheImages)
						Task.WhenAll
						(
							isDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Caches", $"Prepare cache of a WebP image => {requestURI}") : Task.CompletedTask,
							attachment.PrepareCacheAsync(data, lastModified)
						).Execute();
				}
				catch (InvalidImageContentException ex)
				{
					contentType = fileInfo.GetMimeType();
					await context.WriteLogsAsync(this.Logger, "Downloads", $"Error occurred while generating WebP image  => {ex.Message}", ex).ConfigureAwait(false);
				}
				catch (Exception)
				{
					throw;
				}
			}

			// meta headers
			headers["X-Meta-Service"] = attachment.ServiceName;
			headers["X-Meta-Object"] = attachment.ObjectName;
			headers["X-Meta-System-ID"] = attachment.SystemID?.ToLower();
			headers["X-Meta-Object-ID"] = attachment.ObjectID?.ToLower();
			if (!string.IsNullOrWhiteSpace(attachment.EntityInfo) && attachment.EntityInfo.IsValidUUID())
				headers["X-Meta-Entity-ID"] = attachment.EntityInfo.ToLower();
			else
				headers["X-Meta-Entity"] = attachment.EntityInfo;

			// flush the file to output stream
			if (attachment.IsWebP())
			{
				headers["X-Cache"] = "SEND-FILE";
				await context.SendFileAsync(fileInfo, null, eTag, context.GetHttpCacheControl(context.IsAuthenticated() || isBypassCacheRequested), headers, correlationID, cancellationToken).ConfigureAwait(false);
			}
			else
				await context.WriteAsync(data, contentType, null, eTag, lastModified, context.GetHttpCacheControl(context.IsAuthenticated() || isBypassCacheRequested), default, headers, correlationID, cancellationToken).ConfigureAwait(false);

			// send request to purge cache of CDN
			if (isBypassCacheRequested)
				attachment.SendPurgeCacheRequest(requestURI);

			// update counter & logs
			stopwatch.Stop();
			await Task.WhenAll
			(
				context.UpdateAsync(attachment, "Direct", cancellationToken),
				isDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Downloads", $"Successfully flush a WebP image file ({requestURI}) - Execution times: {stopwatch.GetElapsedTimes()}\r\nInfo: {attachment.ToJson()}") : Task.CompletedTask
			).ConfigureAwait(false);
		}
	}
}