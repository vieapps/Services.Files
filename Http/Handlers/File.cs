#region Related component
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class FileHandler : FileHttpHandler
	{
		ILogger Logger { get; set; }

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			this.Logger = Components.Utility.Logger.CreateLogger<QRCodeHandler>();

			if (context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD"))
				await this.FlushAsync(context, cancellationToken).ConfigureAwait(false);
			else if (context.Request.Method.IsEquals("POST"))
				await this.UpdateAsync(context, cancellationToken).ConfigureAwait(false);
			else
				throw new MethodNotAllowedException(context.Request.Method);
		}

		#region Flush file to output stream
		async Task FlushAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare information
			var requestUri = context.GetRequestUri();
			var queryString = requestUri.ParseQuery();
			AttachmentInfo info;
			try
			{
				var requestInfo = requestUri.GetRequestPathSegments();
				if (requestInfo.Length < 5 || !requestInfo[1].IsValidUUID() || !requestInfo[3].IsValidUUID())
					throw new InvalidRequestException();

				info = new AttachmentInfo(requestInfo[1], requestInfo[2].Replace("=", "/"), requestInfo[3], requestInfo[4]);
				info.FilePath = Path.Combine(Handler.AttachmentFilesPath, info.SystemID, info.Identifier + "-" + info.Filename);
			}
			catch (Exception ex)
			{
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
				return;
			}

			// check "If-Modified-Since" request to reduce traffict
			var eTag = "File#" + info.Identifier.ToLower();
			if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")) && context.GetHeaderParameter("If-Modified-Since") != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", context.GetCorrelationID());
				if (Global.IsDebugLogEnabled)
					context.WriteLogs(this.Logger, "Files", $"Response to request with status code 304 to reduce traffic ({requestUri})");
				return;
			}

			// get & check permissions
			Attachment attachment = null;
			try
			{
				attachment = await Handler.GetAttachmentAsync(info.Identifier, context.GetSession(), cancellationToken).ConfigureAwait(false);
				if (attachment.IsTemporary)
					info.FilePath = Path.Combine(Handler.AttachmentFilesPath, "temp", info.Identifier + "-" + info.Filename);

				if (!await context.CanDownloadAsync(attachment.ServiceName, attachment.SystemID, attachment.DefinitionID, attachment.ObjectID).ConfigureAwait(false))
					throw new AccessDeniedException();
			}
			catch (AccessDeniedException ex)
			{
				if (!context.User.Identity.IsAuthenticated && !queryString.ContainsKey("x-app-token") && !queryString.ContainsKey("x-passport-token"))
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
			var fileInfo = new FileInfo(info.FilePath);
			if (!fileInfo.Exists)
			{
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", null);
				return;
			}

			// flush file into stream
			try
			{
				// flush thumbnail image to output stream, update counter & logs
				await Task.WhenAll(
					context.WriteAsync(fileInfo, info.ContentType, info.IsReadable() ? null : info.Filename, eTag, cancellationToken),
					attachment.IsTemporary ? context.UpdateCounterAsync(attachment) : Task.CompletedTask
				).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
			}
		}
		#endregion

		#region Update (receive uploaded files from the client)
		Task UpdateAsync(HttpContext context, CancellationToken cancellationToken)
		{
			throw new NotImplementedException();
		}
		#endregion

	}

	#region Attachment info	 
	public struct AttachmentInfo
	{
		public AttachmentInfo(string systemID, string contentType, string identifier, string filename)
		{
			this.SystemID = systemID;
			this.ContentType = contentType;
			this.Identifier = identifier;
			this.Filename = filename;
			this.FilePath = "";
		}

		public string SystemID { get; set; }

		public string ContentType { get; set; }

		public string Identifier { get; set; }

		public string Filename { get; set; }

		public string FilePath { get; set; }
	}
	#endregion

}