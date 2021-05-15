#region Related component
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
			var requestUri = context.GetRequestUri();
			var pathSegments = requestUri.GetRequestPathSegments();
			if (Global.IsDebugLogEnabled)
				await context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Start to download a file ({pathSegments.Join(" / ")})").ConfigureAwait(false);

			if (pathSegments.Length < 2 || !pathSegments[1].IsValidUUID())
				throw new InvalidRequestException();

			var identifier = pathSegments[1].ToLower();
			var direct = pathSegments.Length > 2 && pathSegments[2].Equals("0");

			// check "If-Modified-Since" request to reduce traffict
			var eTag = "file#" + identifier;
			var noneMatch = context.GetHeaderParameter("If-None-Match");
			var modifiedSince = context.GetHeaderParameter("If-Modified-Since") ?? context.GetHeaderParameter("If-Unmodified-Since");
			if (eTag.IsEquals(noneMatch) && modifiedSince != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, modifiedSince.FromHttpDateTime().ToUnixTimestamp(), "public", correlationID);
				await context.FlushAsync(cancellationToken).ConfigureAwait(false);
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Response to request with status code 304 to reduce traffic ({requestUri})").ConfigureAwait(false);
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
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", correlationID);

			// flush the file to output stream, update counter & logs
			else
			{
				await context.WriteAsync(fileInfo, attachment.IsReadable() && direct ? null : attachment.Filename, eTag, cancellationToken).ConfigureAwait(false);
				await Task.WhenAll
				(
					context.UpdateAsync(attachment, "Download", cancellationToken),
					Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Successfully flush a file (as download) [{requestUri} => {fileInfo.FullName}]") : Task.CompletedTask
				).ConfigureAwait(false);
			}
		}
	}
}