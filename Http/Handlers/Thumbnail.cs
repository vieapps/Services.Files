#region Related component
using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class ThumbnailHandler : Services.FileHandler
	{
		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken)
			=> context.Request.Method.IsEquals("GET") || context.Request.Method.IsEquals("HEAD")
				? this.ShowAsync(context, cancellationToken)
				: context.Request.Method.IsEquals("POST")
					? this.ReceiveAsync(context, cancellationToken)
					: Task.FromException(new MethodNotAllowedException(context.Request.Method));

		async Task ShowAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var stopwatch = Stopwatch.StartNew();
			var correlationID = context.GetCorrelationID();
			var isDebugLogEnabled = context.IsDebugLogEnabled();
			var processCache = !context.IsBypassCache();

			var requestURI = context.GetRequestUri();
			var requestURL = $"{requestURI}";
			var pathSegments = requestURI.GetRequestPathSegments();

			var handlerName = pathSegments[0];
			var isNoThumbnailImage = requestURI.AbsolutePath.IsEndsWith("/no-image.png") || requestURI.AbsolutePath.IsEndsWith("/no-image.jpg") || requestURI.AbsolutePath.IsEndsWith("/no-image.webp");

			var serviceName = !isNoThumbnailImage && pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "";
			var systemID = !isNoThumbnailImage && pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1].ToLower() : "";
			var isThumbnail = isNoThumbnailImage || (Int32.TryParse(pathSegments.Length > 2 ? pathSegments[2] : "", out var mode) && mode == 0);
			int width = 0, height = 0, index = 0;
			if (!isNoThumbnailImage && Int32.TryParse(pathSegments.Length > 3 ? pathSegments[3] : "", out var twidth) && twidth > 0)
				width = twidth;
			if (!isNoThumbnailImage && !Int32.TryParse(pathSegments.Length > 4 ? pathSegments[4] : "", out var theight) && theight > 0)
				height = theight;
			var identifier = !isNoThumbnailImage && pathSegments.Length > 5 && pathSegments[5].IsValidUUID() ? pathSegments[5].ToLower() : "";
			if (!isNoThumbnailImage && !Int32.TryParse(pathSegments.Length > 6 ? pathSegments[6] : "", out var tindex) && tindex > 0 && tindex < 7)
				index = tindex;
			var asBig = !handlerName.IsStartsWith("thumbnailsmall");
			var format = handlerName.IsEndsWith("webps") || (isNoThumbnailImage && Handler.NoThumbnailImageFilePath.IsEndsWith(".webp"))
				? ImageFormat.Webp
				: handlerName.IsEndsWith("pngs") || (isNoThumbnailImage && Handler.NoThumbnailImageFilePath.IsEndsWith(".png")) || context.GetQueryParameter("asPng") != null || context.GetQueryParameter("transparent") != null
					? ImageFormat.Png
					: ImageFormat.Jpeg;

			// validate the request
			if (!isNoThumbnailImage && (string.IsNullOrWhiteSpace(identifier) || (string.IsNullOrWhiteSpace(serviceName) && string.IsNullOrWhiteSpace(systemID))))
				throw new InvalidRequestException();

			// prepare entity tag and headers
			var cacheKey = (identifier, index, format, width, height, asBig).GetCacheKey();
			var eTag = cacheKey.Replace("thumbnail", "vieapps");
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["X-Cache"] = "None",
				["X-Node"] = Global.NodeID,
				["X-Correlation-ID"] = correlationID
			};

			// check "If-Modified-Since" request to reduce traffict
			var noneMatch = context.GetHeaderParameter("If-None-Match");
			var modifiedSince = context.GetHeaderParameter("If-Modified-Since") ?? context.GetHeaderParameter("If-Unmodified-Since");
			var lastModified = Handler.IsCacheThumbnails && processCache && await Global.Cache.ExistsAsync($"{cacheKey}:time", cancellationToken).ConfigureAwait(false) ? await Global.Cache.GetAsync<long>($"{cacheKey}:time", cancellationToken).ConfigureAwait(false) : 0;
			if (eTag.IsEquals(noneMatch) && modifiedSince != null && lastModified > 0 && modifiedSince.FromHttpDateTime().ToUnixTimestamp() >= lastModified)
			{
				headers["X-Cache"] = "HTTP-304";
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, lastModified, "public", correlationID, headers);
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Response to request with status code 304 to reduce traffic [{eTag} => {requestURL}]").ConfigureAwait(false);
				return;
			}

			// prepare
			var attachment = new AttachmentInfo
			{
				ID = identifier,
				ServiceName = serviceName,
				SystemID = systemID,
				ObjectID = identifier,
				Filename = isThumbnail ? $"{identifier}{(index > 0 ? $"-{index}" : "")}.jpg" : pathSegments.Length > 6 ? pathSegments[6].UrlDecode() : "",
				IsThumbnail = isThumbnail,
				IsTemporary = false
			};

			// check existed
			var fileInfo = new FileInfo(isNoThumbnailImage ? Handler.NoThumbnailImageFilePath : attachment.GetFilePath());
			var hasCached = !isNoThumbnailImage && Handler.IsCacheThumbnails && processCache && await Global.Cache.ExistsAsync(cacheKey, cancellationToken).ConfigureAwait(false);
			if (!hasCached)
			{
				if (!isThumbnail && !isNoThumbnailImage && attachment.Filename.IsEndsWith(".webp"))
				{
					attachment.Filename = attachment.Filename.Left(attachment.Filename.Length - 5);
					fileInfo = new FileInfo(attachment.GetFilePath());
					if (!fileInfo.Exists)
					{
						attachment.Filename += ".webp";
						fileInfo = new FileInfo(attachment.GetFilePath());
					}
				}
				if (!fileInfo.Exists)
				{
					context.ShowError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", correlationID);
					return;
				}
			}

			// check permission
			async Task<bool> gotRightsAsync()
			{
				if (!isThumbnail)
				{
					attachment = await context.GetAsync(attachment.ID, cancellationToken).ConfigureAwait(false);
					return await context.CanDownloadAsync(attachment, cancellationToken).ConfigureAwait(false);
				}
				return true;
			}

			// generate
			async Task<byte[]> getAsync()
			{
				headers["X-Cache"] = "HTTP-200";
				var thumbnail = await Global.Cache.GetAsync<byte[]>(cacheKey, cancellationToken).ConfigureAwait(false);
				if (lastModified < 1)
				{
					fileInfo ??= new FileInfo(isNoThumbnailImage ? Handler.NoThumbnailImageFilePath : attachment.GetFilePath());
					lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
					await Global.Cache.SetAsync($"{cacheKey}:time", lastModified, 0, cancellationToken).ConfigureAwait(false);
				}
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Cached of a thumbnail image was found [{eTag} => {requestURL}]").ConfigureAwait(false);
				return thumbnail;
			}

			async Task<byte[]> generateAsync()
			{
				var stepwatch = Stopwatch.StartNew();
				var original = await fileInfo.ReadAsBinaryAsync(cancellationToken).ConfigureAwait(false);
				byte[] thumbnail;
				if (isNoThumbnailImage)
					thumbnail = original;
				else
					try
					{
						thumbnail = await original.GenerateAsync(format, width, height, asBig, fileInfo.Extension.IsEquals(".webp"), cancellationToken).ConfigureAwait(false);
						stepwatch.Stop();
						if (isDebugLogEnabled)
							await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Generate a thumbnail image successful - Execution times: {stepwatch.GetElapsedTimes()}\r\n- Info: {eTag} => {requestURL}\r\n- Original length: {original.Length:###,###,##0} bytes\r\n- Thumbnail length: {thumbnail.Length:###,###,##0} bytes");
					}
					catch (Exception ex)
					{
						await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Error occurred while generating a thumbnail image\r\n- Path: {fileInfo.FullName}\r\n- URL: {requestURL}\r\n- ETag: {eTag}", ex).ConfigureAwait(false);
						thumbnail = await original.ConvertAsync(format, cancellationToken).ConfigureAwait(false);
					}
				lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
				if (!isNoThumbnailImage && Handler.IsCacheThumbnails)
					attachment.PrepareCacheAsync(index, format, original, lastModified, width, height, asBig).Execute();
				return thumbnail;
			}

			// prepare the thumbnail image
			var generateTask = hasCached ? getAsync() : generateAsync();
			if (!await gotRightsAsync().ConfigureAwait(false))
				throw new AccessDeniedException();

			context.SendSessionState(attachment.SystemID);

			// meta headers
			if (!isThumbnail)
			{
				headers["X-Meta-Service"] = attachment.ServiceName;
				headers["X-Meta-Object"] = attachment.ObjectName;
				headers["X-Meta-System-ID"] = attachment.SystemID?.ToLower();
				headers["X-Meta-Object-ID"] = attachment.ObjectID?.ToLower();
				if (!string.IsNullOrWhiteSpace(attachment.EntityInfo) && attachment.EntityInfo.IsValidUUID())
					headers["X-Meta-Entity-ID"] = attachment.EntityInfo.ToLower();
				else
					headers["X-Meta-Entity"] = attachment.EntityInfo;
			}

			// flush the thumbnail image to output stream
			var asReplacement = false;
			try
			{
				await context.WriteAsync(await generateTask.ConfigureAwait(false), isNoThumbnailImage ? fileInfo.GetMimeType() : $"image/{format}".ToLower(), null, eTag, lastModified, "public", TimeSpan.FromDays(366), headers, correlationID, cancellationToken).ConfigureAwait(false);
			}
			catch (SixLabors.ImageSharp.UnknownImageFormatException ex)
			{
				await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Unknown format of thumbnail image\r\n- Path: {fileInfo.FullName}\r\n- URL: {requestURL}\r\n- ETag: {eTag}", ex).ConfigureAwait(false);
				asReplacement = true;
			}
			catch (Exception ex)
			{
				await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Error occurred while showing a thumbnail image\r\n- Path: {fileInfo.FullName}\r\n- URL: {requestURL}\r\n- ETag: {eTag}", ex).ConfigureAwait(false);
				asReplacement = true;
			}

			// use no-thumbnail-image as a replacement if got any error
			if (asReplacement)
			{
				fileInfo = new FileInfo(Handler.NoThumbnailImageFilePath);
				await context.WriteAsync(fileInfo, fileInfo.GetMimeType(), null, eTag, lastModified, "public", TimeSpan.FromDays(366), headers, correlationID, cancellationToken).ConfigureAwait(false);
			}

			// update counter & logs
			stopwatch.Stop();
			if (!asReplacement)
				await Task.WhenAll
				(
					!isNoThumbnailImage ? context.UpdateAsync(attachment, "Direct", cancellationToken) : Task.CompletedTask,
					isDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Thumbnails", $"Show a thumbnail image successful [{eTag} => {requestURL}] - Execution times: {stopwatch.GetElapsedTimes()}") : Task.CompletedTask
				).ConfigureAwait(false);
		}

		async Task ReceiveAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var stopwatch = Stopwatch.StartNew();
			var serviceName = context.GetParameter("x-service-name");
			var objectName = context.GetParameter("x-object-name");
			var systemID = context.GetParameter("x-system-id");
			var entityInfo = context.GetParameter("x-entity");
			var objectID = context.GetParameter("x-object-id");
			var isTemporary = "true".IsEquals(context.GetParameter("x-temporary"));
			var isDebugLogEnabled = Global.IsDebugLogEnabled || context.ContainsKey("x-logs");

			if (string.IsNullOrWhiteSpace(objectID))
				throw new InvalidRequestException("Invalid object identity");

			// check permissions
			var gotRights = isTemporary
				? await context.CanContributeAsync(serviceName, objectName, systemID, entityInfo, "", cancellationToken).ConfigureAwait(false)
				: await context.CanEditAsync(serviceName, objectName, systemID, entityInfo, objectID, cancellationToken).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			context.SendSessionState(systemID);

			// limit size
			if (!Int32.TryParse(UtilityService.GetAppSetting("Limits:Thumbnail"), out var limitSize))
				limitSize = 1024;

			// prepare uploaded files
			var thumbnails = new List<(byte[] Data, AttachmentInfo Info)>();
			if (context.GetParameter("x-as-base64") != null)
			{
				var body = await context.ReadTextAsync(cancellationToken).ConfigureAwait(false);
				var json = body.ToJson()["Data"];
				if (json is JArray array)
					await array.Take(7).Select(data => data as JValue).ForEachAsync(async data =>
					{
						try
						{
							var thumbnailInfo = data.Value.ToString().ToArray();
							var thumbnailData = thumbnailInfo.Last().Base64ToBytes();
							var thumbnailContentType = thumbnailInfo.First().ToArray(";").First();
							thumbnailData = thumbnailContentType.IsThumbnail() ? thumbnailData : await thumbnailData.ConvertAsync(ImageFormat.Jpeg, cancellationToken).ConfigureAwait(false);
							thumbnails.Add((thumbnailData.Length <= limitSize * 1024 ? thumbnailData : null, new AttachmentInfo()));
						}
						catch { }
					}, true, false).ConfigureAwait(false);
				else if (json is JValue data)
					try
					{
						var thumbnailInfo = data.Value.ToString().ToArray();
						var thumbnailData = thumbnailInfo.Last().Base64ToBytes();
						var thumbnailContentType = thumbnailInfo.First().ToArray(";").First();
						thumbnailData = thumbnailContentType.IsThumbnail() ? thumbnailData : await thumbnailData.ConvertAsync(ImageFormat.Jpeg, cancellationToken).ConfigureAwait(false);
						thumbnails.Add((thumbnailData.Length <= limitSize * 1024 ? thumbnailData : null, new AttachmentInfo()));
					}
					catch { }
			}
			else
			{
				for (var index = 0; index < context.Request.Form.Files.Count && index < 7; index++)
					thumbnails.Add((null, new AttachmentInfo()));

				await context.Request.Form.Files.Take(7)
					.Where(file => file != null && file.ContentType.IsStartsWith("image/") && file.Length > 0 && file.Length <= limitSize * 1024)
					.ForEachAsync(async (file, index) =>
					{
						try
						{
							using var thumbnailStream = file.OpenReadStream();
							var thumbnailData = new byte[file.Length];
							await thumbnailStream.ReadAsync(thumbnailData, cancellationToken).ConfigureAwait(false);
							thumbnails[index] = (file.ContentType.IsThumbnail() ? thumbnailData : await thumbnailData.ConvertAsync(ImageFormat.Jpeg, cancellationToken).ConfigureAwait(false), new AttachmentInfo());
						}
						catch { }
					}, true, false).ConfigureAwait(false);
			}

			// save uploaded files into disc & create meta info
			try
			{
				// save uploaded files into disc
				var title = "";
				try
				{
					title = context.GetParameter("x-object-title")?.Url64Decode();
				}
				catch	{ }
				thumbnails = thumbnails.Select((thumbnail, index) => (thumbnail.Data, thumbnail.Data == null ? thumbnail.Info : new AttachmentInfo
				{
					ID = context.GetParameter("x-attachment-id") ?? UtilityService.NewUUID,
					ServiceName = serviceName,
					ObjectName = objectName,
					SystemID = systemID,
					EntityInfo = entityInfo,
					ObjectID = objectID,
					Size = thumbnail.Data.Length,
					Filename = $"{objectID}{(index > 0 ? $"-{index}" : "")}.jpg",
					ContentType = "image/jpeg",
					IsShared = false,
					IsTracked = false,
					IsTemporary = isTemporary,
					Title = title,
					IsThumbnail = true
				})).ToList();
				await thumbnails.ForEachAsync(thumbnail => thumbnail.Data != null ? thumbnail.Data.SaveAsBinaryAsync(thumbnail.Info.GetFilePath(true), cancellationToken) : Task.CompletedTask, true, false).ConfigureAwait(false);

				// create meta info
				var response = new JArray();
				await thumbnails.ForEachAsync(async (thumbnail, index) =>
				{
					if (thumbnail.Data != null)
						response.Add(await context.CreateAsync(thumbnail.Info, cancellationToken).ConfigureAwait(false));
				}, true, false).ConfigureAwait(false);

				// move files from temporary directory to official directory
				thumbnails.Where(thumbnail => thumbnail.Data != null).ForEach(thumbnail => thumbnail.Info.PrepareDirectories().MoveFile(this.Logger, "Uploads", context.GetCorrelationID(), true));

				// update cache
				if (Handler.IsCacheThumbnails)
				{
					await thumbnails.ForEachAsync((thumbnail, index) => thumbnail.Data != null ? thumbnail.Info.PrepareCacheAsync(index, ImageFormat.Jpeg, thumbnail.Data, DateTime.Now.ToUnixTimestamp()) : Task.CompletedTask, true, false).ConfigureAwait(false);
					if (isDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Uploads", $"Prepare cache of thumbnail images successful ({thumbnails.Select((thumbnail, index) => thumbnail.Data != null ? thumbnail.Info.GetCacheKey(index, ImageFormat.Jpeg) : null).Where(key => key != null).Join(", ")})").ConfigureAwait(false);
				}

				// sync
				await thumbnails.Where(thumbnail => thumbnail.Data != null).ForEachAsync(async thumbnail =>
				{
					new CommunicateMessage(Global.ServiceName)
					{
						Type = "Thumbnail#Sync",
						ExcludedNodeID = Global.NodeID,
						Data = new JObject
						{
							{ "Node", Global.NodeID },
							{ "ServiceName", thumbnail.Info.ServiceName },
							{ "SystemID", thumbnail.Info.SystemID },
							{ "Filename", thumbnail.Info.Filename },
							{ "IsTemporary", false },
							{ "CorrelationID", context.GetCorrelationID() }
						}
					}.Send();
					if (isDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Synchronizers", $"Send an inter-communicate message to sync a thumbnail image ({thumbnail.Info.GetFilePath()})").ConfigureAwait(false);
				}).ConfigureAwait(false);

				// response
				stopwatch.Stop();
				await Task.WhenAll
				(
					context.WriteAsync(response, new Dictionary<string, string>
					{
						["X-Node"] = Global.NodeID,
						["X-Execution-Times"] = stopwatch.GetElapsedTimes(),
						["X-Correlation-ID"] = context.GetCorrelationID()
					}, cancellationToken),
					context.WriteLogsAsync(this.Logger, "Uploads", $"{thumbnails.Count(thumbnail => thumbnail.Data != null)} thumbnail image(s) has been uploaded - Execution times: {stopwatch.GetElapsedTimes()}")
				).ConfigureAwait(false);
			}
			catch
			{
				throw;
			}
		}
	}
}