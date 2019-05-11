#region Related component
using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class FileHandler : Services.FileHandler
	{
		public override ILogger Logger { get; } = Components.Utility.Logger.CreateLogger<FileHandler>();

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted))
				try
				{
					if (context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD"))
						await this.FlushAsync(context, cts.Token).ConfigureAwait(false);
					else if (context.Request.Method.IsEquals("POST"))
						await this.ReceiveAsync(context, cts.Token).ConfigureAwait(false);
					else
						throw new MethodNotAllowedException(context.Request.Method);
				}
				catch (OperationCanceledException) { }
				catch (Exception ex)
				{
					var requestUri = context.GetRequestUri();
					var queryString = requestUri.ParseQuery();
					await context.WriteLogsAsync(this.Logger, $"Http.{(context.Request.Method.IsEquals("POST") ? "Uploads" : "Downloads")}", $"Error occurred while processing with a file ({context.Request.Method} {requestUri})", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
					if (context.Request.Method.IsEquals("POST"))
						context.WriteHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
					else
					{
						if (ex is AccessDeniedException && !context.User.Identity.IsAuthenticated && !queryString.ContainsKey("x-app-token") && !queryString.ContainsKey("x-passport-token"))
							context.Response.Redirect(context.GetPassportSessionAuthenticatorUrl());
						else
							context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
					}
				}
		}

		async Task FlushAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var requestUri = context.GetRequestUri();
			var queryString = requestUri.ParseQuery();
			var pathSegments = requestUri.GetRequestPathSegments();

			var attachmentInfo = new AttachmentInfo
			{
				ID = pathSegments.Length > 3 && pathSegments[3].IsValidUUID() ? pathSegments[3] : "",
				ServiceName = !pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				SystemID = pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				ContentType = pathSegments.Length > 2 ? pathSegments[2].Replace("=", "/") : "",
				Filename = pathSegments.Length > 4 && pathSegments[3].IsValidUUID() ? pathSegments[4] : "",
				IsThumbnail = false
			};

			if (string.IsNullOrWhiteSpace(attachmentInfo.ID) || string.IsNullOrWhiteSpace(attachmentInfo.Filename))
				throw new InvalidRequestException();

			// check "If-Modified-Since" request to reduce traffict
			var eTag = "File#" + attachmentInfo.ID.ToLower();
			if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")) && context.GetHeaderParameter("If-Modified-Since") != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", context.GetCorrelationID());
				if (Global.IsDebugLogEnabled)
					context.WriteLogs(this.Logger, "Http.Downloads", $"Response to request with status code 304 to reduce traffic ({requestUri})");
				return;
			}

			// get & check permissions
			attachmentInfo = await context.GetAsync(attachmentInfo.ID, cancellationToken).ConfigureAwait(false);
			if (!await context.CanDownloadAsync(attachmentInfo.ServiceName, attachmentInfo.SystemID, attachmentInfo.DefinitionID, attachmentInfo.ObjectID).ConfigureAwait(false))
				throw new AccessDeniedException();

			// check exist
			var fileInfo = new FileInfo(attachmentInfo.GetFilePath());
			if (!fileInfo.Exists)
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", null);

			// flush the file to output stream, update counter & logs
			else
			{
				await context.WriteAsync(fileInfo, attachmentInfo.ContentType, attachmentInfo.IsReadable() ? null : attachmentInfo.Filename, eTag, cancellationToken).ConfigureAwait(false);
				await Task.WhenAll(
					context.UpdateAsync(attachmentInfo, cancellationToken),
					Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Successfully flush a file [{requestUri} => {fileInfo.FullName}]") : Task.CompletedTask
				).ConfigureAwait(false);
			}
		}

		async Task ReceiveAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var stopwatch = Stopwatch.StartNew();
			var serviceName = context.GetParameter("service-name") ?? context.GetParameter("x-service-name");
			var systemID = context.GetParameter("system-id") ?? context.GetParameter("x-system-id");
			var definitionID = context.GetParameter("definition-id") ?? context.GetParameter("x-definition-id");
			var objectName = context.GetParameter("object-name") ?? context.GetParameter("x-object-name");
			var objectID = context.GetParameter("object-identity") ?? context.GetParameter("object-id") ?? context.GetParameter("x-object-id");
			var isShared = "true".IsEquals(context.GetParameter("is-shared") ?? context.GetParameter("x-shared"));
			var isTracked = "true".IsEquals(context.GetParameter("is-tracked") ?? context.GetParameter("x-tracked"));
			var isTemporary = "true".IsEquals(context.GetParameter("is-temporary") ?? context.GetParameter("x-temporary"));

			if (string.IsNullOrWhiteSpace(objectID))
				throw new InvalidRequestException("Invalid object identity");

			// check permissions
			var gotRights = isTemporary
				? !string.IsNullOrWhiteSpace(systemID) && !string.IsNullOrWhiteSpace(definitionID)
					? await context.CanContributeAsync(serviceName, systemID, definitionID, "").ConfigureAwait(false)
					: await context.CanContributeAsync(serviceName, objectName, "").ConfigureAwait(false)
				: !string.IsNullOrWhiteSpace(systemID) && !string.IsNullOrWhiteSpace(definitionID)
					? await context.CanEditAsync(serviceName, systemID, definitionID, objectID).ConfigureAwait(false)
					: await context.CanEditAsync(serviceName, objectName, objectID).ConfigureAwait(false);

			if (!gotRights)
				throw new AccessDeniedException();

			// save uploaded files & create meta info
			var attachmentInfos = new List<AttachmentInfo>();
			try
			{
				// save uploaded files into disc
				attachmentInfos = "true".IsEquals(UtilityService.GetAppSetting("Files:AllowLargeObjects", UtilityService.GetAppSetting("Files:AllowLargeStreams", "false")))
					? await this.ReceiveByMultipartBoundaryAsync(context, serviceName, systemID, definitionID, objectID, isShared, isTracked, isTemporary, cancellationToken).ConfigureAwait(false)
					: await this.ReceiveByMultipartFileAsync(context, serviceName, systemID, definitionID, objectID, isShared, isTracked, isTemporary, cancellationToken).ConfigureAwait(false);

				// create meta info
				var response = new JArray();
				await attachmentInfos.ForEachAsync(async (attachmentInfo, token) => response.Add(await context.CreateAsync(attachmentInfo, objectName, token).ConfigureAwait(false)), cancellationToken, true, false).ConfigureAwait(false);
				await context.WriteAsync(response, cancellationToken).ConfigureAwait(false);
				stopwatch.Stop();
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Uploads", $"{attachmentInfos.Count} attachment file(s) has been uploaded - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
			}
			catch (Exception)
			{
				attachmentInfos.ForEach(attachmentInfo => attachmentInfo.DeleteFile());
				throw;
			}
		}

		async Task<List<AttachmentInfo>> ReceiveByMultipartFileAsync(HttpContext context, string serviceName, string systemID, string definitionID, string objectID, bool isShared, bool isTracked, bool isTemporary, CancellationToken cancellationToken)
		{
			var attachmentInfos = new List<AttachmentInfo>();
			await context.Request.Form.Files.Where(file => file != null && file.Length > 0).ForEachAsync(async (file, token) =>
			{
				using (var uploadStream = file.OpenReadStream())
				{
					// prepare
					var attachmentInfo = new AttachmentInfo
					{
						ID = UtilityService.NewUUID,
						ServiceName = serviceName,
						SystemID = systemID,
						DefinitionID = definitionID,
						ObjectID = objectID,
						Size = file.Length,
						Filename = file.FileName,
						ContentType = file.ContentType,
						IsShared = isShared,
						IsTracked = isTracked,
						IsTemporary = isTemporary,
						Title = file.FileName,
						Description = "",
						IsThumbnail = false
					}.PrepareDirectories();

					// save file into disc
					using (var fileStream = new FileStream(attachmentInfo.GetFilePath(), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true))
					{
						var buffer = new byte[AspNetCoreUtilityService.BufferSize];
						var read = 0;
						do
						{
							read = await uploadStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
							await fileStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
							await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
						} while (read > 0);
					}

					// update attachment info
					attachmentInfos.Add(attachmentInfo);
				}
			}, cancellationToken, true, false).ConfigureAwait(false);
			return attachmentInfos;
		}

		async Task<List<AttachmentInfo>> ReceiveByMultipartBoundaryAsync(HttpContext context, string serviceName, string systemID, string definitionID, string objectID, bool isShared, bool isTracked, bool isTemporary, CancellationToken cancellationToken)
		{
			// check
			var attachmentInfos = new List<AttachmentInfo>();
			if (string.IsNullOrWhiteSpace(context.Request.ContentType) || context.Request.ContentType.PositionOf("multipart/") < 0)
				return attachmentInfos;

			// prepare the boundary of sections
			var boundary = context.Request.ContentType.ToArray(' ').Where(entry => entry.StartsWith("boundary=")).First().Substring("boundary=".Length);
			if (boundary.Length >= 2 && boundary[0] == '"' && boundary[boundary.Length - 1] == '"')
				boundary = boundary.Substring(1, boundary.Length - 2);

			// read all sections (all files) and save into disc
			var reader = new MultipartReader(boundary, context.Request.Body);
			var section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false);
			while (section != null)
			{
				// prepare
				var filename = section.ContentDisposition.ToArray(';').SingleOrDefault(part => part.Contains("filename")).ToArray('=').Last().Trim('"');
				var attachmentInfo = new AttachmentInfo
				{
					ID = UtilityService.NewUUID,
					ServiceName = serviceName,
					SystemID = systemID,
					DefinitionID = definitionID,
					ObjectID = objectID,
					Size = section.Body.Length,
					Filename = filename,
					ContentType = section.ContentType,
					IsShared = isShared,
					IsTracked = isTracked,
					IsTemporary = isTemporary,
					Title = filename,
					Description = "",
					IsThumbnail = false
				}.PrepareDirectories();

				// save file into disc
				using (var fileStream = new FileStream(attachmentInfo.GetFilePath(), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true))
				{
					var buffer = new byte[AspNetCoreUtilityService.BufferSize];
					var read = 0;
					do
					{
						read = await section.Body.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
						await fileStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
						await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
					} while (read > 0);
				}

				// update attachment info
				attachmentInfos.Add(attachmentInfo);

				// read next section (next file)
				section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false);
			}
			return attachmentInfos;
		}
	}
}