#region Related components
using System;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
using net.vieapps.Components.Caching;
#endregion

namespace net.vieapps.Services.Files
{
	public class ServiceComponent : ServiceBase
	{
		public override string ServiceName => "Files";

		public override void Start(string[] args = null, bool initializeRepository = true, Func<IService, Task> nextAsync = null)
		{
			Utility.Cache = new Cache($"VIEApps-Services-{this.ServiceName}", Components.Utility.Logger.GetLoggerFactory());
			Utility.FilesHttpURI = this.GetHttpURI("Files", "https://fs.vieapps.net");
			while (Utility.FilesHttpURI.EndsWith("/"))
				Utility.FilesHttpURI = Utility.FilesHttpURI.Left(Utility.FilesHttpURI.Length - 1);
			base.Start(args, initializeRepository, nextAsync);
		}

		public override async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			var stopwatch = Stopwatch.StartNew();
			this.WriteLogs(requestInfo, $"Begin request ({requestInfo.Verb} {requestInfo.GetURI()})");
			try
			{
				JToken json = null;
				switch (requestInfo.ObjectName.ToLower())
				{
					case "thumbnail":
						json = await this.ProcessThumbnailAsync(requestInfo, cancellationToken).ConfigureAwait(false);
						break;

					case "attachment":
						json = await this.ProcessAttachmentAsync(requestInfo, cancellationToken).ConfigureAwait(false);
						break;

					case "captcha":
						if (!requestInfo.Verb.IsEquals("GET"))
							throw new MethodAccessException(requestInfo.Verb);
						var code = CaptchaService.GenerateCode(requestInfo.Extra != null && requestInfo.Extra.ContainsKey("Salt") ? requestInfo.Extra["Salt"] : null);
						json = new JObject
						{
							{ "Code", code },
							{ "Uri", $"{Utility.CaptchaURI}{code.Url64Encode()}/{UtilityService.GetUUID().Left(13).Url64Encode()}.jpg" }
						};
						break;

					default:
						throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]");
				}
				stopwatch.Stop();
				this.WriteLogs(requestInfo, $"Success response - Execution times: {stopwatch.GetElapsedTimes()}");
				if (this.IsDebugResultsEnabled)
					this.WriteLogs(requestInfo,
						$"- Request: {requestInfo.ToJson().ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}" + "\r\n" +
						$"- Response: {json?.ToString(this.IsDebugLogEnabled ? Formatting.Indented : Formatting.None)}"
					);
				return json;
			}
			catch (Exception ex)
			{
				throw this.GetRuntimeException(requestInfo, ex, stopwatch);
			}
		}

		#region Working with thumbnail images
		Task<JToken> ProcessThumbnailAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			switch (requestInfo.Verb)
			{
				case "GET":
					return "search".IsEquals(requestInfo.GetObjectIdentity())
						? this.SearchThumbnailsAsync(requestInfo, cancellationToken)
						: Task.FromException<JToken>(new InvalidRequestException());

				case "POST":
					return this.CreateThumbnailAsync(requestInfo, cancellationToken);

				case "DELETE":
					return this.DeleteThumbnailAsync(requestInfo, cancellationToken);

				default:
					return Task.FromException<JToken>(new MethodNotAllowedException(requestInfo.Verb));
			}
		}

		async Task<JToken> SearchThumbnailsAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var request = requestInfo.GetRequestExpando();
			var objectID = requestInfo.GetQueryParameter("x-object-id") ?? requestInfo.GetQueryParameter("object-id");
			if (string.IsNullOrWhiteSpace(objectID) || !objectID.IsValidUUID())
				throw new InvalidRequestException();

			var thumbnails = await Thumbnail.FindAsync(Filters<Thumbnail>.Equals("ObjectID", objectID), Sorts<Thumbnail>.Ascending("Filename"), 0, 1, null, cancellationToken).ConfigureAwait(false);
			var title = (requestInfo.GetParameter("x-object-title") ?? UtilityService.NewUUID).GetANSIUri();
			return thumbnails.ToJArray(thumbnail => thumbnail.ToJson(false, null, true, title));
		}

		async Task<JToken> CreateThumbnailAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// verify
			if (requestInfo.Extra == null
				|| !requestInfo.Extra.ContainsKey("Signature") || !requestInfo.Extra["Signature"].Equals(requestInfo.Body.GetHMACSHA256(this.ValidationKey))
				|| !requestInfo.Extra.ContainsKey("SessionID") || !requestInfo.Extra["SessionID"].Equals(requestInfo.Session.SessionID.GetHMACBLAKE256(this.ValidationKey)))
				throw new InformationInvalidException("The signature is not found or invalid");

			// prepare
			var request = requestInfo.GetBodyExpando();
			var thumbnail = request.Copy<Thumbnail>("Created,CreatedID,LastModified,LastModifiedID".ToHashSet());
			if (string.IsNullOrWhiteSpace(thumbnail.ID) || !thumbnail.ID.IsValidUUID() || !thumbnail.ID.IsEquals(requestInfo.GetObjectIdentity()))
				throw new InvalidRequestException();

			// check permissions
			var objectName = requestInfo.GetParameter("x-object-name");
			var gotRights = thumbnail.IsTemporary
				? !string.IsNullOrWhiteSpace(thumbnail.SystemID) && !string.IsNullOrWhiteSpace(thumbnail.DefinitionID)
					? await requestInfo.Session.User.CanContributeAsync(thumbnail.ServiceName, thumbnail.SystemID, thumbnail.DefinitionID, "").ConfigureAwait(false)
					: await requestInfo.Session.User.CanContributeAsync(thumbnail.ServiceName, objectName, "").ConfigureAwait(false)
				: !string.IsNullOrWhiteSpace(thumbnail.SystemID) && !string.IsNullOrWhiteSpace(thumbnail.DefinitionID)
					? await requestInfo.Session.User.CanEditAsync(thumbnail.ServiceName, thumbnail.SystemID, thumbnail.DefinitionID, thumbnail.ObjectID).ConfigureAwait(false)
					: await requestInfo.Session.User.CanEditAsync(thumbnail.ServiceName, objectName, thumbnail.ObjectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// create new
			thumbnail.CreatedID = thumbnail.LastModifiedID = requestInfo.Session.User.ID;
			thumbnail.Created = thumbnail.LastModified = DateTime.Now;
			await Thumbnail.CreateAsync(thumbnail, cancellationToken).ConfigureAwait(false);
			return thumbnail.ToJson(false, null, true, (requestInfo.GetParameter("x-object-title") ?? UtilityService.NewUUID).GetANSIUri());
		}

		async Task<JToken> DeleteThumbnailAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// verify
			if (requestInfo.Extra == null
				|| !requestInfo.Extra.ContainsKey("Signature") || !requestInfo.Extra["Signature"].Equals(requestInfo.Header["x-app-token"].GetHMACSHA256(this.ValidationKey))
				|| !requestInfo.Extra.ContainsKey("SessionID") || !requestInfo.Extra["SessionID"].Equals(requestInfo.Session.SessionID.GetHMACBLAKE256(this.ValidationKey)))
				throw new InformationInvalidException("The signature is not found or invalid");

			var thumbnail = await Thumbnail.GetAsync<Thumbnail>(requestInfo.GetObjectIdentity(), cancellationToken).ConfigureAwait(false);
			if (thumbnail == null)
				throw new InvalidRequestException();

			// check permissions
			var objectName = requestInfo.GetParameter("x-object-name");
			var gotRights = !string.IsNullOrWhiteSpace(thumbnail.SystemID) && !string.IsNullOrWhiteSpace(thumbnail.DefinitionID)
				? await requestInfo.Session.User.CanEditAsync(thumbnail.ServiceName, thumbnail.SystemID, thumbnail.DefinitionID, thumbnail.ObjectID).ConfigureAwait(false)
				: await requestInfo.Session.User.CanEditAsync(thumbnail.ServiceName, objectName, thumbnail.ObjectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// delete
			await Thumbnail.DeleteAsync<Thumbnail>(thumbnail.ID, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false);
			await this.SendInterCommunicateMessageAsync(new CommunicateMessage("Files")
			{
				Type = "Thumbnail#Delete",
				Data = thumbnail.ToJson(false, null, false)
			}, cancellationToken).ConfigureAwait(false);
			return new JObject();
		}
		#endregion

		#region Working with attachment files
		Task<JToken> ProcessAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			switch (requestInfo.Verb)
			{
				case "GET":
					return "search".IsEquals(requestInfo.GetObjectIdentity())
						? this.SearchAttachmentsAsync(requestInfo, cancellationToken)
						: this.GetAttachmentAsync(requestInfo, cancellationToken);

				case "POST":
					return this.CreateAttachmentAsync(requestInfo, cancellationToken);

				case "PUT":
					return this.UpdateAttachmentAsync(requestInfo, cancellationToken);

				case "DELETE":
					return this.DeleteAttachmentAsync(requestInfo, cancellationToken);

				default:
					return Task.FromException<JToken>(new MethodNotAllowedException(requestInfo.Verb));
			}
		}

		async Task<JToken> SearchAttachmentsAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var request = requestInfo.GetRequestExpando();
			var objectID = requestInfo.GetQueryParameter("x-object-id") ?? requestInfo.GetQueryParameter("object-id");
			if (string.IsNullOrWhiteSpace(objectID) || !objectID.IsValidUUID())
				throw new InvalidRequestException();

			var attachments = await Attachment.FindAsync(Filters<Attachment>.Equals("ObjectID", objectID), Sorts<Attachment>.Ascending("Title").ThenByAscending("Filename"), 0, 1, null, cancellationToken).ConfigureAwait(false);
			return attachments.ToJArray(attachment => attachment.ToJson());
		}

		async Task<JToken> GetAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var objectIdentity = requestInfo.GetObjectIdentity();
			var attachmentID = !string.IsNullOrWhiteSpace(objectIdentity) && objectIdentity.IsValidUUID()
				? objectIdentity
				: requestInfo.GetQueryParameter("x-object-id") ?? requestInfo.GetQueryParameter("object-id") ?? requestInfo.GetQueryParameter("attachment-id") ?? requestInfo.GetQueryParameter("id");

			var attachment = await Attachment.GetAsync<Attachment>(attachmentID, cancellationToken).ConfigureAwait(false);
			if (attachment == null)
				throw new InformationNotFoundException();

			if ("counters".IsEquals(objectIdentity))
			{
				attachment.Downloads.Total++;
				attachment.Downloads.Week = attachment.Downloads.LastUpdated.IsInCurrentWeek() ? attachment.Downloads.Week + 1 : 1;
				attachment.Downloads.Month = attachment.Downloads.LastUpdated.IsInCurrentMonth() ? attachment.Downloads.Month + 1 : 1;
				attachment.Downloads.LastUpdated = DateTime.Now;
				await Attachment.UpdateAsync(attachment, true, cancellationToken).ConfigureAwait(false);
				return attachment.Downloads.ToJson();
			}

			return attachment.ToJson();
		}

		async Task<JToken> CreateAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// verify
			if (requestInfo.Extra == null
				|| !requestInfo.Extra.ContainsKey("Signature") || !requestInfo.Extra["Signature"].Equals(requestInfo.Body.GetHMACSHA256(this.ValidationKey))
				|| !requestInfo.Extra.ContainsKey("SessionID") || !requestInfo.Extra["SessionID"].Equals(requestInfo.Session.SessionID.GetHMACBLAKE256(this.ValidationKey)))
				throw new InformationInvalidException("The signature is not found or invalid");

			// prepare
			var request = requestInfo.GetBodyExpando();
			var attachment = request.Copy<Attachment>("Created,CreatedID,LastModified,LastModifiedID".ToHashSet());
			if (string.IsNullOrWhiteSpace(attachment.ID) || !attachment.ID.IsValidUUID() || !attachment.ID.IsEquals(requestInfo.GetObjectIdentity()))
				throw new InvalidRequestException();

			// check permissions
			var objectName = requestInfo.GetParameter("x-object-name");
			var gotRights = attachment.IsTemporary
				? !string.IsNullOrWhiteSpace(attachment.SystemID) && !string.IsNullOrWhiteSpace(attachment.DefinitionID)
					? await requestInfo.Session.User.CanContributeAsync(attachment.ServiceName, attachment.SystemID, attachment.DefinitionID, "").ConfigureAwait(false)
					: await requestInfo.Session.User.CanContributeAsync(attachment.ServiceName, objectName, "").ConfigureAwait(false)
				: !string.IsNullOrWhiteSpace(attachment.SystemID) && !string.IsNullOrWhiteSpace(attachment.DefinitionID)
					? await requestInfo.Session.User.CanEditAsync(attachment.ServiceName, attachment.SystemID, attachment.DefinitionID, attachment.ObjectID).ConfigureAwait(false)
					: await requestInfo.Session.User.CanEditAsync(attachment.ServiceName, objectName, attachment.ObjectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// create new
			attachment.CreatedID = attachment.LastModifiedID = requestInfo.Session.User.ID;
			attachment.Created = attachment.LastModified = DateTime.Now;
			await Attachment.CreateAsync(attachment, cancellationToken).ConfigureAwait(false);
			return attachment.ToJson();
		}

		async Task<JToken> UpdateAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// verify
			if (requestInfo.Extra == null
				|| !requestInfo.Extra.ContainsKey("Signature") || !requestInfo.Extra["Signature"].Equals(requestInfo.Body.GetHMACSHA256(this.ValidationKey))
				|| !requestInfo.Extra.ContainsKey("SessionID") || !requestInfo.Extra["SessionID"].Equals(requestInfo.Session.SessionID.GetHMACBLAKE256(this.ValidationKey)))
				throw new InformationInvalidException("The signature is not found or invalid");

			// prepare
			var attachment = await Attachment.GetAsync<Attachment>(requestInfo.GetObjectIdentity(), cancellationToken).ConfigureAwait(false);
			if (attachment == null)
				throw new InformationNotFoundException();

			var request = requestInfo.GetBodyExpando();
			attachment.CopyFrom(requestInfo.GetBodyExpando(), "ID,ServiceName,SystemID,DefinitionID,ObjectID,Filename,Size,ContentType,DownloadTimes,IsTemporary,Created,CreatedID,LastModified,LastModifiedID".ToHashSet());

			// check permissions
			var objectName = requestInfo.GetParameter("x-object-name");
			var gotRights = !string.IsNullOrWhiteSpace(attachment.SystemID) && !string.IsNullOrWhiteSpace(attachment.DefinitionID)
				? await requestInfo.Session.User.CanEditAsync(attachment.ServiceName, attachment.SystemID, attachment.DefinitionID, attachment.ObjectID).ConfigureAwait(false)
				: await requestInfo.Session.User.CanEditAsync(attachment.ServiceName, objectName, attachment.ObjectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// update
			attachment.LastModifiedID = requestInfo.Session.User.ID;
			attachment.LastModified = DateTime.Now;
			await Attachment.UpdateAsync(attachment, false, cancellationToken).ConfigureAwait(false);
			return attachment.ToJson();
		}

		async Task<JToken> DeleteAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// verify
			if (requestInfo.Extra == null
				|| !requestInfo.Extra.ContainsKey("Signature") || !requestInfo.Extra["Signature"].Equals(requestInfo.Header["x-app-token"].GetHMACSHA256(this.ValidationKey))
				|| !requestInfo.Extra.ContainsKey("SessionID") || !requestInfo.Extra["SessionID"].Equals(requestInfo.Session.SessionID.GetHMACBLAKE256(this.ValidationKey)))
				throw new InformationInvalidException("The signature is not found or invalid");

			var attachment = await Attachment.GetAsync<Attachment>(requestInfo.GetObjectIdentity(), cancellationToken).ConfigureAwait(false);
			if (attachment == null)
				throw new InformationNotFoundException();

			// check permissions
			var objectName = requestInfo.GetParameter("x-object-name");
			var gotRights = !string.IsNullOrWhiteSpace(attachment.SystemID) && !string.IsNullOrWhiteSpace(attachment.DefinitionID)
				? await requestInfo.Session.User.CanEditAsync(attachment.ServiceName, attachment.SystemID, attachment.DefinitionID, attachment.ObjectID).ConfigureAwait(false)
				: await requestInfo.Session.User.CanEditAsync(attachment.ServiceName, objectName, attachment.ObjectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// delete
			await Attachment.DeleteAsync<Attachment>(attachment.ID, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false);
			await this.SendInterCommunicateMessageAsync(new CommunicateMessage("Files")
			{
				Type = "Attachment#Delete",
				Data = attachment.ToJson(false, null, false)
			}, cancellationToken).ConfigureAwait(false);
			return new JObject();
		}
		#endregion

	}
}