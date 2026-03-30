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
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
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
			var stopwatch = Stopwatch.StartNew();
			var stepwatch = Stopwatch.StartNew();
			var correlationID = context.GetCorrelationID();
			var requestURI = context.GetRequestUri();
			var isDebugLogEnabled = context.IsDebugLogEnabled();
			var isForceCacheRequested = context.IsBypassCache();
			var processCache = !isForceCacheRequested;

			var pathSegments = requestURI.GetRequestPathSegments();
			pathSegments = pathSegments.Length > 2 && pathSegments[1].IsEquals(pathSegments[2]) ? pathSegments.Take(0, 1).Concat(pathSegments.Skip(2)).ToArray() : pathSegments;
			var mime = Handler.MIMEs.Any(info => info.Handler.IsEquals(pathSegments[0])) ? Handler.MIMEs.First(info => info.Handler.IsEquals(pathSegments[0])) : (null, null);
			pathSegments = mime.Handler == null ? pathSegments : pathSegments.Take(2).Concat([mime.MIMEType]).Concat(pathSegments.Skip(2)).ToArray();

			var identifier = pathSegments.Length > 3 && pathSegments[3].Length > 31 && pathSegments[3].Left(32).IsValidUUID() ? pathSegments[3].Left(32).ToLower() : "";
			var attachment = new AttachmentInfo
			{
				ID = identifier,
				ServiceName = pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "",
				SystemID = pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1].ToLower() : "",
				ContentType = pathSegments.Length > 2 ? pathSegments[2].Replace("=", "/") : "",
				Filename = pathSegments.Length > 4 ? pathSegments[4].UrlDecode() : pathSegments.Length > 3 && pathSegments[3].Length > 33 && pathSegments[3].Left(32).IsEquals(identifier) ? pathSegments[3].Right(pathSegments[3].Length - 33).UrlDecode() : "",
				IsThumbnail = false
			};

			if (string.IsNullOrWhiteSpace(attachment.ID) || string.IsNullOrWhiteSpace(attachment.Filename))
			{
				await context.WriteLogsAsync(this.Logger, "Downloads", $"Invalid request segments\r\nOriginal:\r\n- {requestURI.GetRequestPathSegments().Select((segment, index) => $"{index}: {segment}").Join("\r\n- ")}\r\nNormalized:\r\n- {pathSegments.Select((segment, index) => $"{index}: {segment}").Join("\r\n- ")}").ConfigureAwait(false);
				throw new InvalidRequestException();
			}

			context.SendSessionState(attachment.SystemID);
			context.UpdateServerTiming("ngxPrepare", stepwatch.ElapsedMilliseconds);
			stepwatch.Restart();

			// check "If-Modified-Since" request to reduce traffic
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["X-Cache"] = "SEND-FILE",
				["X-Node"] = Global.NodeID,
				["X-Correlation-ID"] = correlationID
			};
			var cacheKey = attachment.GetCacheKey("file");
			var eTag = cacheKey.Replace("file", "vieapps");
			var noneMatch = processCache ? context.GetHeaderParameter("If-None-Match") : null;
			var modifiedSince = processCache ? context.GetHeaderParameter("If-Modified-Since") ?? context.GetHeaderParameter("If-Unmodified-Since") : null;

			if (eTag.IsEquals(noneMatch) && modifiedSince != null)
			{
				headers["X-Cache"] = "HTTP-304";
				context.UpdateServerTiming("ngxCache", stepwatch.ElapsedMilliseconds);
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, modifiedSince.FromHttpDateTime().ToUnixTimestamp(), "public", correlationID, headers);
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Downloads", $"Response to request with status code 304 to reduce traffic [{eTag} => {requestURI}]").ConfigureAwait(false);
				return;
			}

			// get info & check permissions
			attachment = await context.GetAsync(attachment.ID, cancellationToken).ConfigureAwait(false);
			if (!await context.CanDownloadAsync(attachment, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// check file
			var fileInfo = new FileInfo(attachment.GetFilePath());
			if (!fileInfo.Exists)
			{
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Downloads", $"Not found: [{requestURI}] => [{fileInfo.FullName}]").ConfigureAwait(false);
				context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", correlationID);
				return;
			}

			if (isDebugLogEnabled)
				await context.WriteLogsAsync(this.Logger, "Downloads", $"Start flush a file => {requestURI}\r\nInfo: {attachment.ToJson()}").ConfigureAwait(false);

			// meta headers
			headers["X-Meta-Service"] = attachment.ServiceName;
			headers["X-Meta-Object"] = attachment.ObjectName;
			headers["X-Meta-System-ID"] = attachment.SystemID?.ToLower();
			headers["X-Meta-Object-ID"] = attachment.ObjectID?.ToLower();
			if (!string.IsNullOrWhiteSpace(attachment.EntityInfo) && attachment.EntityInfo.IsValidUUID())
				headers["X-Meta-Entity-ID"] = attachment.EntityInfo.ToLower();
			else
				headers["X-Meta-Entity"] = attachment.EntityInfo;

			// send the file to output stream
			await context.SendFileAsync(fileInfo, attachment.GetContentDisposition(), eTag, context.GetHttpCacheControl(context.IsAuthenticated() || isForceCacheRequested), headers, correlationID, cancellationToken).ConfigureAwait(false);

			// prepare WebP image cache
			if (Handler.IsCacheImages && attachment.IsCacheableImage() && !attachment.IsWebP() && !await Global.Cache.ExistsAsync(attachment.GetCacheKey("webp"), cancellationToken).ConfigureAwait(false))
				Task.WhenAll
				(
					isDebugLogEnabled ? context.WriteLogsAsync("Caches", $"Prepare WebP image cache => {requestURI}") : Task.CompletedTask,
					attachment.PrepareCacheAsync(null)
				).Execute();

			// send request to purge cache of CDN
			if (isForceCacheRequested)
				attachment.SendPurgeCacheRequest(requestURI);

			// update counter & logs
			stopwatch.Stop();
			await Task.WhenAll
			(
				context.UpdateAsync(attachment, attachment.IsReadable() ? "Direct" : "Download", cancellationToken),
				isDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Downloads", $"Successfully flush a file ({requestURI}) - Execution times: {stopwatch.GetElapsedTimes()}\r\nInfo: {attachment.ToJson()}") : Task.CompletedTask
			).ConfigureAwait(false);
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
			var isDebugLogEnabled = Global.IsDebugLogEnabled || context.ContainsKey("x-logs");

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

			context.SendSessionState(systemID);

			// save uploaded files & create meta info
			var attachments = new List<AttachmentInfo>();
			try
			{
				// save uploaded files into temporary directory
				attachments = "file".IsEquals(context.GetParameter("x-receive-mode")) || "true".IsEquals(UtilityService.GetAppSetting("Files:SmallObjects", UtilityService.GetAppSetting("Files:SmallStreams", "false")))
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
				attachments.Where(attachment => !attachment.IsTemporary).ForEach(attachment => attachment.PrepareDirectories().MoveFile(this.Logger, "Uploads"));

				// update cache
				if (Handler.IsCacheImages)
					attachments.Where(attachment => attachment.IsCacheableImage() && !attachment.IsWebP()).ForEach(attachment => attachment.PrepareCacheAsync(null).Execute());

				// sync
				attachments.Where(attachment => !attachment.IsTemporary).ForEach(attachment =>
				{
					new CommunicateMessage(Global.ServiceName)
					{
						Type = "Attachment#Sync",
						ExcludedNodeID = Global.NodeID,
						Data = new JObject
						{
							{ "Node", Global.NodeID },
							{ "ServiceName", attachment.ServiceName },
							{ "SystemID", attachment.SystemID },
							{ "Filename", $"{attachment.ID}-{attachment.Filename}" },
							{ "IsTemporary", false },
							{ "CorrelationID", context.GetCorrelationID() }
						}
					}.Send();
					if (isDebugLogEnabled)
						context.WriteLogsAsync(this.Logger, "Synchronizers", $"Send an inter-communicate message to sync an attachment file ({attachment.GetFilePath()})").Execute();
				});

				// response as a single image/file
				if (segment.IsEquals("one.image") || segment.IsEquals("one.file") || segment.IsEquals("temp.file"))
				{
					var info = (response as JArray).First as JObject;
					response = segment.IsEquals("temp.file")
					? new JObject
					{
						{ "x-url", info["URIs"].Get<string>("Direct") },
						{ "x-filename", $"{info.Get<string>("ID")}-{info.Get<string>("Filename")}" },
						{ "x-node", Extensions.GetUniqueName($"{Global.ServiceName}.http") }
					}
					: new JObject
					{
						{ context.GetParameter("x-response-name") ?? "url", info["URIs"].Get<string>("Direct") }
					};
				}

				// response
				stopwatch.Stop();
				await Task.WhenAll
				(
					context.WriteAsync(response, Newtonsoft.Json.Formatting.None, new Dictionary<string, string>
					{
						["X-Node"] = Global.NodeID,
						["X-Execution-Times"] = stopwatch.GetElapsedTimes(),
						["X-Correlation-ID"] = context.GetCorrelationID()
					}, cancellationToken),
					context.WriteLogsAsync(this.Logger, "Uploads", $"{attachments.Count} attachment file(s) has been uploaded - Execution times: {stopwatch.GetElapsedTimes()}")
				).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await context.WriteLogsAsync(this.Logger, "Uploads", $"Error occurred while receiving attachment file(s)", ex).ConfigureAwait(false);
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
				var filename = string.IsNullOrWhiteSpace(file.FileName) ? context.GetParameter("x-attachment-file-name") : file.FileName;
				var attachment = new AttachmentInfo
				{
					ID = context.GetParameter("x-attachment-id") ?? UtilityService.NewUUID,
					ServiceName = serviceName,
					ObjectName = objectName,
					SystemID = systemID,
					EntityInfo = entityInfo,
					ObjectID = objectID,
					Size = file.Length,
					Filename = filename,
					ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? context.GetParameter("x-attachment-content-type") : file.ContentType,
					IsShared = isShared,
					IsTracked = isTracked,
					IsTemporary = isTemporary,
					Title = filename,
					Description = "",
					IsThumbnail = false
				}.Normalize();

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
					ContentType = string.IsNullOrWhiteSpace(section.ContentType) ? context.GetParameter("x-attachment-content-type") : section.ContentType,
					IsShared = isShared,
					IsTracked = isTracked,
					IsTemporary = isTemporary,
					Title = filename,
					Description = "",
					IsThumbnail = false
				}.Normalize();

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