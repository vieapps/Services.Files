#region Related component
using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
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
			var correlationID = context.GetCorrelationID();
			var requestURI = context.GetRequestUri();
			var pathSegments = requestURI.GetRequestPathSegments();
			var isDebugLogEnabled = context.IsDebugLogEnabled();
			if (isDebugLogEnabled)
				await context.WriteLogsAsync(this.Logger, "Downloads", $"Start to download a file ({pathSegments.Join(" / ")})").ConfigureAwait(false);

			if (pathSegments.Length < 2 || !pathSegments[1].IsValidUUID())
				throw new InvalidRequestException();

			// check "If-Modified-Since" request to reduce traffict
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

			if (Handler.TrackSessions)
				context.SendSessionState(attachment.SystemID);

			// check exist
			var fileInfo = new FileInfo(attachment.GetFilePath());
			if (!fileInfo.Exists)
				context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", correlationID);

			// flush the file to output stream, update counter & logs
			else
			{
				var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					["X-Meta-System"] = attachment.SystemID?.ToLower(),
					["X-Meta-Entity"] = attachment.EntityInfo?.ToLower(),
					["X-Meta-Object"] = attachment.ObjectID?.ToLower(),
					["X-Node"] = Global.NodeID
				};
				await context.WriteAsync(fileInfo, fileInfo.GetMimeType(), attachment.GetContentDisposition(pathSegments.Length > 2 && pathSegments[2].Equals("1")), eTag, fileInfo.LastWriteTime.ToUnixTimestamp(), "public", TimeSpan.FromDays(366), headers, correlationID, cancellationToken).ConfigureAwait(false);
				await Task.WhenAll
				(
					context.UpdateAsync(attachment, "Download", cancellationToken),
					isDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Downloads", $"Successfully flush a file (as download) [{requestURI} => {fileInfo.FullName}]") : Task.CompletedTask
				).ConfigureAwait(false);
			}
		}
	}
}