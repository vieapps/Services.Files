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

		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
		{
			if (context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD"))
				await this.FlushAsync(context, cancellationToken).ConfigureAwait(false);
			else if (context.Request.Method.IsEquals("POST"))
				await this.ReceiveAsync(context, cancellationToken).ConfigureAwait(false);
			else
				throw new MethodNotAllowedException(context.Request.Method);
		}

		async Task FlushAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var requestUri = context.GetRequestUri();
			var queryString = requestUri.ParseQuery();
			var pathSegments = requestUri.GetRequestPathSegments();

			var attachment = new AttachmentInfo
			{
				ID = pathSegments.Length > 3 && pathSegments[3].IsValidUUID() ? pathSegments[3] : "",
				ServiceName = !pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				SystemID = pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				ContentType = pathSegments.Length > 2 ? pathSegments[2].Replace("=", "/") : "",
				Filename = pathSegments.Length > 4 && pathSegments[3].IsValidUUID() ? pathSegments[4].UrlDecode() : "",
				IsThumbnail = false
			};

			if (string.IsNullOrWhiteSpace(attachment.ID) || string.IsNullOrWhiteSpace(attachment.Filename))
				throw new InvalidRequestException();

			// check "If-Modified-Since" request to reduce traffict
			var eTag = "File#" + attachment.ID.ToLower();
			if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")) && context.GetHeaderParameter("If-Modified-Since") != null)
			{
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, 0, "public", context.GetCorrelationID());
				if (Global.IsDebugLogEnabled)
					context.WriteLogs(this.Logger, "Http.Downloads", $"Response to request with status code 304 to reduce traffic ({requestUri})");
				return;
			}

			// get info & check permissions
			attachment = await context.GetAsync(attachment.ID, cancellationToken).ConfigureAwait(false);
			if (!await context.CanDownloadAsync(attachment, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// check exist
			var fileInfo = new FileInfo(attachment.GetFilePath());
			if (!fileInfo.Exists)
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", null);

			// flush the file to output stream, update counter & logs
			else
			{
				await context.WriteAsync(fileInfo, attachment.ContentType, attachment.IsReadable() ? null : attachment.Filename, eTag, cancellationToken).ConfigureAwait(false);
				await Task.WhenAll(
					context.UpdateAsync(attachment, attachment.IsReadable() ? "Direct" : "Download", cancellationToken),
					Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Successfully flush a file [{requestUri} => {fileInfo.FullName}]") : Task.CompletedTask
				).ConfigureAwait(false);
			}
		}

		async Task ReceiveAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var stopwatch = Stopwatch.StartNew();
			var serviceName = context.GetParameter("x-service-name");
			var objectName = context.GetParameter("x-object-name");
			var systemID = context.GetParameter("x-system-id");
			var definitionID = context.GetParameter("x-definition-id");
			var objectID = context.GetParameter("x-object-id");
			var isShared = "true".IsEquals(context.GetParameter("x-shared"));
			var isTracked = "true".IsEquals(context.GetParameter("x-tracked"));
			var isTemporary = "true".IsEquals(context.GetParameter("x-temporary"));

			if (string.IsNullOrWhiteSpace(objectID))
				throw new InvalidRequestException("Invalid object identity");

			// check permissions
			var gotRights = isTemporary
				? await context.CanContributeAsync(serviceName, objectName, systemID, definitionID, "", cancellationToken).ConfigureAwait(false)
				: await context.CanEditAsync(serviceName, objectName, systemID, definitionID, objectID, cancellationToken).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// save uploaded files & create meta info
			var attachments = new List<AttachmentInfo>();
			try
			{
				// save uploaded files into temporary directory
				attachments = "true".IsEquals(UtilityService.GetAppSetting("Files:SmallObjects", UtilityService.GetAppSetting("Files:SmallStreams", "false")))
					? await this.ReceiveByFormFileAsync(context, serviceName, objectName, systemID, definitionID, objectID, isShared, isTracked, isTemporary, cancellationToken).ConfigureAwait(false)
					: await this.ReceiveByFormDataAsync(context, serviceName, objectName, systemID, definitionID, objectID, isShared, isTracked, isTemporary, cancellationToken).ConfigureAwait(false);

				// create meta info
				var response = new JArray();
				await attachments.ForEachAsync(async (attachment, token) => response.Add(await context.CreateAsync(attachment, token).ConfigureAwait(false)), cancellationToken, true, false).ConfigureAwait(false);

				// move files from temporary directory to official directory
				attachments.ForEach(attachment => attachment.PrepareDirectories().MoveFile(this.Logger, "Http.Uploads"));

				// response
				await context.WriteAsync(response, cancellationToken).ConfigureAwait(false);
				stopwatch.Stop();
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Uploads", $"{attachments.Count} attachment file(s) has been uploaded - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
			}
			catch (Exception)
			{
				attachments.ForEach(attachment => attachment.DeleteFile(true, this.Logger, "Http.Uploads"));
				throw;
			}
		}

		async Task<List<AttachmentInfo>> ReceiveByFormFileAsync(HttpContext context, string serviceName, string objectName, string systemID, string definitionID, string objectID, bool isShared, bool isTracked, bool isTemporary, CancellationToken cancellationToken)
		{
			var attachments = new List<AttachmentInfo>();
			await context.Request.Form.Files.Where(file => file != null && file.Length > 0).ForEachAsync(async (file, token) =>
			{
				using (var uploadStream = file.OpenReadStream())
				{
					// prepare
					var attachment = new AttachmentInfo
					{
						ID = UtilityService.NewUUID,
						ServiceName = serviceName,
						ObjectName = objectName,
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
					};

					// save file into disc
					using (var fileStream = new FileStream(attachment.GetFilePath(true), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true))
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
					attachments.Add(attachment);
				}
			}, cancellationToken, true, false).ConfigureAwait(false);
			return attachments;
		}

		async Task<List<AttachmentInfo>> ReceiveByFormDataAsync(HttpContext context, string serviceName, string objectName, string systemID, string definitionID, string objectID, bool isShared, bool isTracked, bool isTemporary, CancellationToken cancellationToken)
		{
			// check
			var attachments = new List<AttachmentInfo>();
			if (string.IsNullOrWhiteSpace(context.Request.ContentType) || context.Request.ContentType.PositionOf("multipart/") < 0)
				return attachments;

			// prepare the reader
			var boundary = context.Request.ContentType.ToArray(' ').Where(entry => entry.StartsWith("boundary=")).First().Substring(9);
			if (boundary.Length >= 2 && boundary[0] == '"' && boundary[boundary.Length - 1] == '"')
				boundary = boundary.Substring(1, boundary.Length - 2);
			var reader = new MultipartReader(boundary, context.Request.Body);

			// save all files into temporary directory
			MultipartSection section = null;
			do
			{
				// read the section
				section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false);
				if (section == null)
					break;

				// prepare filename
				var filename = "";
				try
				{
					filename = section.ContentDisposition.ToArray(';').First(part => part.Contains("filename")).ToArray('=').Last().Trim('"');
				}
				catch { }
				if (string.IsNullOrWhiteSpace(filename))
					continue;

				// prepare info
				var attachment = new AttachmentInfo
				{
					ID = UtilityService.NewUUID,
					ServiceName = serviceName,
					ObjectName = objectName,
					SystemID = systemID,
					DefinitionID = definitionID,
					ObjectID = objectID,
					Filename = filename,
					ContentType = section.ContentType,
					IsShared = isShared,
					IsTracked = isTracked,
					IsTemporary = isTemporary,
					Title = filename,
					Description = "",
					IsThumbnail = false
				};

				// save file into disc
				using (var fileStream = new FileStream(attachment.GetFilePath(true), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true))
				{
					var buffer = new byte[AspNetCoreUtilityService.BufferSize];
					var read = 0;
					do
					{
						read = await section.Body.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
						await fileStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
						await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
					} while (read > 0);
					attachment.Size = fileStream.Length;
				}

				// update attachment info
				attachments.Add(attachment);
			} while (section == null);

			// return info of all uploaded files
			return attachments;
		}
	}
}