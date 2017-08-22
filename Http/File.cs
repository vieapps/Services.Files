#region Related component
using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Web;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class FileHandler : AbstractHttpHandler
	{
		protected override async Task SendInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default(CancellationToken))
		{
			await Global.SendInterCommunicateMessageAsync(message);
		}

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (context.Request.HttpMethod.IsEquals("GET") || context.Request.HttpMethod.IsEquals("HEAD"))
				await this.FlushAsync(context, cancellationToken);
			else if (context.Request.HttpMethod.IsEquals("POST"))
				await this.UpdateAsync(context, cancellationToken);
			else
				throw new MethodNotAllowedException(context.Request.HttpMethod);
		}

		#region Flush file to output stream
		async Task FlushAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare information
			AttachmentInfo info;
			try
			{
				var requestUrl = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length);
				while (requestUrl.StartsWith("/"))
					requestUrl = requestUrl.Right(requestUrl.Length - 1);
				if (requestUrl.IndexOf("?") > 0)
					requestUrl = requestUrl.Left(requestUrl.IndexOf("?"));

				var requestInfo = requestUrl.ToArray('/', true);
				if (requestInfo.Length < 5 || !requestInfo[1].IsValidUUID() || !requestInfo[3].IsValidUUID())
					throw new InvalidRequestException();

				info = new AttachmentInfo(requestInfo[1], requestInfo[2].Replace("=", "/"), requestInfo[3], requestInfo[4]);
				info.FilePath = Global.AttachmentFilesPath + info.SystemID + @"\" + info.Identifier + "-" + info.Filename;
			}
			catch (Exception ex)
			{
				if (context.Response.IsClientConnected)
					context.ShowError(ex);
				return;
			}

			// stop if the client is disconnected
			if (!context.Response.IsClientConnected)
				return;

			// check "If-Modified-Since" request to reduce traffict
			var eTag = "File#" + info.Identifier.ToLower();
			if (context.Request.Headers["If-Modified-Since"] != null && eTag.Equals(context.Request.Headers["If-None-Match"]))
			{
				context.Response.Cache.SetCacheability(HttpCacheability.Public);
				context.Response.StatusCode = (int)HttpStatusCode.NotModified;
				context.Response.StatusDescription = "Not Modified";
				context.Response.Headers.Add("ETag", "\"" + eTag + "\"");
				return;
			}

			// get & check permissions
			Attachment attachment = null;
			try
			{
				attachment = await Global.GetAttachmentAsync(info.Identifier, Global.GetSession(context), cancellationToken);
				if (attachment.IsTemporary)
					info.FilePath = Global.AttachmentFilesPath + @"temp\" + info.Identifier + "-" + info.Filename;

				if (!await Global.IsAbleToDownloadAsync(attachment.ServiceName, attachment.SystemID, attachment.EntityID, attachment.ObjectID))
					throw new AccessDeniedException();
			}
			catch (AccessDeniedException ex)
			{
				if (context.Response.IsClientConnected)
				{
					if (!context.Request.IsAuthenticated && context.Request.QueryString["x-app-token"] == null && context.Request.QueryString["x-passport-token"] == null)
						context.Response.Redirect(Global.GetTransferToPassportUrl(context));
					else
						context.ShowError(ex);
				}
				return;
			}
			catch (Exception ex)
			{
				if (context.Response.IsClientConnected)
					context.ShowError(ex);
				return;
			}

			// check exist
			var fileInfo = new FileInfo(info.FilePath);
			if (!fileInfo.Exists)
			{
				if (context.Response.IsClientConnected)
					context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", null);
				return;
			}

			// flush file into stream
			try
			{
				// set cache policy at client-side
				context.Response.Cache.SetCacheability(HttpCacheability.Public);
				context.Response.Cache.SetExpires(DateTime.Now.AddDays(365));
				context.Response.Cache.SetSlidingExpiration(true);
				context.Response.Cache.SetOmitVaryStar(true);
				context.Response.Cache.SetValidUntilExpires(true);
				context.Response.Cache.SetLastModified(fileInfo.LastWriteTime);
				context.Response.Cache.SetETag(eTag);

				// flush thumbnail image to output stream
				await context.WriteFileToOutputAsync(fileInfo, info.ContentType, eTag, info.IsReadable() ? null : info.Filename, cancellationToken);

				// update counter & logs
				if (!attachment.IsTemporary)
					await Global.UpdateCounterAsync(context, attachment);
			}
			catch (Exception ex)
			{
				if (context.Response.IsClientConnected)
					context.ShowError(ex);
			}
		}
		#endregion

		#region Update (receive uploaded files from the client)
		async Task UpdateAsync(HttpContext context, CancellationToken cancellationToken)
		{
			await Task.Delay(0);
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