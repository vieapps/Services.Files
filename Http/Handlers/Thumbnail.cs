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
			var isDebugLogEnabled = Global.IsDebugLogEnabled || context.Request.Query.ContainsKey("x-logs");
			var useCache = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Thumbnails", "true")) && Global.Cache != null;
			var processCache = context.GetParameter("x-no-cache") == null && context.GetParameter("x-force-cache") == null;

			var requestURI = context.GetRequestUri();
			var requestURL = $"{requestURI}";
			var pathSegments = requestURI.GetRequestPathSegments();

			var handlerName = pathSegments[0];
			var serviceName = pathSegments.Length > 1 && !pathSegments[1].IsValidUUID() ? pathSegments[1] : "";
			var systemID = pathSegments.Length > 1 && pathSegments[1].IsValidUUID() ? pathSegments[1].ToLower() : "";
			if (!Int32.TryParse(pathSegments.Length > 3 ? pathSegments[3] : "", out var width) || width < 0)
				width = 0;
			if (!Int32.TryParse(pathSegments.Length > 4 ? pathSegments[4] : "", out var height) || height < 0)
				height = 0;
			var identifier = pathSegments.Length > 5 && pathSegments[5].IsValidUUID() ? pathSegments[5].ToLower() : "";
			if (!Int32.TryParse(pathSegments.Length > 6 ? pathSegments[6] : "", out var index) || index < 0 || index > 6)
				index = 0;

			var isNoThumbnailImage = requestURI.AbsolutePath.IsEndsWith("/no-image.png") || requestURI.AbsolutePath.IsEndsWith("/no-image.jpg") || requestURI.AbsolutePath.IsEndsWith("/no-image.webp");
			var isThumbnail = isNoThumbnailImage || (Int32.TryParse(pathSegments[2], out var mode) && mode == 0);
			var asBig = !handlerName.IsStartsWith("thumbnailsmall");
			var format = handlerName.IsEndsWith("webps")
				? ImageFormat.Webp
				: handlerName.IsEndsWith("pngs") || (isNoThumbnailImage && Handler.NoThumbnailImageFilePath.IsEndsWith(".png")) || context.GetQueryParameter("asPng") != null || context.GetQueryParameter("transparent") != null
					? ImageFormat.Png
					: ImageFormat.Jpeg;

			// validate the request
			if (!isNoThumbnailImage && (string.IsNullOrWhiteSpace(identifier) || (string.IsNullOrWhiteSpace(serviceName) && string.IsNullOrWhiteSpace(systemID))))
				throw new InvalidRequestException();

			// prepare entity tag and headers
			var eTag = (identifier, index, format, width, height, asBig).GetKey();
			var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["X-Cache"] = "None",
				["X-Node"] = Global.NodeID
			};

			// check "If-Modified-Since" request to reduce traffict
			var noneMatch = context.GetHeaderParameter("If-None-Match");
			var modifiedSince = context.GetHeaderParameter("If-Modified-Since") ?? context.GetHeaderParameter("If-Unmodified-Since");
			var lastModified = useCache && processCache && await Global.Cache.ExistsAsync($"{eTag}:time", cancellationToken).ConfigureAwait(false) ? await Global.Cache.GetAsync<long>($"{eTag}:time", cancellationToken).ConfigureAwait(false) : 0;
			if (eTag.IsEquals(noneMatch) && modifiedSince != null && lastModified > 0 && modifiedSince.FromHttpDateTime().ToUnixTimestamp() >= lastModified)
			{
				headers["X-Cache"] = $"HTTP-304/{typeof(ThumbnailHandler).Assembly.GetVersion(false)}";
				context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, lastModified, "public", correlationID, headers);
				if (isDebugLogEnabled)
					await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Response to request with status code 304 to reduce traffic [{eTag} => {requestURL}]").ConfigureAwait(false);
				return;
			}

			// check existed
			var attachment = new AttachmentInfo
			{
				ID = identifier,
				ServiceName = serviceName,
				SystemID = systemID,
				ObjectID = identifier,
				Filename = isThumbnail ? $"{identifier}{(index > 0 ? $"-{index}" : "")}.jpg" : pathSegments.Length > 6 ? pathSegments[6].UrlDecode() : "",
				IsTemporary = false,
				IsTracked = false,
				IsThumbnail = isThumbnail
			};
			if (format == ImageFormat.Webp && !isThumbnail && attachment.Filename.IsEndsWith(".webp"))
				attachment.Filename = attachment.Filename.Left(attachment.Filename.Length - 5);

			FileInfo fileInfo = null;
			var hasCached = useCache && processCache && await Global.Cache.ExistsAsync(eTag, cancellationToken).ConfigureAwait(false);

			if (hasCached)
				headers["X-Cache"] = $"HTTP-200/{typeof(ThumbnailHandler).Assembly.GetVersion(false)}";
			else
			{
				fileInfo = new FileInfo(isNoThumbnailImage ? Handler.NoThumbnailImageFilePath : attachment.GetFilePath());
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
				var thumbnail = await Global.Cache.GetAsync<byte[]>(eTag, cancellationToken).ConfigureAwait(false);
				if (thumbnail != null)
				{
					if (lastModified < 1)
					{
						fileInfo ??= new FileInfo(isNoThumbnailImage ? Handler.NoThumbnailImageFilePath : attachment.GetFilePath());
						lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
						await Global.Cache.SetAsync($"{eTag}:time", lastModified, 0, cancellationToken).ConfigureAwait(false);
					}
					if (isDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Cached thumbnail was found [{eTag} => {requestURL}]").ConfigureAwait(false);
				}
				return thumbnail;
			}

			async Task<byte[]> generateAsync()
			{
				var stepwatch = Stopwatch.StartNew();
				lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
				var original = await fileInfo.ReadAsBinaryAsync(cancellationToken).ConfigureAwait(false);
				byte[] thumbnail;
				try
				{
					thumbnail = await original.GenerateAsync(format, width, height, asBig, cancellationToken).ConfigureAwait(false);
					stepwatch.Stop();
					if (isDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Generate a thumbnail image successful - Execution times: {stepwatch.GetElapsedTimes()}\r\n- Info: {eTag} => {requestURL}\r\n- Original length: {original.Length:###,###,###,###,###,##0} bytes\r\n- Thumbnail length: {thumbnail.Length:###,###,###,###,###,##0} bytes");
				}
				catch (Exception ex)
				{
					await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Error occurred while generating a thumbnail image => {ex.Message}", ex).ConfigureAwait(false);
					thumbnail = await original.ConvertAsync(format, cancellationToken).ConfigureAwait(false);
				}
				if (useCache)
					this.PrepareCacheAsync(attachment, index, format, original, lastModified, width, height, asBig).Run();
				return thumbnail;
			}

			// prepare the thumbnail image
			var generateTask = hasCached ? getAsync() : generateAsync();
			if (!await gotRightsAsync().ConfigureAwait(false))
				throw new AccessDeniedException();

			// flush the thumbnail image to output stream
			try
			{
				await context.WriteAsync(await generateTask.ConfigureAwait(false), $"image/{format}".ToLower(), null, eTag, lastModified, "public", TimeSpan.FromDays(366), headers, correlationID, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await context.WriteLogsAsync(this.Logger, "Thumbnails", $"Error occurred while flushing a thumbnail image => {ex.Message}", ex).ConfigureAwait(false);
				throw;
			}

			// update counter & logs
			stopwatch.Stop();
			await Task.WhenAll
			(
				context.UpdateAsync(attachment, "Direct", cancellationToken),
				isDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Thumbnails", $"Show a thumbnail image successful ({requestURL}) - Execution times: {stopwatch.GetElapsedTimes()}") : Task.CompletedTask
			).ConfigureAwait(false);
		}

		async Task PrepareCacheAsync(AttachmentInfo attachment, int index, ImageFormat format, byte[] original = null, long lastModified = 0, int width = 0, int height = 0, bool asBig = true)
		{
			if (original == null || lastModified < 1)
			{
				var fileInfo = new FileInfo(attachment.GetFilePath());
				original = await fileInfo.ReadAsBinaryAsync(Global.CancellationToken).ConfigureAwait(false);
				lastModified = fileInfo.LastWriteTime.ToUnixTimestamp();
			}

			byte[] thumbnail;
			try
			{
				thumbnail = await original.GenerateAsync(format, width, height, asBig, Global.CancellationToken).ConfigureAwait(false);
			}
			catch
			{
				thumbnail = await original.ConvertAsync(format, Global.CancellationToken).ConfigureAwait(false);
			}

			var cacheKey = (attachment.ObjectID, index, format, width, height, asBig).GetKey();
			await Task.WhenAll
			(
				Global.Cache.AddSetMembersAsync($"{attachment.ObjectID}:thumbnails", [cacheKey, $"{cacheKey}:time"], Global.CancellationToken),
				Global.Cache.SetAsFragmentsAsync(cacheKey, thumbnail, 0, Global.CancellationToken),
				Global.Cache.SetAsync($"{cacheKey}:time", lastModified, 0, Global.CancellationToken)
			).ConfigureAwait(false);

			if (format != ImageFormat.Webp)
			{
				try
				{
					thumbnail = await original.GenerateAsync(ImageFormat.Webp, width, height, asBig, Global.CancellationToken).ConfigureAwait(false);
				}
				catch
				{
					thumbnail = await original.ConvertAsync(ImageFormat.Webp, Global.CancellationToken).ConfigureAwait(false);
				}

				cacheKey = (attachment.ObjectID, index, ImageFormat.Webp, width, height, asBig).GetKey();
				await Task.WhenAll
				(
					Global.Cache.AddSetMembersAsync($"{attachment.ObjectID}:thumbnails", [cacheKey, $"{cacheKey}:time"], Global.CancellationToken),
					Global.Cache.SetAsFragmentsAsync(cacheKey, thumbnail, 0, Global.CancellationToken),
					Global.Cache.SetAsync($"{cacheKey}:time", lastModified, 0, Global.CancellationToken)
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
			var entityInfo = context.GetParameter("x-entity");
			var objectID = context.GetParameter("x-object-id");
			var isTemporary = "true".IsEquals(context.GetParameter("x-temporary"));

			if (string.IsNullOrWhiteSpace(objectID))
				throw new InvalidRequestException("Invalid object identity");

			// check permissions
			var gotRights = isTemporary
				? await context.CanContributeAsync(serviceName, objectName, systemID, entityInfo, "", cancellationToken).ConfigureAwait(false)
				: await context.CanEditAsync(serviceName, objectName, systemID, entityInfo, objectID, cancellationToken).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// limit size - default is 512 KB
			if (!Int32.TryParse(UtilityService.GetAppSetting("Limits:Thumbnail"), out var limitSize))
				limitSize = 512;

			// read upload file
			var thumbnails = new List<byte[]>();
			var asBase64 = context.GetParameter("x-as-base64") != null;
			if (asBase64)
			{
				var base64Data = (await context.ReadTextAsync(cancellationToken).ConfigureAwait(false)).ToJson()["Data"];
				if (base64Data is JArray base64Array)
					base64Array.Take(7).Select(data => data as JValue).ForEach(data =>
					{
						var thumbnail = data.Value.ToString().ToArray().Last().Base64ToBytes();
						if (thumbnail != null && thumbnail.Length <= limitSize * 1024)
						{
							thumbnails.Add(thumbnail);
						}
						else
							thumbnails.Add(null);
					});
				else if (base64Data is JValue base64Value)
					thumbnails.Add(base64Value.Value.ToString().ToArray().Last().Base64ToBytes());
			}
			else
			{
				for (var index = 0; index < context.Request.Form.Files.Count && index < 7; index++)
					thumbnails.Add(null);

				await context.Request.Form.Files.Take(7)
					.Where(file => file != null && file.ContentType.IsStartsWith("image/") && file.Length > 0 && file.Length <= limitSize * 1024)
					.ForEachAsync(async (file, index) =>
					{
						using var stream = file.OpenReadStream();
						var thumbnail = new byte[file.Length];
						await stream.ReadAsync(thumbnail, cancellationToken).ConfigureAwait(false);
						thumbnails[index] = thumbnail;
					}, true, false).ConfigureAwait(false);
			}

			// save uploaded files & create meta info
			var attachments = new List<AttachmentInfo>();
			var useCache = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Thumbnails", "true")) && Global.Cache != null;
			try
			{
				// save uploaded files into disc
				var title = "";
				try
				{
					title = context.GetParameter("x-object-title")?.Url64Decode()?.GetANSIUri() ?? UtilityService.NewUUID;
				}
				catch
				{
					title = UtilityService.NewUUID;
				}

				await thumbnails.ForEachAsync(async (thumbnail, index) =>
				{
					if (thumbnail != null)
					{
						// prepare
						var attachment = new AttachmentInfo
						{
							ID = context.GetParameter("x-attachment-id") ?? UtilityService.NewUUID,
							ServiceName = serviceName,
							ObjectName = objectName,
							SystemID = systemID,
							EntityInfo = entityInfo,
							ObjectID = objectID,
							Size = thumbnail.Length,
							Filename = $"{objectID}{(index > 0 ? $"-{index}" : "")}.jpg",
							ContentType = "image/jpeg",
							IsShared = false,
							IsTracked = false,
							IsTemporary = isTemporary,
							Title = title,
							Description = "",
							IsThumbnail = true
						};

						// save file into temporary directory
						await thumbnail.SaveAsBinaryAsync(attachment.GetFilePath(true), cancellationToken).ConfigureAwait(false);

						// update attachment info
						attachments.Add(attachment);
					}
				}, true, false).ConfigureAwait(false);

				// create meta info
				var response = new JArray();
				var cacheKeys = new List<string>();
				await attachments.ForEachAsync(async attachment =>
				{
					response.Add(await context.CreateAsync(attachment, cancellationToken).ConfigureAwait(false));
					if (useCache)
					{
						var keys = await Global.Cache.GetSetMembersAsync($"{attachment.ObjectID}:thumbnails", cancellationToken).ConfigureAwait(false) ?? [];
						cacheKeys = cacheKeys.Concat(keys).Concat([$"{attachment.ObjectID}:thumbnails"]).Concat(new[] { ImageFormat.Jpeg, ImageFormat.Webp, ImageFormat.Png }.Select(format => (attachment.ObjectID, 0, format, 0, 0, true).GetKey())).ToList();
					}
				}, true, false).ConfigureAwait(false);

				// move files from temporary directory to official directory
				attachments.ForEach(attachment => attachment.PrepareDirectories().MoveFile(this.Logger, "Uploads", context.GetCorrelationID(), true));

				// update cache
				if (useCache)
				{
					await Global.Cache.RemoveAsync(cacheKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), cancellationToken).ConfigureAwait(false);
					await attachments.ForEachAsync((attachment, index) => this.PrepareCacheAsync(attachment, index, ImageFormat.Jpeg)).ConfigureAwait(false);
				}

				// sync
				await attachments.ForEachAsync(async attachment =>
				{
					new CommunicateMessage(Global.ServiceName)
					{
						Type = "Thumbnail#Delete",
						ExcludedNodeID = Global.NodeID,
						Data = attachment.ToJson(json => json["CorrelationID"] = context.GetCorrelationID())
					}.Send();
					await Task.Delay(UtilityService.GetRandomNumber(456, 789)).ConfigureAwait(false);
					new CommunicateMessage(Global.ServiceName)
					{
						Type = "Thumbnail#Sync",
						ExcludedNodeID = Global.NodeID,
						Data = new JObject
						{
						{ "Node", Global.NodeID },
						{ "ServiceName", attachment.ServiceName },
						{ "SystemID", attachment.SystemID },
						{ "Filename", attachment.Filename },
						{ "IsTemporary", false },
						{ "CorrelationID", context.GetCorrelationID() }
						}
					}.Send();
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync(this.Logger, "Synchronizers", $"Send an inter-communicate message to sync a thumbnail image ({attachment.GetFilePath()})").ConfigureAwait(false);
				}).ConfigureAwait(false);

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
					context.WriteLogsAsync(this.Logger, "Uploads", $"{thumbnails.Count(thumbnail => thumbnail != null)} thumbnail image(s) has been uploaded - Execution times: {stopwatch.GetElapsedTimes()}")
				).ConfigureAwait(false);
			}
			catch (Exception)
			{
				attachments.ForEach(attachment => attachment.DeleteFile(true, this.Logger, "Uploads"));
				throw;
			}
		}
	}
}