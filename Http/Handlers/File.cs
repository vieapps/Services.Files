﻿#region Related component
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
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class FileHandler : Services.FileHandler
	{
		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
			=> context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD")
				? this.FlushAsync(context, cancellationToken)
				: context.Request.Method.IsEquals("POST")
					? this.ReceiveAsync(context, cancellationToken)
					: Task.FromException(new MethodNotAllowedException(context.Request.Method));

		async Task FlushAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var correlationID = context.GetCorrelationID();
			var requestUri = context.GetRequestUri();
			var pathSegments = requestUri.GetRequestPathSegments();

			var attachment = new AttachmentInfo
			{
				ID = pathSegments.Length > 3 && pathSegments[3].Length > 31 && pathSegments[3].Left(32).IsValidUUID() ? pathSegments[3].Left(32).ToLower() : "",
				ServiceName = pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				SystemID = pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1].ToLower() : "",
				ContentType = pathSegments.Length > 2 ? pathSegments[2].Replace("=", "/") : "",
				Filename = pathSegments.Length > 4 ? pathSegments[4].UrlDecode() : "",
				IsThumbnail = false
			};

			if (string.IsNullOrWhiteSpace(attachment.ID) || string.IsNullOrWhiteSpace(attachment.Filename))
				throw new InvalidRequestException();

			// check "If-Modified-Since" request to reduce traffict
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Cache"] = "None" };
			var eTag = "file#" + attachment.ID.ToLower();
			var noneMatch = context.GetHeaderParameter("If-None-Match");
			var modifiedSince = context.GetHeaderParameter("If-Modified-Since") ?? context.GetHeaderParameter("If-Unmodified-Since");
			if (eTag.IsEquals(noneMatch) && modifiedSince != null)
			{
				headers["X-Cache"] = "SVC-304";
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, modifiedSince.FromHttpDateTime().ToUnixTimestamp(), "public", correlationID, headers);
				await Task.WhenAll
				(
					context.FlushAsync(cancellationToken),
					Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Response to request with status code 304 to reduce traffic ({requestUri})") : Task.CompletedTask
				).ConfigureAwait(false);
				return;
			}

			// get info & check permissions
			attachment = await context.GetAsync(attachment.ID, cancellationToken).ConfigureAwait(false);
			if (!await context.CanDownloadAsync(attachment, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// check existed
			var cacheKey = attachment.ContentType.IsStartsWith("image/") && "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Images", "true")) && Global.Cache != null
				? eTag
				: null;
			var hasCached = cacheKey != null && await Global.Cache.ExistsAsync(cacheKey, cancellationToken).ConfigureAwait(false);

			FileInfo fileInfo = null;
			if (!hasCached)
			{
				fileInfo = new FileInfo(attachment.GetFilePath());
				if (!fileInfo.Exists)
				{
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Not found: [{requestUri}] => [{fileInfo.FullName}]").ConfigureAwait(false);
					context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", correlationID);
					return;
				}
			}

			// flush the file to output stream, update counter & logs
			if (hasCached)
			{
				headers["X-Cache"] = "SVC-200";
				var lastModified = await Global.Cache.GetAsync<long>($"{cacheKey}:time", cancellationToken).ConfigureAwait(false);
				using var stream = (await Global.Cache.GetAsync<byte[]>(cacheKey, cancellationToken).ConfigureAwait(false)).ToMemoryStream();
				await Task.WhenAll
				(
					context.WriteAsync(stream, attachment.ContentType, attachment.IsReadable() ? null : attachment.Filename, eTag, lastModified, "public", TimeSpan.FromDays(366), headers, correlationID, cancellationToken),
					Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Successfully flush a cached image ({requestUri})") : Task.CompletedTask
				).ConfigureAwait(false);
			}
			else
			{
				var lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
				using var fileStream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, TextFileReader.BufferSize, true);
				await context.WriteAsync(fileStream, fileInfo.GetMimeType(), attachment.IsReadable() ? null : attachment.Filename, eTag, lastModified, "public", TimeSpan.FromDays(366), headers, correlationID, cancellationToken).ConfigureAwait(false);
				await Task.WhenAll
				(
					cacheKey != null
						? Task.WhenAll
						(
							Global.Cache.SetAsFragmentsAsync(cacheKey, await fileInfo.ReadAsBinaryAsync(cancellationToken).ConfigureAwait(false), cancellationToken),
							Global.Cache.SetAsync($"{cacheKey}:time", lastModified, cancellationToken),
							Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Update an image file into cache successful ({requestUri})") : Task.CompletedTask
						) : Task.CompletedTask,
					Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Successfully flush a file [{requestUri} => {fileInfo.FullName}]") : Task.CompletedTask
				).ConfigureAwait(false);
			}

			await context.UpdateAsync(attachment, attachment.IsReadable() ? "Direct" : "Download", cancellationToken).ConfigureAwait(false);
		}

		async Task ReceiveAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var stopwatch = Stopwatch.StartNew();
			var segment = context.GetRequestPathSegments(true).First();

			var serviceName = context.GetParameter("x-service-name");
			var objectName = context.GetParameter("x-object-name");
			var systemID = context.GetParameter("x-system-id");
			var entityInfo = context.GetParameter("x-entity");
			var objectID = context.GetParameter("x-object-id");
			var isShared = "true".IsEquals(context.GetParameter("x-shared"));
			var isTracked = "true".IsEquals(context.GetParameter("x-tracked"));
			var isTemporary = "true".IsEquals(context.GetParameter("x-temporary"));

			if (string.IsNullOrWhiteSpace(objectID) && !segment.IsEquals("temp.file"))
				throw new InvalidRequestException("Invalid object identity");

			// check permissions
			var gotRights = segment.IsEquals("temp.file")
				? isTemporary && (!string.IsNullOrWhiteSpace(serviceName) || (!string.IsNullOrWhiteSpace(systemID) && systemID.IsValidUUID()))
				: isTemporary
					? await context.CanContributeAsync(serviceName, objectName, systemID, entityInfo, "", cancellationToken).ConfigureAwait(false)
					: await context.CanEditAsync(serviceName, objectName, systemID, entityInfo, objectID, cancellationToken).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// save uploaded files & create meta info
			var attachments = new List<AttachmentInfo>();
			try
			{
				// save uploaded files into temporary directory
				attachments = "true".IsEquals(UtilityService.GetAppSetting("Files:SmallObjects", UtilityService.GetAppSetting("Files:SmallStreams", "false")))
					? await this.ReceiveByFormFileAsync(context, serviceName, objectName, systemID, entityInfo, objectID, isShared, isTracked, isTemporary, cancellationToken).ConfigureAwait(false)
					: await this.ReceiveByFormDataAsync(context, serviceName, objectName, systemID, entityInfo, objectID, isShared, isTracked, isTemporary, cancellationToken).ConfigureAwait(false);

				// update meta
				if (segment.IsEquals("temp.file"))
					attachments.ForEach(attachment => attachment.IsTemporary = true);

				// create meta info
				Exception exception = null;
				JToken response = new JArray();
				await attachments.ForEachAsync(async attachment =>
				{
					if (exception == null)
						try
						{
							(response as JArray).Add(await context.CreateAsync(attachment, cancellationToken).ConfigureAwait(false));
						}
						catch (Exception ex)
						{
							exception = ex;
						}
				}, true, false).ConfigureAwait(false);
				if (exception != null)
					throw exception;

				// move files from temporary directory to official directory
				attachments.Where(attachment => !attachment.IsTemporary).ForEach(attachment => attachment.PrepareDirectories().MoveFile(this.Logger, "Http.Uploads"));

				// response as a single image/file
				if (segment.IsEquals("one.image") || segment.IsEquals("one.file") || segment.IsEquals("temp.file"))
				{
					var info = (response as JArray).First as JObject;
					response = segment.IsEquals("temp.file")
						? new JObject
						{
							{ "x-url", info["URIs"].Get<string>("Direct") },
							{ "x-filename", $"{info.Get<string>("ID")}-{info.Get<string>("Filename")}" },
							{ "x-node", Handler.NodeName }
						}
						: new JObject
						{
							{ context.GetParameter("x-response-name") ?? "url", info["URIs"].Get<string>("Direct") }
						};
				}

				// response
				await context.WriteAsync(response, cancellationToken).ConfigureAwait(false);
				stopwatch.Stop();
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Http.Uploads", $"{attachments.Count} attachment file(s) has been uploaded - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await context.WriteLogsAsync(this.Logger, "Http.Uploads", $"Error occurred while receiving attachment file(s)", ex).ConfigureAwait(false);
				//attachments.ForEach(attachment => attachment.DeleteFile(true, this.Logger, "Http.Uploads"));
				throw;
			}
		}

		async Task<List<AttachmentInfo>> ReceiveByFormFileAsync(HttpContext context, string serviceName, string objectName, string systemID, string entityInfo, string objectID, bool isShared, bool isTracked, bool isTemporary, CancellationToken cancellationToken)
		{
			var attachments = new List<AttachmentInfo>();
			await context.Request.Form.Files.Where(file => file != null && file.Length > 0).ForEachAsync(async file =>
			{
				using var uploadStream = file.OpenReadStream();

				// prepare
				var attachment = new AttachmentInfo
				{
					ID = context.GetParameter("x-attachment-id") ?? UtilityService.NewUUID,
					ServiceName = serviceName,
					ObjectName = objectName,
					SystemID = systemID,
					EntityInfo = entityInfo,
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
						read = await uploadStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
						if (read > 0)
							await fileStream.WriteAsync(buffer, read, cancellationToken).ConfigureAwait(false);
					}
					while (read > 0);
				}

				// update attachment info
				attachments.Add(attachment);
			}, true, false).ConfigureAwait(false);

			return attachments;
		}

		async Task<List<AttachmentInfo>> ReceiveByFormDataAsync(HttpContext context, string serviceName, string objectName, string systemID, string entityInfo, string objectID, bool isShared, bool isTracked, bool isTemporary, CancellationToken cancellationToken)
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
					ID = context.GetParameter("x-attachment-id") ?? UtilityService.NewUUID,
					ServiceName = serviceName,
					ObjectName = objectName,
					SystemID = systemID,
					EntityInfo = entityInfo,
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
						read = await section.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
						if (read > 0)
							await fileStream.WriteAsync(buffer, read, cancellationToken).ConfigureAwait(false);
					}
					while (read > 0);
					attachment.Size = fileStream.Length;
				}

				// update attachment info
				attachments.Add(attachment);
			}
			while (section == null);

			// return info of all uploaded files
			return attachments;
		}
	}
}