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
			using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.CancellationTokenSource.Token))
				try
				{
					// verify the request
					if (!"captcha".IsEquals(requestInfo.ObjectName))
					{
						if (requestInfo.Extra == null || !requestInfo.Extra.TryGetValue("SessionID", out var sessionID) || !sessionID.Equals(requestInfo.Session.SessionID.GetHMACBLAKE256(this.ValidationKey)))
							throw new InvalidRequestException();

						if (requestInfo.Verb.IsEquals("POST") || requestInfo.Verb.IsEquals("PUT"))
						{
							if (!requestInfo.Extra.TryGetValue("Signature", out var signature) || !signature.Equals(requestInfo.Body.GetHMACSHA256(this.ValidationKey)))
								throw new InvalidRequestException();
						}
						else if (requestInfo.Header.TryGetValue("x-app-token", out var appToken))
						{
							if (!requestInfo.Extra.TryGetValue("Signature", out var signature) || !signature.Equals(appToken.GetHMACSHA256(this.ValidationKey)))
								throw new InvalidRequestException();
						}
					}

					// process the request
					JToken json = null;
					switch (requestInfo.ObjectName.ToLower())
					{
						case "thumbnail":
						case "thumbnails":
							json = await this.ProcessThumbnailAsync(requestInfo, cts.Token).ConfigureAwait(false);
							break;

						case "attachment":
						case "attachments":
							json = await this.ProcessAttachmentAsync(requestInfo, cts.Token).ConfigureAwait(false);
							break;

						case "captcha":
						case "captchas":
							if (requestInfo.Verb.IsEquals("GET"))
							{
								var code = CaptchaService.GenerateCode(requestInfo.Extra != null && requestInfo.Extra.ContainsKey("Salt") ? requestInfo.Extra["Salt"] : null);
								json = new JObject
								{
									{ "Code", code },
									{ "Uri", $"{Utility.CaptchaURI}{code.Url64Encode()}/{UtilityService.GetUUID().Left(13).Url64Encode()}.jpg" }
								};
							}
							else
								throw new MethodAccessException(requestInfo.Verb);
							break;

						default:
							if (requestInfo.Verb.IsEquals("GET"))
								json = await this.GetAsync(requestInfo, cts.Token).ConfigureAwait(false);
							else if (requestInfo.Verb.IsEquals("PATCH"))
								json = await this.MarkAsOfficialAsync(requestInfo, cts.Token).ConfigureAwait(false);
							else
								throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]");
							break;
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

				case "PATCH":
					return this.MoveThumbnailsAsync(requestInfo, cancellationToken);

				default:
					return Task.FromException<JToken>(new MethodNotAllowedException(requestInfo.Verb));
			}
		}

		async Task<JToken> SearchThumbnailsAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// prepare
			var request = requestInfo.GetRequestExpando();
			var objectIdentity = requestInfo.GetParameter("x-object-id") ?? requestInfo.GetParameter("object-id");
			if (string.IsNullOrWhiteSpace(objectIdentity))
				throw new InvalidRequestException();

			var objectID = objectIdentity.IsValidUUID()
				? objectIdentity.ToLower()
				: null;

			var objectIDs = !objectIdentity.IsValidUUID()
				? objectIdentity.ToLower().ToArray(",", true)
				: null;

			// get cached
			JToken json = null;
			if (objectID != null)
			{
				var cached = await Utility.Cache.GetAsync<string>($"{objectID}:thumbnails", cancellationToken).ConfigureAwait(false);
				json = cached?.ToJson();
			}
			else if (objectIDs != null)
			{
				var cached = await Utility.Cache.GetAsync<string>(objectIDs.Select(id => $"{id}:thumbnails"), cancellationToken).ConfigureAwait(false);
				if (cached != null && cached.Count(kvp => !string.IsNullOrWhiteSpace(kvp.Value)).Equals(objectIDs.Length))
				{
					json = new JObject();
					cached.ForEach(kvp => json[kvp.Key.Replace(":thumbnails", "")] = kvp.Value.ToJson());
				}
			}

			// no cached => search
			if (json == null)
			{
				// search
				var filter = objectIDs == null
					? Filters<Thumbnail>.Equals("ObjectID", objectID) as IFilterBy<Thumbnail>
					: Filters<Thumbnail>.Or(objectIDs.Select(id => Filters<Thumbnail>.Equals("ObjectID", id)));
				var sort = objectIDs == null
					? Sorts<Thumbnail>.Ascending("Filename")
					: Sorts<Thumbnail>.Ascending("ObjectID").ThenByAscending("Filename");
				var thumbnails = await Thumbnail.FindAsync(filter, sort, 0, 1, null, cancellationToken).ConfigureAwait(false);

				// build JSON
				if (objectIDs == null)
				{
					var title = (requestInfo.GetParameter("x-object-title") ?? UtilityService.NewUUID).GetANSIUri();
					json = thumbnails.ToJArray(thumbnail => thumbnail.ToJson(false, null, true, title));
					await Utility.Cache.SetAsync($"{objectID}:thumbnails", json.ToString(Formatting.None), 0, cancellationToken).ConfigureAwait(false);
				}
				else
				{
					var titles = new JObject();
					try
					{
						titles = (requestInfo.GetParameter("x-object-title") ?? "{}").ToJson() as JObject;
					}
					catch { }
					json = this.BuildJson(thumbnails, thumbnail => thumbnail.ToJson(false, null, true, titles.Get<string>(thumbnail.ID) ?? thumbnail.ID));
					await (json as JObject).ForEachAsync((kvp, token) => Utility.Cache.SetAsync($"{kvp.Key}:thumbnails", kvp.Value.ToString(Formatting.None), 0, token), cancellationToken).ConfigureAwait(false);
				}
			}

			// the response
			if (json is JArray)
				this.NormalizeURIs(requestInfo, json as JArray);
			else
				(json as JObject).ForEach(child => this.NormalizeURIs(requestInfo, child as JArray));
			return json;
		}

		async Task<JToken> CreateThumbnailAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// prepare
			var request = requestInfo.GetBodyExpando();
			var thumbnail = request.Copy<Thumbnail>("Title,Created,CreatedID,LastModified,LastModifiedID".ToHashSet());
			if (string.IsNullOrWhiteSpace(thumbnail.ID) || !thumbnail.ID.IsValidUUID() || !thumbnail.ID.IsEquals(requestInfo.GetObjectIdentity()))
				throw new InvalidRequestException();

			// check permissions
			var gotRights = thumbnail.IsTemporary
				? await Router.GetService(thumbnail.ServiceName).CanContributeAsync(requestInfo, thumbnail.ObjectName, thumbnail.SystemID, thumbnail.DefinitionID, "").ConfigureAwait(false)
				: await Router.GetService(thumbnail.ServiceName).CanEditAsync(requestInfo, thumbnail.ObjectName, thumbnail.SystemID, thumbnail.DefinitionID, thumbnail.ObjectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// create new
			thumbnail.CreatedID = thumbnail.LastModifiedID = requestInfo.Session.User.ID;
			thumbnail.Created = thumbnail.LastModified = DateTime.Now;
			await Thumbnail.CreateAsync(thumbnail, cancellationToken).ConfigureAwait(false);
			await Task.WhenAll(
				Utility.Cache.RemoveAsync($"{thumbnail.ObjectID}:thumbnails", cancellationToken),
				requestInfo.Extra.TryGetValue("Node", out var node) ? this.SendInterCommunicateMessageAsync(new CommunicateMessage("Files")
				{
					Type = "Thumbnail#Sync",
					Data = new JObject
					{
						{ "Node", node },
						{ "ServiceName", thumbnail.ServiceName },
						{ "SystemID", thumbnail.SystemID },
						{ "Filename", thumbnail.Filename },
						{ "IsTemporary", thumbnail.IsTemporary }
					}
				}, cancellationToken) : Task.CompletedTask
			).ConfigureAwait(false);
			return thumbnail.ToJson(false, null, true, (requestInfo.GetParameter("x-object-title") ?? UtilityService.NewUUID).GetANSIUri());
		}

		async Task<JToken> DeleteThumbnailAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var thumbnail = await Thumbnail.GetAsync<Thumbnail>(requestInfo.GetObjectIdentity(), cancellationToken).ConfigureAwait(false);
			if (thumbnail == null)
				throw new InvalidRequestException();

			// check permissions
			var gotRights = await Router.GetService(thumbnail.ServiceName).CanEditAsync(requestInfo, thumbnail.ObjectName, thumbnail.SystemID, thumbnail.DefinitionID, thumbnail.ObjectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// delete
			await Thumbnail.DeleteAsync<Thumbnail>(thumbnail.ID, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false);
			await Task.WhenAll(
				Utility.Cache.RemoveAsync($"{thumbnail.ObjectID}:thumbnails", cancellationToken),
				this.SendInterCommunicateMessageAsync(new CommunicateMessage("Files")
				{
					Type = "Thumbnail#Delete",
					Data = thumbnail.ToJson(false, null, false)
				}, cancellationToken)
			).ConfigureAwait(false);
			return new JObject();
		}

		async Task<JToken> MoveThumbnailsAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var serviceName = requestInfo.GetParameter("x-service-name");
			var objectName = requestInfo.GetParameter("x-object-name");
			var systemID = requestInfo.GetParameter("x-system-id");
			var definitionID = requestInfo.GetParameter("x-definition-id");
			var objectID = requestInfo.GetObjectIdentity() ?? requestInfo.GetParameter("x-object-id");

			var gotRights = await Router.GetService(serviceName).CanEditAsync(requestInfo, objectName, systemID, definitionID, objectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// move from temporary to main directory (mark as official)
			var thumbnails = await Thumbnail.FindAsync(Filters<Thumbnail>.Equals("ObjectID", objectID), Sorts<Thumbnail>.Ascending("Filename"), 0, 1, null, cancellationToken).ConfigureAwait(false);
			return await this.MarkThumbnailsAsOfficialAsync(thumbnails, requestInfo.Session.User.ID, (requestInfo.GetParameter("x-object-title") ?? UtilityService.NewUUID).GetANSIUri(), cancellationToken).ConfigureAwait(false);
		}

		async Task<JToken> MarkThumbnailsAsOfficialAsync(List<Thumbnail> thumbnails, string userID, string objectTitle, CancellationToken cancellationToken)
		{
			var json = new JArray();
			await thumbnails.ForEachAsync(async (thumbnail, token) =>
			{
				if (thumbnail.IsTemporary)
				{
					thumbnail.IsTemporary = false;
					thumbnail.LastModified = DateTime.Now;
					thumbnail.LastModifiedID = userID;
					await Thumbnail.UpdateAsync(thumbnail, userID, token).ConfigureAwait(false);
					await this.SendInterCommunicateMessageAsync(new CommunicateMessage("Files")
					{
						Type = "Thumbnail#Move",
						Data = thumbnail.ToJson(false, null, false)
					}, token).ConfigureAwait(false);
				}
				json.Add(thumbnail.ToJson(false, null, true, objectTitle));
			}, cancellationToken).ConfigureAwait(false);
			return json;
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

				case "PATCH":
					return this.MoveAttachmentsAsync(requestInfo, cancellationToken);

				default:
					return Task.FromException<JToken>(new MethodNotAllowedException(requestInfo.Verb));
			}
		}

		async Task<JToken> SearchAttachmentsAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var request = requestInfo.GetRequestExpando();
			var objectIdentity = requestInfo.GetQueryParameter("x-object-id") ?? requestInfo.GetQueryParameter("object-id");
			if (string.IsNullOrWhiteSpace(objectIdentity))
				throw new InvalidRequestException();

			var objectID = objectIdentity.IsValidUUID()
				? objectIdentity.ToLower()
				: null;

			var objectIDs = !objectIdentity.IsValidUUID()
				? objectIdentity.ToLower().ToArray(",", true)
				: null;

			// get cached
			JToken json = null;
			if (objectID != null)
			{
				var cached = await Utility.Cache.GetAsync<string>($"{objectID}:attachments", cancellationToken).ConfigureAwait(false);
				json = cached?.ToJson();
			}
			else if (objectIDs != null)
			{
				var cached = await Utility.Cache.GetAsync<string>(objectIDs.Select(id => $"{id}:attachments"), cancellationToken).ConfigureAwait(false);
				if (cached != null && cached.Count(kvp => !string.IsNullOrWhiteSpace(kvp.Value)).Equals(objectIDs.Length))
				{
					json = new JObject();
					cached.ForEach(kvp => json[kvp.Key.Replace(":attachments", "")] = kvp.Value.ToJson());
				}
			}

			// no cached => search
			if (json == null)
			{
				var filter = objectIDs == null
				? Filters<Attachment>.Equals("ObjectID", objectID) as IFilterBy<Attachment>
				: Filters<Attachment>.Or(objectIDs.Select(id => Filters<Attachment>.Equals("ObjectID", id)));
				var sort = objectIDs == null
					? Sorts<Attachment>.Ascending("Title").ThenByAscending("Filename")
					: Sorts<Attachment>.Ascending("ObjectID").ThenByAscending("Title").ThenByAscending("Filename");
				var attachments = await Attachment.FindAsync(filter, sort, 0, 1, null, cancellationToken).ConfigureAwait(false);

				// build JSON
				if (objectIDs == null)
				{
					json = attachments.ToJArray(attachment => attachment.ToJson());
					await Utility.Cache.SetAsync($"{objectID}:attachments", json.ToString(Formatting.None), 0, cancellationToken).ConfigureAwait(false);
				}
				else
				{
					json = this.BuildJson(attachments, attachment => attachment.ToJson());
					await (json as JObject).ForEachAsync((kvp, token) => Utility.Cache.SetAsync($"{kvp.Key}:attachments", kvp.Value.ToString(Formatting.None), 0, token), cancellationToken).ConfigureAwait(false);
				}
			}

			// response
			return json;
		}

		async Task<JToken> GetAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// prepare
			var objectIdentity = requestInfo.GetObjectIdentity();
			var objectID = !string.IsNullOrWhiteSpace(objectIdentity) && objectIdentity.IsValidUUID()
				? objectIdentity
				: requestInfo.GetQueryParameter("x-object-id") ?? requestInfo.GetQueryParameter("object-id") ?? requestInfo.GetQueryParameter("attachment-id") ?? requestInfo.GetQueryParameter("id");

			// get object
			var attachment = await Attachment.GetAsync<Attachment>(objectID, cancellationToken).ConfigureAwait(false);
			if (attachment == null)
				throw new InformationNotFoundException();

			// update counters
			if ("counters".IsEquals(objectIdentity))
			{
				attachment.Downloads.Total++;
				attachment.Downloads.Week = attachment.Downloads.LastUpdated.IsInCurrentWeek() ? attachment.Downloads.Week + 1 : 1;
				attachment.Downloads.Month = attachment.Downloads.LastUpdated.IsInCurrentMonth() ? attachment.Downloads.Month + 1 : 1;
				attachment.Downloads.LastUpdated = DateTime.Now;
				await Attachment.UpdateAsync(attachment, true, cancellationToken).ConfigureAwait(false);
				return attachment.Downloads.ToJson();
			}

			// update trackers
			else if ("trackers".IsEquals(objectIdentity))
			{

			}

			// response
			return attachment.ToJson();
		}

		async Task<JToken> CreateAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// prepare
			var request = requestInfo.GetBodyExpando();
			var attachment = request.Copy<Attachment>("Created,CreatedID,LastModified,LastModifiedID".ToHashSet());
			if (string.IsNullOrWhiteSpace(attachment.ID) || !attachment.ID.IsValidUUID() || !attachment.ID.IsEquals(requestInfo.GetObjectIdentity()))
				throw new InvalidRequestException();

			// check permissions
			var gotRights = attachment.IsTemporary
				? await Router.GetService(attachment.ServiceName).CanContributeAsync(requestInfo, attachment.ObjectName, attachment.SystemID, attachment.DefinitionID, "").ConfigureAwait(false)
				: await Router.GetService(attachment.ServiceName).CanEditAsync(requestInfo, attachment.ObjectName, attachment.SystemID, attachment.DefinitionID, attachment.ObjectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// create new
			attachment.CreatedID = attachment.LastModifiedID = requestInfo.Session.User.ID;
			attachment.Created = attachment.LastModified = DateTime.Now;
			await Attachment.CreateAsync(attachment, cancellationToken).ConfigureAwait(false);
			await Task.WhenAll(
				Utility.Cache.RemoveAsync($"{attachment.ObjectID}:attachments", cancellationToken),
				requestInfo.Extra.TryGetValue("Node", out var node) ? this.SendInterCommunicateMessageAsync(new CommunicateMessage("Files")
				{
					Type = "Attachment#Sync",
					Data = new JObject
					{
						{ "Node", node },
						{ "ServiceName", attachment.ServiceName },
						{ "SystemID", attachment.SystemID },
						{ "Filename", attachment.ID + "-" + attachment.Filename },
						{ "IsTemporary", attachment.IsTemporary }
					}
				}, cancellationToken) : Task.CompletedTask
			).ConfigureAwait(false);
			return attachment.ToJson();
		}

		async Task<JToken> UpdateAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// prepare
			var attachment = await Attachment.GetAsync<Attachment>(requestInfo.GetObjectIdentity(), cancellationToken).ConfigureAwait(false);
			if (attachment == null)
				throw new InformationNotFoundException();

			var request = requestInfo.GetBodyExpando();
			attachment.CopyFrom(requestInfo.GetBodyExpando(), "ID,ServiceName,ObjectName,SystemID,DefinitionID,ObjectID,Filename,Size,ContentType,DownloadTimes,IsTemporary,Created,CreatedID,LastModified,LastModifiedID".ToHashSet());

			// check permissions
			var gotRights = await Router.GetService(attachment.ServiceName).CanEditAsync(requestInfo, attachment.ObjectName, attachment.SystemID, attachment.DefinitionID, attachment.ObjectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// update
			attachment.LastModifiedID = requestInfo.Session.User.ID;
			attachment.LastModified = DateTime.Now;
			await Attachment.UpdateAsync(attachment, false, cancellationToken).ConfigureAwait(false);

			await Utility.Cache.RemoveAsync($"{attachment.ObjectID}:attachments", cancellationToken).ConfigureAwait(false);
			return attachment.ToJson();
		}

		async Task<JToken> DeleteAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var attachment = await Attachment.GetAsync<Attachment>(requestInfo.GetObjectIdentity(), cancellationToken).ConfigureAwait(false);
			if (attachment == null)
				throw new InformationNotFoundException();

			// check permissions
			var gotRights = await Router.GetService(attachment.ServiceName).CanEditAsync(requestInfo, attachment.ObjectName, attachment.SystemID, attachment.DefinitionID, attachment.ObjectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// delete
			await Attachment.DeleteAsync<Attachment>(attachment.ID, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false);
			await Task.WhenAll(
				Utility.Cache.RemoveAsync($"{attachment.ObjectID}:attachments", cancellationToken),
				this.SendInterCommunicateMessageAsync(new CommunicateMessage("Files")
				{
					Type = "Attachment#Delete",
					Data = attachment.ToJson(false, null, false)
				}, cancellationToken)
			).ConfigureAwait(false);
			return new JObject();
		}

		async Task<JToken> MoveAttachmentsAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var serviceName = requestInfo.GetParameter("x-service-name");
			var objectName = requestInfo.GetParameter("x-object-name");
			var systemID = requestInfo.GetParameter("x-system-id");
			var definitionID = requestInfo.GetParameter("x-definition-id");
			var objectID = requestInfo.GetObjectIdentity() ?? requestInfo.GetParameter("x-object-id");

			var gotRights = await Router.GetService(serviceName).CanEditAsync(requestInfo, objectName, systemID, definitionID, objectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// move from temporary to main directory (mark as official)
			var attachments = await Attachment.FindAsync(Filters<Attachment>.Equals("ObjectID", objectID), Sorts<Attachment>.Ascending("Title").ThenByAscending("Filename"), 0, 1, null, cancellationToken).ConfigureAwait(false);
			return await this.MarkAttachmentsAsOfficialAsync(attachments, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false);
		}

		async Task<JToken> MarkAttachmentsAsOfficialAsync(List<Attachment> attachments, string userID, CancellationToken cancellationToken)
		{
			var json = new JArray();
			await attachments.ForEachAsync(async (attachment, token) =>
			{
				if (attachment.IsTemporary)
				{
					attachment.IsTemporary = false;
					attachment.LastModified = DateTime.Now;
					attachment.LastModifiedID = userID;
					await Attachment.UpdateAsync(attachment, userID, token).ConfigureAwait(false);
					await this.SendInterCommunicateMessageAsync(new CommunicateMessage("Files")
					{
						Type = "Attachment#Move",
						Data = attachment.ToJson(false, null, false)
					}, token).ConfigureAwait(false);
				}
				json.Add(attachment.ToJson());
			}, cancellationToken).ConfigureAwait(false);
			return json;
		}
		#endregion

		#region Working with both thumbnails and attachment files
		async Task<JToken> GetAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// prepare
			var request = requestInfo.GetRequestExpando();
			var objectIdentity = requestInfo.GetParameter("x-object-id") ?? requestInfo.GetParameter("object-id");
			if (string.IsNullOrWhiteSpace(objectIdentity))
				throw new InvalidRequestException();

			var objectID = objectIdentity.IsValidUUID()
				? objectIdentity.ToLower()
				: null;

			var objectIDs = !objectIdentity.IsValidUUID()
				? objectIdentity.ToLower().ToArray(",", true)
				: null;

			// get cached
			JToken json = null;
			if (objectIDs == null)
			{
				var thumbnailsCachedTask = Utility.Cache.GetAsync<string>($"{objectID}:thumbnails", cancellationToken);
				var attachmentsCachedTask = Utility.Cache.GetAsync<string>($"{objectID}:attachments", cancellationToken);
				await Task.WhenAll(thumbnailsCachedTask, attachmentsCachedTask).ConfigureAwait(false);
				if (!string.IsNullOrWhiteSpace(thumbnailsCachedTask.Result) && !string.IsNullOrWhiteSpace(attachmentsCachedTask.Result))
				{
					json = new JObject
					{
						{ "Thumbnails", thumbnailsCachedTask.Result.ToJson() },
						{ "Attachments", attachmentsCachedTask.Result.ToJson() }
					};
					this.NormalizeURIs(requestInfo, json["Thumbnails"] as JArray);
				}
			}
			else
			{
				var thumbnailsCachedTask = Utility.Cache.GetAsync<string>(objectIDs.Select(id => $"{id}:thumbnails"), cancellationToken);
				var attachmentsCachedTask = Utility.Cache.GetAsync<string>(objectIDs.Select(id => $"{id}:attachments"), cancellationToken);
				await Task.WhenAll(thumbnailsCachedTask, attachmentsCachedTask).ConfigureAwait(false);
				if (thumbnailsCachedTask.Result != null && thumbnailsCachedTask.Result.Count(kvp => !string.IsNullOrWhiteSpace(kvp.Value)).Equals(objectIDs.Length)
					&& attachmentsCachedTask.Result != null && attachmentsCachedTask.Result.Count(kvp => !string.IsNullOrWhiteSpace(kvp.Value)).Equals(objectIDs.Length))
				{
					var thumbnailsJson = new JObject();
					thumbnailsCachedTask.Result.ForEach(kvp => thumbnailsJson[kvp.Key.Replace(":thumbnails", "")] = kvp.Value.ToJson());
					thumbnailsJson.ForEach(child => this.NormalizeURIs(requestInfo, child as JArray));
					var attachmentsJson = new JObject();
					attachmentsCachedTask.Result.ForEach(kvp => attachmentsJson[kvp.Key.Replace(":attachments", "")] = kvp.Value.ToJson());
					json = new JObject();
					objectIDs.ForEach(id =>
					{
						json[id] = new JObject
						{
							{ "Thumbnails", thumbnailsJson[id] },
							{ "Attachments", attachmentsJson[id] }
						};
					});
				}
			}

			// no cache => search database
			if (json == null)
			{
				JToken thumbnailsJson = null;
				var thumbnailsFilter = objectIDs == null
					? Filters<Thumbnail>.Equals("ObjectID", objectID) as IFilterBy<Thumbnail>
					: Filters<Thumbnail>.Or(objectIDs.Select(id => Filters<Thumbnail>.Equals("ObjectID", id)));
				var thumbnailsSort = objectIDs == null
					? Sorts<Thumbnail>.Ascending("Filename")
					: Sorts<Thumbnail>.Ascending("ObjectID").ThenByAscending("Filename");
				var thumbnailsTask = Thumbnail.FindAsync(thumbnailsFilter, thumbnailsSort, 0, 1, null, cancellationToken)
					.ContinueWith(async task =>
					{
						if (objectIDs == null)
						{
							var title = (requestInfo.GetParameter("x-object-title") ?? UtilityService.NewUUID).GetANSIUri();
							thumbnailsJson = task.Result.ToJArray(thumbnail => thumbnail.ToJson(false, null, true, title));
							await Utility.Cache.SetAsync($"{objectID}:thumbnails", thumbnailsJson.ToString(Formatting.None), 0, cancellationToken).ConfigureAwait(false);
						}
						else
						{
							var titles = new JObject();
							try
							{
								titles = (requestInfo.GetParameter("x-object-title") ?? "{}").ToJson() as JObject;
							}
							catch { }
							thumbnailsJson = this.BuildJson(task.Result, thumbnail => thumbnail.ToJson(false, null, true, titles.Get<string>(thumbnail.ID) ?? thumbnail.ID));
							await (thumbnailsJson as JObject).ForEachAsync((kvp, token) => Utility.Cache.SetAsync($"{kvp.Key}:thumbnails", kvp.Value.ToString(Formatting.None), 0, token), cancellationToken).ConfigureAwait(false);
							(thumbnailsJson as JObject).ForEach(child => this.NormalizeURIs(requestInfo, child as JArray));
						}
					}, TaskContinuationOptions.OnlyOnRanToCompletion);

				JToken attachmentsJson = null;
				var attachmentsFilter = objectIDs == null
				? Filters<Attachment>.Equals("ObjectID", objectID) as IFilterBy<Attachment>
				: Filters<Attachment>.Or(objectIDs.Select(id => Filters<Attachment>.Equals("ObjectID", id)));
				var attachmentsSort = objectIDs == null
					? Sorts<Attachment>.Ascending("Title").ThenByAscending("Filename")
					: Sorts<Attachment>.Ascending("ObjectID").ThenByAscending("Title").ThenByAscending("Filename");
				var attachmentsTask = Attachment.FindAsync(attachmentsFilter, attachmentsSort, 0, 1, null, cancellationToken)
					.ContinueWith(async task =>
					{
						if (objectIDs == null)
						{
							attachmentsJson = task.Result.ToJArray(attachment => attachment.ToJson());
							await Utility.Cache.SetAsync($"{objectID}:attachments", attachmentsJson.ToString(Formatting.None), 0, cancellationToken).ConfigureAwait(false);
						}
						else
						{
							attachmentsJson = this.BuildJson(task.Result, attachment => attachment.ToJson());
							await (attachmentsJson as JObject).ForEachAsync((kvp, token) => Utility.Cache.SetAsync($"{kvp.Key}:attachments", kvp.Value.ToString(Formatting.None), 0, token), cancellationToken).ConfigureAwait(false);
						}
					}, TaskContinuationOptions.OnlyOnRanToCompletion);

				await Task.WhenAll(thumbnailsTask, attachmentsTask).ConfigureAwait(false);
				if (objectIDs == null)
					json = new JObject
					{
						{ "Thumbnails", thumbnailsJson },
						{ "Attachments", attachmentsJson }
					};
				else
				{
					json = new JObject();
					objectIDs.ForEach(id =>
					{
						json[id] = new JObject
						{
							{ "Thumbnails", thumbnailsJson?[id] },
							{ "Attachments", attachmentsJson?[id] }
						};
					});
				}
			}

			return json;
		}

		async Task<JToken> MarkAsOfficialAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var serviceName = requestInfo.GetParameter("x-service-name");
			var objectName = requestInfo.GetParameter("x-object-name");
			var systemID = requestInfo.GetParameter("x-system-id");
			var definitionID = requestInfo.GetParameter("x-definition-id");
			var objectID = requestInfo.GetObjectIdentity() ?? requestInfo.GetParameter("x-object-id");
			if (string.IsNullOrWhiteSpace(objectID))
				throw new InvalidRequestException();

			var gotRights = await Router.GetService(serviceName).CanEditAsync(requestInfo, objectName, systemID, definitionID, objectID).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// move from temporary to main directory (mark as official)
			JToken thumbnailsJson = null, attachmentsJson = null;
			var thumbnailsTask = Thumbnail.FindAsync(Filters<Thumbnail>.Equals("ObjectID", objectID), Sorts<Thumbnail>.Ascending("Filename"), 0, 1, null, cancellationToken)
				.ContinueWith(async task => thumbnailsJson = await this.MarkThumbnailsAsOfficialAsync(task.Result, requestInfo.Session.User.ID, (requestInfo.GetParameter("x-object-title") ?? UtilityService.NewUUID).GetANSIUri(), cancellationToken).ConfigureAwait(false), TaskContinuationOptions.OnlyOnRanToCompletion);
			var attachmentsTask = Attachment.FindAsync(Filters<Attachment>.Equals("ObjectID", objectID), Sorts<Attachment>.Ascending("Title").ThenByAscending("Filename"), 0, 1, null, cancellationToken)
				.ContinueWith(async task => attachmentsJson = await this.MarkAttachmentsAsOfficialAsync(task.Result, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false), TaskContinuationOptions.OnlyOnRanToCompletion);

			await Task.WhenAll(thumbnailsTask, attachmentsTask).ConfigureAwait(false);
			return new JObject
			{
				{ "Thumbnails", thumbnailsJson },
				{ "Attachments", attachmentsJson }
			};
		}
		#endregion

		#region Helpers for working with JSON
		JObject BuildJson<T>(List<T> objects, Func<T, JObject> toJson) where T : class
		{
			var json = new JObject();
			var objectID = "";
			JArray children = null;
			objects.ForEach(@object =>
			{
				var child = toJson(@object);
				var objID = child.Get<string>("ObjectID");
				if (!objID.IsEquals(objectID))
				{
					if (children != null && children.Count > 0 && !string.IsNullOrWhiteSpace(objectID))
						json[objectID] = children;
					objectID = objID;
					children = new JArray();
				}
				children.Add(child);
			});
			if (children != null && children.Count > 0 && !string.IsNullOrWhiteSpace(objectID))
				json[objectID] = children;
			return json;
		}

		JArray NormalizeURIs(RequestInfo requestInfo, JArray thumbnails)
		{
			// prepare
			if (!Int32.TryParse(requestInfo.GetParameter("x-width"), out var width))
				width = 0;
			if (!Int32.TryParse(requestInfo.GetParameter("x-height"), out var height))
				height = 0;
			if (!Boolean.TryParse(requestInfo.GetParameter("x-is-big"), out var isBig))
				isBig = false;
			if (!Boolean.TryParse(requestInfo.GetParameter("x-is-png"), out var isPng))
				isPng = false;

			// normalize URIs
			thumbnails.ForEach(thumbnail =>
			{
				var uri = thumbnail.Get<string>("URI");
				if (isBig && isPng)
					uri = uri.Replace("thumbnails", "thumbnailbigpngs");
				else if (isBig)
					uri = uri.Replace("thumbnails", "thumbnailbigs");
				else if (isPng)
					uri = uri.Replace("thumbnails", "thumbnailpngs");
				if (width != 0 || height != 0)
				{
					uri = uri.Replace("/0/0/0/", $"/0/{width}/{height}/");
					uri = uri.Replace("/1/0/0/", $"/1/{width}/{height}/");
				}
				thumbnail["URI"] = uri;
			});
			return thumbnails;
		}
		#endregion

	}
}