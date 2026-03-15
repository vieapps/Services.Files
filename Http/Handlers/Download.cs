#region Related component
using Microsoft.AspNetCore.Http;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reactive.Concurrency;
using System.Threading;
using System.Threading.Tasks;

#endregion

namespace net.vieapps.Services.Files
{
	public class DownloadHandler : Services.FileHandler
	{
		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
			=> context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD")
				? this.DownloadAsync(context, cancellationToken)
				: Task.FromException(new MethodNotAllowedException(context.Request.Method));

		async Task DownloadAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var stopwatch = Stopwatch.StartNew();
			var correlationID = context.GetCorrelationID();
			var requestURI = context.GetRequestUri();
			var pathSegments = requestURI.GetRequestPathSegments();
			if (pathSegments.Length < 2 || !pathSegments[1].IsValidUUID())
				throw new InvalidRequestException();

			// check "If-Modified-Since" request to reduce traffict
			var isDebugLogEnabled = context.IsDebugLogEnabled();
			var identifier = pathSegments[1].ToLower();
			var eTag = $"vieapps#{identifier}";
			var noneMatch = context.GetHeaderParameter("If-None-Match");
			var modifiedSince = context.GetHeaderParameter("If-Modified-Since") ?? context.GetHeaderParameter("If-Unmodified-Since");
			if (eTag.IsEquals(noneMatch) && modifiedSince != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, modifiedSince.FromHttpDateTime().ToUnixTimestamp(), "public", correlationID, new Dictionary<string, string> { ["X-Node"] = Global.NodeID });
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Downloads", $"Response to request with status code 304 to reduce traffic ({requestURI})").ConfigureAwait(false);
				return;
			}

			// get & check permissions
			var attachment = await context.GetAsync(identifier, cancellationToken).ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(attachment.ID))
				throw new FileNotFoundException();
			if (!await context.CanDownloadAsync(attachment, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// check exist
			var fileInfo = new FileInfo(attachment.GetFilePath());
			if (!fileInfo.Exists)
			{
				context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", correlationID);
				return;
			}

			// flush the file to output stream, update counter & logs
			context.SendSessionState(attachment.SystemID);
			context.UpdateServerTiming("ngxPrepare", stopwatch.ElapsedMilliseconds);
			if (isDebugLogEnabled)
				await context.WriteLogsAsync(this.Logger, "Downloads", $"Start to download a file ({pathSegments.Join(" / ")})").ConfigureAwait(false);

			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["X-Meta-System"] = attachment.SystemID?.ToLower(),
				["X-Meta-Entity"] = attachment.EntityInfo?.ToLower(),
				["X-Meta-Object"] = attachment.ObjectID?.ToLower(),
				["X-Cache"] = "SEND-FILE",
				["X-Node"] = Global.NodeID
			};
			var cacheControl = context.IsAuthenticated() ? "private, no-cache, no-store" : "public, max-age=31622400, s-maxage=31622400, immutable, stale-while-revalidate=60, stale-if-error=86400";
			await context.SendFileAsync(fileInfo, attachment.GetContentDisposition(pathSegments.Length > 2 && pathSegments[2].Equals("1")), eTag, cacheControl, headers, correlationID, cancellationToken).ConfigureAwait(false);

			await Task.WhenAll
			(
				context.UpdateAsync(attachment, "Download", cancellationToken),
				isDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Downloads", $"Successfully flush a file (as download) [{requestURI} => {fileInfo.FullName}]") : Task.CompletedTask
			).ConfigureAwait(false);
		}
	}
}