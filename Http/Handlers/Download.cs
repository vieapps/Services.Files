#region Related component
using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class DownloadHandler : Services.FileHandler
	{
		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
		{
			if (context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD"))
				await this.DownloadAsync(context, cancellationToken).ConfigureAwait(false);
			else
				throw new MethodNotAllowedException(context.Request.Method);
		}

		async Task DownloadAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var requestUri = context.GetRequestUri();
			var pathSegments = requestUri.GetRequestPathSegments();
			if (pathSegments.Length < 2 || !pathSegments[1].IsValidUUID())
				throw new InvalidRequestException();

			var identifier = pathSegments[1];
			var direct = pathSegments.Length > 2 && pathSegments[2].Equals("0");

			// check "If-Modified-Since" request to reduce traffict
			var eTag = "File#" + identifier.ToLower();
			if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")) && context.GetHeaderParameter("If-Modified-Since") != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", context.GetCorrelationID());
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
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", null);

			// flush the file to output stream, update counter & logs
			else
			{
				await context.WriteAsync(fileInfo, attachment.ContentType, attachment.IsReadable() && direct ? null : attachment.Filename.Right(attachment.Filename.Length - 33), eTag, cancellationToken).ConfigureAwait(false);
				await Task.WhenAll(
					context.UpdateAsync(attachment, "Download", cancellationToken),
					Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Successfully flush a file (as download) [{requestUri} => {fileInfo.FullName}]") : Task.CompletedTask
				).ConfigureAwait(false);
			}
		}
	}
}