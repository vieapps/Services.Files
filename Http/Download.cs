﻿#region Related component
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
	public class DownloadHandler : AbstractHttpHandler
	{
		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (!context.Request.HttpMethod.IsEquals("GET") && !context.Request.HttpMethod.IsEquals("HEAD"))
				throw new InvalidRequestException();

			// prepare information
			var identifier = "";
			var direct = false;
			try
			{
				var requestUrl = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length);
				while (requestUrl.StartsWith("/"))
					requestUrl = requestUrl.Right(requestUrl.Length - 1);
				if (requestUrl.IndexOf("?") > 0)
					requestUrl = requestUrl.Left(requestUrl.IndexOf("?"));

				var requestInfo = requestUrl.ToArray('/', true);
				if (requestInfo.Length < 2 || !requestInfo[1].IsValidUUID())
					throw new InvalidRequestException();

				identifier = requestInfo[1];
				direct = requestInfo.Length > 2 && requestInfo[2].Equals("0");
			}
			catch (Exception ex)
			{
				if (context.Response.IsClientConnected)
					Global.ShowError(context, ex);
				return;
			}

			// stop if the client is disconnected
			if (!context.Response.IsClientConnected)
				return;

			// check "If-Modified-Since" request to reduce traffict
			var eTag = "Attachment#" + identifier.ToLower();
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
				attachment = await Global.GetAttachmentAsync(identifier, Global.GetSession(context), cancellationToken);
				if (attachment == null || string.IsNullOrEmpty(attachment.ID))
					throw new FileNotFoundException();
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
						Global.ShowError(context, ex);
				}
				return;
			}
			catch (Exception ex)
			{
				if (context.Response.IsClientConnected)
					Global.ShowError(context, ex);
				return;
			}

			// check exist
			var fileInfo = new FileInfo(Global.AttachmentFilesPath + (attachment.IsTemporary ? @"temp\" : attachment.SystemID + @"\") + attachment.Name);
			if (!fileInfo.Exists)
			{
				if (context.Response.IsClientConnected)
					Global.ShowError(context, (int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", null, new FileNotFoundException(fileInfo.FullName));
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
				var contentDisposition = attachment.IsReadable() && direct
					? null
					: attachment.Name.Right(attachment.Name.Length - 33);
				await context.WriteFileToOutputAsync(fileInfo, attachment.ContentType, eTag, contentDisposition, cancellationToken);

				// update counter & logs
				if (!attachment.IsTemporary)
					await Global.UpdateCounterAsync(context, attachment);
			}
			catch (Exception ex)
			{
				if (context.Response.IsClientConnected)
					Global.ShowError(context, ex);
			}
		}
	}
}