#region Related component
using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
					await context.WriteLogsAsync(this.Logger, $"Http.{(context.Request.Method.IsEquals("POST") ? "Uploads" : "Downloads")}", $"Error occurred while processing with a file ({requestUri})", ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
					if (ex is AccessDeniedException && !context.User.Identity.IsAuthenticated && !queryString.ContainsKey("x-app-token") && !queryString.ContainsKey("x-passport-token"))
						context.Response.Redirect(context.GetPassportSessionAuthenticatorUrl());
					else
						context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
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
				Filename = pathSegments.Length > 4 && pathSegments[3].IsValidUUID() ? $"{pathSegments[3]}-{pathSegments[4]}" : "",
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
				await context.WriteAsync(fileInfo, attachmentInfo.ContentType, attachmentInfo.IsReadable() ? null : attachmentInfo.Filename.Right(attachmentInfo.Filename.Length - 33), eTag, cancellationToken).ConfigureAwait(false);
				await Task.WhenAll(
					context.UpdateAsync(attachmentInfo, cancellationToken),
					Global.IsDebugLogEnabled ? context.WriteLogsAsync(this.Logger, "Http.Downloads", $"Successfully flush a file [{requestUri} => {fileInfo.FullName}]") : Task.CompletedTask
				).ConfigureAwait(false);
			}
		}

		async Task ReceiveAsync(HttpContext context, CancellationToken cancellationToken)
		{
			// prepare
			var serviceName = context.GetParameter("service-name") ?? context.GetParameter("x-service-name");
			var systemID = context.GetParameter("system-id") ?? context.GetParameter("x-system-id");
			var definitionID = context.GetParameter("definition-id") ?? context.GetParameter("x-definition-id");
			var objectName = context.GetParameter("object-name") ?? context.GetParameter("x-object-name");
			var objectID = context.GetParameter("object-identity") ?? context.GetParameter("object-id") ?? context.GetParameter("x-object-id");
			var isTemporary = "true".IsEquals(context.GetParameter("x-temporary"));

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

			// prepare directories
			var path = Path.Combine(Handler.AttachmentFilesPath, serviceName != "" ? serviceName : systemID);
			new[] { path, Path.Combine(path, "temp"), Path.Combine(path, "trash") }.ForEach(directory =>
			{
				if (!Directory.Exists(directory))
					Directory.CreateDirectory(directory);
			});

			// save uploaded files & create meta info
			var attachmentInfos = new List<AttachmentInfo>();
			try
			{
				// save uploaded files into disc
				await context.Request.Form.Files.Where(file => file != null && file.Length > 0).ForEachAsync(async (file, token) =>
				{
					using (var uploadStream = file.OpenReadStream())
					{
						// prepare
						var id = UtilityService.NewUUID;
						var attachmentInfo = new AttachmentInfo
						{
							ID = id,
							ServiceName = serviceName,
							SystemID = systemID,
							DefinitionID = definitionID,
							ObjectID = objectID,
							Size = file.Length,
							Filename = file.Name,
							ContentType = file.ContentType,
							IsShared = false,
							IsTracked = false,
							IsTemporary = isTemporary,
							Title = file.Name.ConvertUnicodeToANSI(),
							Description = "",
							IsThumbnail = false
						};

						// save file into disc
						using (var fileStream = new FileStream(attachmentInfo.GetFilePath(), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, TextFileReader.BufferSize, true))
						{
							var buffer = new byte[TextFileReader.BufferSize];
							var read = await uploadStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
							while (read > 0)
							{
								await fileStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
								await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
								read = await uploadStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
							}
						}

						// update attachment info
						attachmentInfos.Add(attachmentInfo);
					}
				}, cancellationToken, true, false).ConfigureAwait(false);

				// create meta info
				await attachmentInfos.ForEachAsync((attachmentInfo, token) => context.CreateAsync(attachmentInfo, objectName, token), cancellationToken).ConfigureAwait(false);
			}
			catch (Exception)
			{
				attachmentInfos.ForEach(attachmentInfo =>
				{
					var filePath = attachmentInfo.GetFilePath();
					if (File.Exists(filePath))
						try
						{
							File.Delete(filePath);
						}
						catch { }
				});
				throw;
			}
		}
	}
}