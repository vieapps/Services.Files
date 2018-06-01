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
		ILogger Logger { get; set; }

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (!context.Request.Method.IsEquals("GET") && !context.Request.Method.IsEquals("HEAD"))
				throw new MethodNotAllowedException(context.Request.Method);

			this.Logger = Components.Utility.Logger.CreateLogger<QRCodeHandler>();

			// prepare information
			var requestUri = context.GetRequestUri();
			var queryString = requestUri.ParseQuery();
			var identifier = "";
			var direct = false;
			try
			{
				var requestInfo = requestUri.GetRequestPathSegments();
				if (requestInfo.Length < 2 || !requestInfo[1].IsValidUUID())
					throw new InvalidRequestException();

				identifier = requestInfo[1];
				direct = requestInfo.Length > 2 && requestInfo[2].Equals("0");
			}
			catch (Exception ex)
			{
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
				return;
			}

			// check "If-Modified-Since" request to reduce traffict
			var eTag = "Attachment#" + identifier.ToLower();
			if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")) && context.GetHeaderParameter("If-Modified-Since") != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", context.GetCorrelationID());
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Downloads", $"Response to request with status code 304 to reduce traffic ({requestUri})").ConfigureAwait(false);
				return;
			}

			// get & check permissions
			Attachment attachment = null;
			try
			{
				attachment = await Handler.GetAttachmentAsync(identifier, context.GetSession(), cancellationToken).ConfigureAwait(false);
				if (attachment == null || string.IsNullOrEmpty(attachment.ID))
					throw new FileNotFoundException();
				if (!await context.CanDownloadAsync(attachment.ServiceName, attachment.SystemID, attachment.DefinitionID, attachment.ObjectID).ConfigureAwait(false))
					throw new AccessDeniedException();
			}
			catch (AccessDeniedException ex)
			{
				if (!context.User.Identity.IsAuthenticated)
					context.Response.Redirect(context.GetPassportSessionAuthenticatorUrl());
				else
					context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
				return;
			}
			catch (Exception ex)
			{
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
				return;
			}

			// check exist
			var fileInfo = new FileInfo(Path.Combine(Handler.AttachmentFilesPath, attachment.IsTemporary ? "temp" : attachment.SystemID, attachment.Name));
			if (!fileInfo.Exists)
			{
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", null);
				return;
			}

			// flush file into stream
			try
			{
				var contentDisposition = attachment.IsReadable() && direct
					? null
					: attachment.Name.Right(attachment.Name.Length - 33);

				await Task.WhenAll(
					context.WriteAsync(fileInfo, attachment.ContentType, contentDisposition, eTag, cancellationToken),
					attachment.IsTemporary ? context.UpdateCounterAsync(attachment) : Task.CompletedTask
				).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
			}
		}
	}
}