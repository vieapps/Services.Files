#region Related components
using System;
using System.IO;
using System.Linq;
using System.Dynamic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Repository;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class ServiceComponent : ServiceBase
	{
		public override string ServiceName => "Files";

		#region Properties
		bool Sync { get; } = "true".IsEquals(UtilityService.GetAppSetting("Files:Sync", "false"));

		int SyncMinutes { get; } = UtilityService.GetAppSetting("Files:Sync:Minutes", "13").As<int>();

		int SyncHours { get; } = UtilityService.GetAppSetting("Files:Sync:Hours", "24").As<int>();

		int SyncThreads { get; } = UtilityService.GetAppSetting("Files:Sync:Threads", "32").As<int>();

		bool SyncInParallels { get; } = "true".IsEquals(UtilityService.GetAppSetting("Files:Sync:Parallels", "false"));

		string AttachmentsDirectory { get; } = UtilityService.GetAppSetting("Files:Sync:Directory:Attachments", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-files", "attachments"));

		string AvatarsDirectory { get; } = UtilityService.GetAppSetting("Files:Sync:Directory:Avatars", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data-files", "user-avatars"));

		bool PrepareCache { get; } = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Prepare", "true"));

		bool IsPrepareCacheRequester { get; } = "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:Requester", "false"));

		ConcurrentQueue<JObject> PrepareCacheRequests { get; } = [];

		bool IsPreparingCache { get; set; } = false;

		IDisposable CacheCommunicator { get; set; }
		#endregion

		#region Register & Start the service
		void RegisterCacheCommunicator()
		{
			this.CacheCommunicator?.Dispose();
			this.CacheCommunicator = Router.GotBackupRouter()
				? Router.BackupChannel.AssignProcessL1CacheRequest(Utility.Cache, this)
				: Router.IncomingChannel.AssignProcessL1CacheRequest(Utility.Cache, this);
			Utility.Cache.AssignSendL1CacheRequest(this, Router.GotBackupRouter());
			Utility.HttpCache.AssignSendL1CacheRequest($"{this.ServiceName}.HTTP", this.NodeID, Router.GotBackupRouter());
		}

		public override Task RegisterServiceAsync(IEnumerable<string> args, Action<IService> onSuccess = null, Action<Exception> onError = null)
			=> base.RegisterServiceAsync
			(
				args,
				_ =>
				{
					this.RegisterCacheCommunicator();
					onSuccess?.Invoke(this);
				},
				onError
			);

		public override Task UnregisterServiceAsync(IEnumerable<string> args, bool available = true, Action<IService> onSuccess = null, Action<Exception> onError = null)
			=> base.UnregisterServiceAsync
			(
				args,
				available,
				_ =>
				{
					this.CacheCommunicator?.Dispose();
					this.CacheCommunicator = null;
					onSuccess?.Invoke(this);
				},
				onError
			);

		public override Task StartAsync(string[] args = null, bool initializeRepository = true, Action<IService> next = null)
			=> this.StartAsync(args, (_, _) => this.RegisterCacheCommunicator(), initializeRepository, _ =>
			{
				Utility.FilesHttpURI = this.GetHttpURI("Files", "https://fs.vieapps.net");
				while (Utility.FilesHttpURI.EndsWith('/'))
					Utility.FilesHttpURI = Utility.FilesHttpURI.Left(Utility.FilesHttpURI.Length - 1);

				if (this.Sync)
					this.StartTimer(this.SyncFilesAsync, 60 * this.SyncMinutes);

				if (this.IsPrepareCacheRequester)
				{
					var time = DateTime.Now.GetFirstDayOfWeek();
					if (time < DateTime.Now)
						time = time.AddDays(7);
					time = new DateTime(time.Year, time.Month, time.Day, 3, 13, 13);
					this.StartTimer(() =>
					{
						if (DateTime.Now.Day == time.Day && DateTime.Now.Hour == time.Hour && DateTime.Now.Minute > 10 && DateTime.Now.Minute < 20)
						{
							this.PrepareCachesAsync().Execute();
							time = time.AddDays(7);
						}
					}, 60 * 13);
				}

				// last action
				next?.Invoke(this);
			});
		#endregion

		public override async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			await this.WriteLogsAsync(requestInfo, $"Begin request ({requestInfo.Verb} {requestInfo.GetURI()})").ConfigureAwait(false);
			var stopwatch = Stopwatch.StartNew();
			try
			{
				// verify the request
				if (!"sync".IsEquals(requestInfo.ObjectName))
				{
					if (requestInfo.Extra == null || !requestInfo.Extra.TryGetValue("SessionID", out var sessionID) || !sessionID.Equals(requestInfo.Session.SessionID.GetHMACBLAKE256(this.ValidationKey)))
						throw new InvalidRequestException();

					if (requestInfo.Verb.IsEquals("POST") || requestInfo.Verb.IsEquals("PUT"))
					{
						if (!requestInfo.Extra.TryGetValue("Signature", out var signature) || !signature.Equals(requestInfo.Body.GetHMACSHA256(this.ValidationKey)))
							throw new InvalidRequestException();
					}
					else
					{
						requestInfo.Extra.TryGetValue("Signature", out var signature);
						if (requestInfo.TryGetParameter("x-app-token", out var appToken) && appToken != null && (signature == null || !signature.Equals(appToken.GetHMACSHA256(this.ValidationKey))))
							throw new InvalidRequestException();
					}
				}

				// process the request
				JToken json = null;
				using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.CancellationToken);
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

					case "cache":
					case "caches":
					case "preparecache":
					case "preparecaches":
						if (await this.IsSystemAdministratorAsync(requestInfo, cts.Token).ConfigureAwait(false))
							this.PrepareCachesAsync(requestInfo.CorrelationID).Execute();
						json = new JObject();
						break;

					case "sync":
						json = new JObject();
						switch (requestInfo.Verb)
						{
							case "HEAD":
								json = this.ProcessSyncProbe(requestInfo);
								break;

							case "GET":
								this.ProcessSyncRequest(requestInfo);
								break;

							case "POST":
								await this.ProcessSyncRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false);
								break;

							default:
								throw new MethodNotAllowedException(requestInfo.Verb);
						}
						break;

					default:
						json = await this.ProcessFilesAsync(requestInfo, cts.Token).ConfigureAwait(false);
						break;
				}
				stopwatch.Stop();
				await this.WriteLogsAsync(requestInfo, $"Success response - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
				if (this.IsDebugResultsEnabled)
					await this.WriteLogsAsync(requestInfo, (requestInfo.TryGetParameter("x-request", out var xrequest) ? $"- Request (Encoded): {xrequest}\r\n" : "") + $"- Request (JSON): {requestInfo.ToString(this.JsonFormat)}\r\n- Response (JSON): {json?.ToString(this.JsonFormat)}").ConfigureAwait(false);
				return json;
			}
			catch (RepositoryOperationException ex)
			{
				throw ex.InnerException is not OperationCanceledException ? this.GetRuntimeException(requestInfo, ex, stopwatch) : ex;
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

			var isForceCache = requestInfo.ContainsKey("x-force-cache");
			var isLogRequest = requestInfo.ContainsKey("x-logs");
			var isDebugLogEnabled = this.IsDebugLogEnabled || isLogRequest;
			if (isDebugLogEnabled)
				await this.WriteLogsAsync(requestInfo, $"Start to search thumbnail images ({requestInfo.GetHeaderParameter("x-origin")})\r\n- Object IDs: {objectID ?? objectIDs?.Join(", ")}\r\n- Header: {requestInfo.Header.ToJson()}\r\n- Query: {requestInfo.Query.ToJson()}\"").ConfigureAwait(false);

			// get cached
			JToken json = null;
			if (objectID != null)
			{
				var cached = isForceCache ? null : await Utility.Cache.GetAsync<string>($"{objectID}:thumbnails", cancellationToken).ConfigureAwait(false);
				json = cached?.ToJson();
			}
			else if (objectIDs != null)
			{
				var cached = isForceCache ? null : await Utility.Cache.GetAsync<string>(objectIDs.Select(id => $"{id}:thumbnails"), cancellationToken).ConfigureAwait(false);
				if (cached != null && cached.Count(kvp => !string.IsNullOrWhiteSpace(kvp.Value)).Equals(objectIDs.Length))
				{
					json = new JObject();
					cached.ForEach(kvp => json[kvp.Key.Replace(":thumbnails", "")] = kvp.Value.ToJson());
				}
			}

			// no cache => search
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
				if (this.PrepareCache)
					this.SendPrepareCacheRequests(requestInfo, thumbnails);

				// build JSON
				var asAttachments = "true".IsEquals(requestInfo.GetParameter("x-thumbnails-as-attachments") ?? requestInfo.GetParameter("x-as-attachments"));
				if (objectIDs == null)
				{
					var title = requestInfo.GetParameter("x-object-title");
					if (title != null)
						try
						{
							title = title.Url64Decode();
						}
						catch
						{
							title = null;
						}

					json = thumbnails.Select(thumbnail => thumbnail.ToJson(true, title, thumbnailJSON =>
					{
						if (asAttachments)
							thumbnailJSON["URIs"] = new JObject { { "Direct", thumbnail.GetURI(title) } };
					})).ToJArray();

					await Utility.Cache.SetAsync($"{objectID}:thumbnails", json.ToString(Formatting.None), cancellationToken).ConfigureAwait(false);
				}
				else
				{
					var titles = new JObject();
					try
					{
						titles = (requestInfo.GetParameter("x-object-title") ?? "{}").ToJson() as JObject;
					}
					catch { }

					json = this.BuildJson(thumbnails, thumbnail =>
					{
						var title = titles.Get<string>(thumbnail.ObjectID);
						if (title != null)
							try
							{
								title = title.Url64Decode();
							}
							catch
							{
								title = null;
							}
						return thumbnail.ToJson(true, title, thumbnailJSON =>
						{
							if (asAttachments)
								thumbnailJSON["URIs"] = new JObject { ["Direct"] = thumbnail.GetURI(title) };
						});
					});
					
					await (json as JObject).ForEachAsync(kvp => Utility.Cache.SetAsync($"{kvp.Key}:thumbnails", kvp.Value.ToString(Formatting.None), cancellationToken)).ConfigureAwait(false);
				}
				if (isDebugLogEnabled)
					await this.WriteLogsAsync(requestInfo, $"Thumbnail images were built ({requestInfo.GetHeaderParameter("x-origin")}) => {json}").ConfigureAwait(false);
			}
			else if (isDebugLogEnabled)
				await this.WriteLogsAsync(requestInfo, $"Cached of thumbnail images was found ({requestInfo.GetHeaderParameter("x-origin")}) => {json}").ConfigureAwait(false);

			// thumbnails of one object
			if (json is JArray)
				this.NormalizeURIs(requestInfo, json as JArray);
				
			// thumbnails of multiple objects
			else
				(json as JObject).ForEach(child => this.NormalizeURIs(requestInfo, child as JArray), cancellationToken);

			// send update mesage
			if (objectID != null)
				new UpdateMessage
				{
					Type = $"{this.ServiceName}#Thumbnail#Search",
					ExcludedDeviceID = requestInfo.Session.DeviceID,
					Data = json
				}.Send();

			// response
			if (isDebugLogEnabled)
				await this.WriteLogsAsync(requestInfo, $"Complete search for thumbnail images ({requestInfo.GetHeaderParameter("x-origin")}) => {json}").ConfigureAwait(false);

			return json;
		}

		async Task<JToken> CreateThumbnailAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// prepare
			var request = requestInfo.GetBodyExpando();
			var isCreateNew = false;
			var thumbnail = await Thumbnail.GetAsync<Thumbnail>(requestInfo.GetObjectIdentity(), cancellationToken).ConfigureAwait(false);
			if (thumbnail != null)
			{
				thumbnail.CopyFrom(request, "ID,Title,Created,CreatedID,LastModified,LastModifiedID".ToHashSet());
				thumbnail.LastModifiedID = requestInfo.Session.User.ID;
				thumbnail.LastModified = DateTime.Now;
			}
			else
			{
				thumbnail = request.Copy<Thumbnail>("Title,Created,CreatedID,LastModified,LastModifiedID".ToHashSet());
				thumbnail.ID ??= UtilityService.NewUUID;
				thumbnail.CreatedID = thumbnail.LastModifiedID = requestInfo.Session.User.ID;
				thumbnail.Created = thumbnail.LastModified = DateTime.Now;
				isCreateNew = true;
			}

			if (string.IsNullOrWhiteSpace(thumbnail.ID) || !thumbnail.ID.IsValidUUID() || !thumbnail.ID.IsEquals(requestInfo.GetObjectIdentity()))
				throw new InvalidRequestException();

			// check permissions
			var gotRights = thumbnail.IsTemporary
				? await Router.GetService(thumbnail.ServiceName).CanContributeAsync(requestInfo.Session.User, thumbnail.ObjectName, thumbnail.SystemID, thumbnail.EntityInfo, "", cancellationToken).ConfigureAwait(false)
				: await Router.GetService(thumbnail.ServiceName).CanEditAsync(requestInfo.Session.User, thumbnail.ObjectName, thumbnail.SystemID, thumbnail.EntityInfo, thumbnail.ObjectID, cancellationToken).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// update into repository
			await (isCreateNew ? Thumbnail.CreateAsync(thumbnail, cancellationToken) : Thumbnail.UpdateAsync(thumbnail, false, cancellationToken)).ConfigureAwait(false);
			await Utility.Cache.RemoveAsync($"{thumbnail.ObjectID}:thumbnails", cancellationToken).ConfigureAwait(false);

			// send update message and response
			var title = requestInfo.GetParameter("x-object-title");
			var response = thumbnail.ToJson(true, title, json =>
			{
				json["URIs"] = new JObject { ["Direct"] = thumbnail.GetURI(title) };
				if (!string.IsNullOrWhiteSpace(thumbnail.ServiceName))
				{
					json["ServiceName"] = thumbnail.ServiceName.GetCapitalizedFirstLetter();
					json["ObjectName"] = thumbnail.ObjectName.GetCapitalizedFirstLetter();
				}
			});
			new UpdateMessage
			{
				Type = $"{this.ServiceName}#Thumbnail#{(isCreateNew ? "Create" : "Update")}",
				DeviceID = "*",
				Data = response
			}.Send();
			return response;
		}

		async Task<JToken> DeleteThumbnailAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// prepare
			var thumbnail = await Thumbnail.GetAsync<Thumbnail>(requestInfo.GetObjectIdentity(), cancellationToken).ConfigureAwait(false);
			if (thumbnail == null)
			{
				var data = new JObject
				{
					["ID"] = requestInfo.GetObjectIdentity(),
					["ServiceName"] = requestInfo.ServiceName.GetCapitalizedFirstLetter(),
					["ObjectName"] = requestInfo.ObjectName.GetCapitalizedFirstLetter()
				};
				new UpdateMessage
				{
					Type = $"{this.ServiceName}#Thumbnail#Delete",
					DeviceID = requestInfo.Session.DeviceID,
					Data = data
				}.Send();
				return data;
			}

			// check
			if (!await Router.GetService(thumbnail.ServiceName).CanEditAsync(requestInfo.Session.User, thumbnail.ObjectName, thumbnail.SystemID, thumbnail.EntityInfo, thumbnail.ObjectID, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// delete & clear cache
			await Thumbnail.DeleteAsync<Thumbnail>(thumbnail.ID, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false);
			var httpKeys = (await Utility.HttpCache.GetSetMembersAsync($"{thumbnail.ObjectID}:images", cancellationToken).ConfigureAwait(false) ?? []).Concat([$"{thumbnail.ObjectID}:images"]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			await Task.WhenAll
			(
				Utility.Cache.RemoveAsync($"{thumbnail.ObjectID}:thumbnails", cancellationToken),
				Utility.HttpCache.RemoveAsync(httpKeys, cancellationToken)
			).ConfigureAwait(false);

			// send update messages
			var response = thumbnail.ToJson(false, null, json =>
			{
				json["ServiceName"] = thumbnail.ServiceName.GetCapitalizedFirstLetter();
				json["ObjectName"] = thumbnail.ObjectName.GetCapitalizedFirstLetter();
			});

			new UpdateMessage
			{
				Type = $"{this.ServiceName}#Thumbnail#Delete",
				DeviceID = "*",
				Data = response
			}.Send();

			new CommunicateMessage(this.ServiceName)
			{
				Type = "Thumbnail#Delete",
				Data = thumbnail.ToJson(false, null)
			}.Send();

			return response;
		}

		async Task<JToken> MoveThumbnailsAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var serviceName = requestInfo.GetParameter("x-service-name");
			var objectName = requestInfo.GetParameter("x-object-name");
			var systemID = requestInfo.GetParameter("x-system-id");
			var entityInfo = requestInfo.GetParameter("x-entity");
			var objectID = requestInfo.GetObjectIdentity() ?? requestInfo.GetParameter("x-object-id");

			if (!await Router.GetService(serviceName).CanEditAsync(requestInfo.Session.User, objectName, systemID, entityInfo, objectID, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// move from temporary to main directory (mark as official)
			var thumbnails = await Thumbnail.FindAsync(Filters<Thumbnail>.Equals("ObjectID", objectID), Sorts<Thumbnail>.Ascending("Filename"), 0, 1, null, cancellationToken).ConfigureAwait(false);
			return await this.MarkThumbnailsAsOfficialAsync(thumbnails, requestInfo.Session.User.ID, (requestInfo.GetParameter("x-object-title") ?? UtilityService.NewUUID).GetANSIUri(), cancellationToken).ConfigureAwait(false);
		}

		async Task<JToken> MarkThumbnailsAsOfficialAsync(List<Thumbnail> thumbnails, string userID, string objectTitle, CancellationToken cancellationToken)
		{
			var json = new JArray();
			await thumbnails.ForEachAsync(async thumbnail =>
			{
				if (thumbnail.IsTemporary)
				{
					thumbnail.IsTemporary = false;
					thumbnail.LastModified = DateTime.Now;
					thumbnail.LastModifiedID = userID;
					await Thumbnail.UpdateAsync(thumbnail, userID, cancellationToken).ConfigureAwait(false);
					await this.SendInterCommunicateMessageAsync(new CommunicateMessage(this.ServiceName)
					{
						Type = "Thumbnail#Move",
						Data = thumbnail.ToJson(false, null)
					}, cancellationToken).ConfigureAwait(false);
				}
				json.Add(thumbnail.ToJson(true, objectTitle));
			}).ConfigureAwait(false);
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
			var query = request.Get<string>("FilterBy.Query");

			if (!string.IsNullOrWhiteSpace(query))
			{
				var filter = request.Get<ExpandoObject>("FilterBy")?.ToFilterBy<Attachment>() ?? Filters<Attachment>.And();
				var (totalRecords, totalPages, pageSize, pageNumber) = request.Get<ExpandoObject>("Pagination")?.GetPagination() ?? (-1, 0, 20, 1);
				totalRecords = totalRecords > -1
					? totalRecords
					: await Attachment.CountAsync(query, filter, null, cancellationToken).ConfigureAwait(false);

				var attachments = totalRecords > 0
					? await Attachment.SearchAsync(query, filter, null, pageSize, pageNumber, null, cancellationToken).ConfigureAwait(false)
					: [];

				totalPages = (totalRecords, pageSize).GetTotalPages();
				if (totalPages > 0 && pageNumber > totalPages)
					pageNumber = totalPages;

				return new JObject
				{
					{ "FilterBy", filter.ToClientJson(query) },
					{ "SortBy", null },
					{ "Pagination", (totalRecords, totalPages, pageSize, pageNumber).GetPagination() },
					{ "Objects", attachments.Select(@object => @object.ToJson()).ToJArray() }
				};
			}

			var objectIdentity = requestInfo.GetParameter("x-object-id") ?? requestInfo.GetParameter("object-id");
			if (string.IsNullOrWhiteSpace(objectIdentity))
				throw new InvalidRequestException();

			var objectID = objectIdentity.IsValidUUID()
				? objectIdentity.ToLower()
				: null;

			var objectIDs = !objectIdentity.IsValidUUID()
				? objectIdentity.ToLower().ToArray(",", true)
				: null;

			var isForceCache = requestInfo.ContainsKey("x-force-cache");
			var isDebugLogEnabled = this.IsDebugLogEnabled || requestInfo.ContainsKey("x-logs");
			if (isDebugLogEnabled)
				await this.WriteLogsAsync(requestInfo, $"Start to search attachments ({requestInfo.GetHeaderParameter("x-origin")})\r\n- Object IDs: {objectID ?? objectIDs?.Join(", ")}\r\n- Info: {requestInfo.Header.ToJson()}").ConfigureAwait(false);

			// get cached
			JToken json = null;
			if (objectID != null)
			{
				var cached = isForceCache ? null : await Utility.Cache.GetAsync<string>($"{objectID}:attachments", cancellationToken).ConfigureAwait(false);
				json = cached?.ToJson();
			}
			else if (objectIDs != null)
			{
				var cached = isForceCache ? null : await Utility.Cache.GetAsync<string>(objectIDs.Select(id => $"{id}:attachments"), cancellationToken).ConfigureAwait(false);
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
				if (this.PrepareCache)
					this.SendPrepareCacheRequests(requestInfo, attachments.Where(attachment => attachment.ContentType.IsStartsWith("image/")));

				// build JSON
				if (objectIDs == null)
				{
					json = attachments.ToJArray(attachment => attachment.ToJson());
					await Utility.Cache.SetAsync($"{objectID}:attachments", json.ToString(Formatting.None), cancellationToken).ConfigureAwait(false);
				}
				else
				{
					json = this.BuildJson(attachments, attachment => attachment.ToJson());
					await (json as JObject).ForEachAsync(kvp => Utility.Cache.SetAsync($"{kvp.Key}:attachments", kvp.Value.ToString(Formatting.None), cancellationToken)).ConfigureAwait(false);
				}
				if (isDebugLogEnabled)
					await this.WriteLogsAsync(requestInfo, $"Attachments were searched & built ({requestInfo.GetHeaderParameter("x-origin")}) => {json}").ConfigureAwait(false);
			}
			else if (isDebugLogEnabled)
				await this.WriteLogsAsync(requestInfo, $"Cached of attachments was found ({requestInfo.GetHeaderParameter("x-origin")}) => {json}").ConfigureAwait(false);

			// send update mesage
			if (objectID != null)
				new UpdateMessage
				{
					Type = $"{this.ServiceName}#Attachments#Search",
					ExcludedDeviceID = requestInfo.Session.DeviceID,
					Data = json
				}.Send();

			// response
			if (isDebugLogEnabled)
				await this.WriteLogsAsync(requestInfo, $"Complete search for attachments ({requestInfo.GetHeaderParameter("x-origin")}) => {json}").ConfigureAwait(false);
			return json;
		}

		async Task<JToken> GetAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// prepare
			var objectIdentity = requestInfo.GetObjectIdentity();
			var objectID = !string.IsNullOrWhiteSpace(objectIdentity) && objectIdentity.IsValidUUID()
				? objectIdentity
				: requestInfo.GetParameter("x-object-id") ?? requestInfo.GetParameter("object-id") ?? requestInfo.GetParameter("attachment-id") ?? requestInfo.GetQueryParameter("id");

			// get object
			var attachment = await Attachment.GetAsync<Attachment>(objectID, cancellationToken).ConfigureAwait(false) ?? throw new InformationNotFoundException();

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
				? await Router.GetService(attachment.ServiceName).CanContributeAsync(requestInfo.Session.User, attachment.ObjectName, attachment.SystemID, attachment.EntityInfo, "", cancellationToken).ConfigureAwait(false)
				: await Router.GetService(attachment.ServiceName).CanEditAsync(requestInfo.Session.User, attachment.ObjectName, attachment.SystemID, attachment.EntityInfo, attachment.ObjectID, cancellationToken).ConfigureAwait(false);
			if (!gotRights)
				throw new AccessDeniedException();

			// create new
			attachment.CreatedID = attachment.LastModifiedID = requestInfo.Session.User.ID;
			attachment.Created = attachment.LastModified = DateTime.Now;
			await Attachment.CreateAsync(attachment, cancellationToken).ConfigureAwait(false);
			await Utility.Cache.RemoveAsync($"{attachment.ObjectID}:attachments", cancellationToken).ConfigureAwait(false);

			// send update message and response
			var response = attachment.ToJson();
			new UpdateMessage
			{
				Type = $"{this.ServiceName}#Attachment#Create",
				DeviceID = "*",
				Data = response
			}.Send();
			return response;
		}

		async Task<JToken> UpdateAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// prepare
			var attachment = await Attachment.GetAsync<Attachment>(requestInfo.GetObjectIdentity(), cancellationToken).ConfigureAwait(false) ?? throw new InformationNotFoundException();
			var request = requestInfo.GetBodyExpando();
			attachment.CopyFrom(requestInfo.GetBodyExpando(), "ID,ServiceName,ObjectName,SystemID,EntityInfo,ObjectID,Filename,Size,ContentType,DownloadTimes,IsTemporary,Created,CreatedID,LastModified,LastModifiedID".ToHashSet());

			if (!await Router.GetService(attachment.ServiceName).CanEditAsync(requestInfo.Session.User, attachment.ObjectName, attachment.SystemID, attachment.EntityInfo, attachment.ObjectID, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// update
			attachment.LastModifiedID = requestInfo.Session.User.ID;
			attachment.LastModified = DateTime.Now;
			await Attachment.UpdateAsync(attachment, false, cancellationToken).ConfigureAwait(false);
			await Utility.Cache.RemoveAsync($"{attachment.ObjectID}:attachments", cancellationToken).ConfigureAwait(false);

			// send update message and response
			var response = attachment.ToJson();
			new UpdateMessage
			{
				Type = $"{this.ServiceName}#Attachment#Update",
				DeviceID = "*",
				Data = response
			}.Send();
			return response;
		}

		async Task<JToken> DeleteAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var attachment = await Attachment.GetAsync<Attachment>(requestInfo.GetObjectIdentity(), cancellationToken).ConfigureAwait(false);

			if (attachment == null)
				throw new InformationNotFoundException();

			else if (!await Router.GetService(attachment.ServiceName).CanEditAsync(requestInfo.Session.User, attachment.ObjectName, attachment.SystemID, attachment.EntityInfo, attachment.ObjectID, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// delete
			await Attachment.DeleteAsync<Attachment>(attachment.ID, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false);
			await Utility.Cache.RemoveAsync($"{attachment.ObjectID}:attachments", cancellationToken).ConfigureAwait(false);
			if (attachment.ContentType.IsStartsWith("image/"))
			{
				var httpKeys = (await Utility.HttpCache.GetSetMembersAsync($"{attachment.ObjectID}:images", cancellationToken).ConfigureAwait(false) ?? []).Concat([$"{attachment.ObjectID}:images"]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
				await Utility.HttpCache.RemoveAsync(httpKeys, cancellationToken).ConfigureAwait(false);
			}

			// send update message and response
			var response = attachment.ToJson();
			new UpdateMessage
			{
				Type = $"{this.ServiceName}#Attachment#Delete",
				DeviceID = "*",
				Data = response
			}.Send();
			new CommunicateMessage(this.ServiceName)
			{
				Type = "Attachment#Delete",
				Data = response,
				ExcludedNodeID = this.NodeID
			}.Send();
			return response;
		}

		async Task<JToken> MoveAttachmentsAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var serviceName = requestInfo.GetParameter("x-service-name");
			var objectName = requestInfo.GetParameter("x-object-name");
			var systemID = requestInfo.GetParameter("x-system-id");
			var entityInfo = requestInfo.GetParameter("x-entity");
			var objectID = requestInfo.GetObjectIdentity() ?? requestInfo.GetParameter("x-object-id");

			if (!await Router.GetService(serviceName).CanEditAsync(requestInfo.Session.User, objectName, systemID, entityInfo, objectID, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// move from temporary to main directory (mark as official)
			var attachments = await Attachment.FindAsync(Filters<Attachment>.Equals("ObjectID", objectID), Sorts<Attachment>.Ascending("Title").ThenByAscending("Filename"), 0, 1, null, cancellationToken).ConfigureAwait(false);
			return await this.MarkAttachmentsAsOfficialAsync(attachments, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false);
		}

		async Task<JToken> MarkAttachmentsAsOfficialAsync(List<Attachment> attachments, string userID, CancellationToken cancellationToken)
		{
			var json = new JArray();
			await attachments.ForEachAsync(async attachment =>
			{
				if (attachment.IsTemporary)
				{
					attachment.IsTemporary = false;
					attachment.LastModified = DateTime.Now;
					attachment.LastModifiedID = userID;
					await Attachment.UpdateAsync(attachment, userID, cancellationToken).ConfigureAwait(false);
					new CommunicateMessage(this.ServiceName)
					{
						Type = "Attachment#Move",
						Data = attachment.ToJson(false, false)
					}.Send();
				}
				json.Add(attachment.ToJson());
			}).ConfigureAwait(false);
			return json;
		}
		#endregion

		#region Working with both thumbnails and attachment files
		Task<JToken> ProcessFilesAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			switch (requestInfo.Verb)
			{
				case "GET":
					return this.GetFilesAsync(requestInfo, cancellationToken);

				case "PATCH":
					return this.MarkFilesAsOfficialAsync(requestInfo, cancellationToken);

				case "DELETE":
					return this.DeleteFilesAsync(requestInfo, cancellationToken);

				default:
					return Task.FromException<JToken>(new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.GetURI()}]"));
			}
		}

		async Task<JToken> GetFilesAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
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
			if (!requestInfo.ContainsKey("x-force-cache"))
			{
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
						objectIDs.ForEach(id => json[id] = new JObject
						{
							{ "Thumbnails", thumbnailsJson[id] },
							{ "Attachments", attachmentsJson[id] }
						});
					}
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
				var thumbnailsTask = Thumbnail.FindAsync(thumbnailsFilter, thumbnailsSort, 0, 1, null, cancellationToken).ContinueWith(async task =>
				{
					if (objectIDs == null)
					{
						var title = requestInfo.GetParameter("x-object-title");
						thumbnailsJson = task.Result.ToJArray(thumbnail => thumbnail.ToJson(true, title));
						await Utility.Cache.SetAsync($"{objectID}:thumbnails", thumbnailsJson.ToString(Formatting.None), cancellationToken).ConfigureAwait(false);
					}
					else
					{
						var titles = new JObject();
						try
						{
							titles = (requestInfo.GetParameter("x-object-title") ?? "{}").ToJson() as JObject;
						}
						catch { }
						thumbnailsJson = this.BuildJson(task.Result, thumbnail =>
						{
							var title = titles.Get<string>(thumbnail.ObjectID);
							return thumbnail.ToJson(true, title);
						});
						await (thumbnailsJson as JObject).ForEachAsync(kvp => Utility.Cache.SetAsync($"{kvp.Key}:thumbnails", kvp.Value.ToString(Formatting.None), cancellationToken)).ConfigureAwait(false);
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
				var attachmentsTask = Attachment.FindAsync(attachmentsFilter, attachmentsSort, 0, 1, null, cancellationToken).ContinueWith(async task =>
				{
					if (objectIDs == null)
					{
						attachmentsJson = task.Result.ToJArray(attachment => attachment.ToJson());
						await Utility.Cache.SetAsync($"{objectID}:attachments", attachmentsJson.ToString(Formatting.None), cancellationToken).ConfigureAwait(false);
					}
					else
					{
						attachmentsJson = this.BuildJson(task.Result, attachment => attachment.ToJson());
						await (attachmentsJson as JObject).ForEachAsync(kvp => Utility.Cache.SetAsync($"{kvp.Key}:attachments", kvp.Value.ToString(Formatting.None), cancellationToken)).ConfigureAwait(false);
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
					objectIDs.ForEach(id => json[id] = new JObject
					{
						{ "Thumbnails", thumbnailsJson?[id] },
						{ "Attachments", attachmentsJson?[id] }
					});
				}
			}

			return json;
		}

		async Task<JToken> MarkFilesAsOfficialAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var serviceName = requestInfo.GetParameter("x-service-name");
			var objectName = requestInfo.GetParameter("x-object-name");
			var systemID = requestInfo.GetParameter("x-system-id");
			var entityInfo = requestInfo.GetParameter("x-entity");
			var objectID = requestInfo.GetParameter("x-object-id");

			if (string.IsNullOrWhiteSpace(objectID))
				throw new InvalidRequestException();
			else if (!await Router.GetService(serviceName).CanEditAsync(requestInfo.Session.User, objectName, systemID, entityInfo, objectID, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// move from temporary to main directory (mark as official)
			JToken thumbnailsJson = null, attachmentsJson = null;
			var thumbnailsTask = Thumbnail.FindAsync(Filters<Thumbnail>.Equals("ObjectID", objectID), Sorts<Thumbnail>.Ascending("Filename"), 0, 1, null, cancellationToken)
				.ContinueWith(async task => thumbnailsJson = await this.MarkThumbnailsAsOfficialAsync(task.Result, requestInfo.Session.User.ID, requestInfo.GetParameter("x-object-title"), cancellationToken).ConfigureAwait(false), TaskContinuationOptions.OnlyOnRanToCompletion);
			var attachmentsTask = Attachment.FindAsync(Filters<Attachment>.Equals("ObjectID", objectID), Sorts<Attachment>.Ascending("Title").ThenByAscending("Filename"), 0, 1, null, cancellationToken)
				.ContinueWith(async task => attachmentsJson = await this.MarkAttachmentsAsOfficialAsync(task.Result, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false), TaskContinuationOptions.OnlyOnRanToCompletion);

			await Task.WhenAll(thumbnailsTask, attachmentsTask).ConfigureAwait(false);
			return new JObject
			{
				{ "Thumbnails", thumbnailsJson },
				{ "Attachments", attachmentsJson }
			};
		}

		async Task<JToken> DeleteFilesAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			// prepare
			var serviceName = requestInfo.GetParameter("x-service-name");
			var objectName = requestInfo.GetParameter("x-object-name");
			var systemID = requestInfo.GetParameter("x-system-id");
			var entityInfo = requestInfo.GetParameter("x-entity");
			var objectID = requestInfo.GetParameter("x-object-id");

			if (string.IsNullOrWhiteSpace(objectID))
				throw new InvalidRequestException();
			if (!await Router.GetService(serviceName).CanEditAsync(requestInfo.Session.User, objectName, systemID, entityInfo, objectID, cancellationToken).ConfigureAwait(false))
				throw new AccessDeniedException();

			// get thumbnails and delete (move to trash)
			var thumbnailsTask = Thumbnail.FindAsync(Filters<Thumbnail>.And
			(
				string.IsNullOrWhiteSpace(systemID) || !systemID.IsValidUUID() ? Filters<Thumbnail>.Equals("ServiceName", serviceName) : Filters<Thumbnail>.Equals("SystemID", systemID),
				Filters<Thumbnail>.Equals("ObjectID", objectID)
			), null, 0, 1, null, cancellationToken)
			.ContinueWith(async task => await task.Result.ForEachAsync(async thumbnail =>
			{
				// delete
				await Thumbnail.DeleteAsync<Thumbnail>(thumbnail.ID, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false);

				// send update message to other nodes to update and sync
				var json = thumbnail.ToJson(false, null);
				new CommunicateMessage(this.ServiceName)
				{
					Type = "Thumbnail#Delete",
					Data = json
				}.Send();
				new UpdateMessage
				{
					Type = $"{this.ServiceName}#Thumbnail#Delete",
					DeviceID = "*",
					Data = json
				}.Send();
			}, true, false).ConfigureAwait(false), TaskContinuationOptions.OnlyOnRanToCompletion);

			// get attachments and delete (move to trash)
			var attachmentsTask = Attachment.FindAsync(Filters<Attachment>.And
			(
				string.IsNullOrWhiteSpace(systemID) || !systemID.IsValidUUID() ? Filters<Attachment>.Equals("ServiceName", serviceName) : Filters<Attachment>.Equals("SystemID", systemID),
				Filters<Attachment>.Equals("ObjectID", objectID)
			), null, 0, 1, null, cancellationToken)
			.ContinueWith(async task => await task.Result.ForEachAsync(async attachment =>
			{
				// delete
				await Attachment.DeleteAsync<Attachment>(attachment.ID, requestInfo.Session.User.ID, cancellationToken).ConfigureAwait(false);

				// send update message to other nodes to update and sync files
				var json = attachment.ToJson();
				new CommunicateMessage(this.ServiceName)
				{
					Type = "Attachment#Delete",
					Data = json
				}.Send();
				new UpdateMessage
				{
					Type = $"{this.ServiceName}#Attachment#Delete",
					DeviceID = "*",
					Data = json
				}.Send();
			}, true, false).ConfigureAwait(false), TaskContinuationOptions.OnlyOnRanToCompletion);

			// wait for all the deletion tasks completed
			await Task.WhenAll(thumbnailsTask, attachmentsTask).ConfigureAwait(false);

			// clear cache
			await Task.WhenAll
			(
				Utility.Cache.RemoveAsync($"{objectID}:attachments", cancellationToken),
				Utility.Cache.RemoveAsync($"{objectID}:thumbnails", cancellationToken),
				Utility.HttpCache.RemoveAsync($"{objectID}:images", cancellationToken)
			).ConfigureAwait(false);

			// response
			return new JObject();
		}
		#endregion

		#region Sync files
		async Task SendSyncRequestAsync(string node, string directory, string filename, string correlationID)
		{
			correlationID ??= UtilityService.NewUUID;
			try
			{
				await Router.GetUniqueService(Extensions.GetUniqueName(this.ServiceName, node)).ProcessRequestAsync(new RequestInfo
				{
					ServiceName = this.ServiceName,
					ObjectName = "Sync",
					Verb = "GET",
					Header = new Dictionary<string, string>
					{
						["x-signature"] = this.NodeID.GetHMACBLAKE512(this.ValidationKey),
						["x-node"] = this.NodeID,
						["x-directory"] = directory,
						["x-file-name"] = filename
					},
					CorrelationID = correlationID
				}, this.CancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await this.WriteLogsAsync(correlationID, this.Logger, "Cannot send a request to sync a file" + "\r\n" +
					$"- From: {this.NodeID}" + "\r\n" +
					$"- To: {node}" + "\r\n" +
					$"- File: {(directory.IsValidUUID() ? $"/{this.AttachmentsDirectory.Replace("\\", "/")}" : "")}/{directory}/{filename}"
				, ex, this.ServiceName, "Synchronizers").ConfigureAwait(false);
			}
		}

		async Task<long> SendSyncRequestsAsync(IEnumerable<string> directories, DateTime? lastWriteTime, bool asAvatars = false, Action<string> tracker = null, string nodeID = null)
		{
			long syncCounter = 0;
			await directories.ForEachAsync(async path =>
			{
				var files = (lastWriteTime != null
					? Directory.GetFiles(path, "*.*").Select(file => new FileInfo(file)).Where(file => file.LastWriteTime >= lastWriteTime.Value)
					: Directory.GetFiles(path, "*.*").Select(file => new FileInfo(file))
				).OrderByDescending(file => file.LastWriteTime).ToList();

				tracker?.Invoke($"Start to send sync requests [{path}] of {files.Count:###,###,##0} file(s)");
				var counter = 0;
				var syncFiles = files.Take(this.SyncThreads).ToList();
				while (syncFiles.Count > 0)
				{
					await syncFiles.ForEachAsync(file => new CommunicateMessage(this.ServiceName)
					{
						Type = "Sync",
						Data = new JObject
						{
							{ "Node", nodeID ?? this.NodeID },
							{ "Directory", asAvatars ? "avatars" : path.Right(32).ToLower() },
							{ "Filename", file.Name }
						}
					}.SendAsync(this.CancellationToken, false, UtilityService.GetRandomNumber(13, 31)), true, this.SyncInParallels, false, this.SyncThreads).ConfigureAwait(false);
					tracker?.Invoke($"{syncFiles.Count:###,##0} sync requests were sent [{DateTime.Now.ToDTString()}]");

					await Task.Delay(UtilityService.GetRandomNumber(789, 1234), this.CancellationToken).ConfigureAwait(false);
					counter += syncFiles.Count;
					syncFiles = files.Skip(counter).Take(this.SyncThreads).ToList();
				}

				tracker?.Invoke($"Sync requests of {counter:###,##0} files [{path}] were sent\r\n");
				syncCounter += counter;
				await Task.Delay(UtilityService.GetRandomNumber(789, 1234), this.CancellationToken).ConfigureAwait(false);
			}, true, false).ConfigureAwait(false);
			return syncCounter;
		}

		async Task SyncFilesAsync()
		{
			var lastWriteTime = DateTime.Now.AddHours(0 - this.SyncHours);
			long syncCounter = 0;
			if (Directory.Exists(this.AttachmentsDirectory))
				syncCounter += await this.SendSyncRequestsAsync(Directory.GetDirectories(this.AttachmentsDirectory).Where(dirPath => dirPath.Right(32).IsValidUUID()), lastWriteTime).ConfigureAwait(false);
			if (Directory.Exists(this.AvatarsDirectory))
				syncCounter += await this.SendSyncRequestsAsync([this.AvatarsDirectory], lastWriteTime, true).ConfigureAwait(false);
			if (syncCounter > 0)
				await this.WriteLogsAsync(UtilityService.NewUUID, $"{syncCounter:###,###,###,###,##0} files were synced", null, this.ServiceName, "Synchronizers").ConfigureAwait(false);
		}

		void ProcessSyncRequest(RequestInfo requestInfo)
		{
			var node = requestInfo.Header["x-node"];
			if (!node.GetHMACBLAKE512(this.ValidationKey).Equals(requestInfo.Header["x-signature"]))
				throw new InvalidRequestException();

			var directory = requestInfo.Header["x-directory"];
			var filename = requestInfo.Header["x-file-name"];

			var filePath = Path.Combine(directory.IsValidUUID() ? this.AttachmentsDirectory : this.AvatarsDirectory, directory.IsValidUUID() ? directory : "", filename);
			if (File.Exists(filePath))
				Task.Run(async () =>
				{
					try
					{
						var stopwatch = Stopwatch.StartNew();
						var fileInfo = new FileInfo(filePath);
						var header = new Dictionary<string, string>
						{
							["x-signature"] = this.NodeID.GetHMACBLAKE512(this.ValidationKey),
							["x-node"] = this.NodeID,
							["x-directory"] = directory,
							["x-file-name"] = filename,
							["x-file-length"] = fileInfo.Length.ToString(),
							["x-file-creation-time"] = fileInfo.CreationTime.ToDTString(),
							["x-file-last-write-time"] = fileInfo.LastWriteTime.ToDTString(),
						};

						var service = Router.GetUniqueService(Extensions.GetUniqueName(this.ServiceName, node));
						var probe = await service.ProcessRequestAsync(new RequestInfo
						{
							ServiceName = this.ServiceName,
							ObjectName = "Sync",
							Verb = "HEAD",
							Header = header,
							CorrelationID = requestInfo.CorrelationID
						}, this.CancellationToken).ConfigureAwait(false);

						if ("OK".IsEquals(probe.Get<string>("Status")))
						{
							var step = 0;
							var read = 0;
							var buffer = new byte[1024 * 64];
							using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 64, true);
							do
							{
								step++;
								read = await stream.ReadAsync(buffer, this.CancellationToken).ConfigureAwait(false);
								var body = read > 0 ? buffer.Take(0, read).ToBase64() : "";
								await service.ProcessRequestAsync(new RequestInfo
								{
									ServiceName = this.ServiceName,
									ObjectName = "Sync",
									Verb = "POST",
									Header = header,
									Body = body,
									Extra = new Dictionary<string, string>
									{
										["x-checksum"] = (string.IsNullOrWhiteSpace(body) ? $"{directory}/{filename}@{this.NodeID}" : body).GetHMACSHA256(this.SyncKey),
										["x-step"] = step.ToString()
									},
									CorrelationID = requestInfo.CorrelationID
								}, this.CancellationToken).ConfigureAwait(false);
							} while (read > 0);
							stopwatch.Stop();
							await this.WriteLogsAsync(requestInfo.CorrelationID, this.Logger, $"Sync a file successful - Execution times: {stopwatch.GetElapsedTimes()}" + "\r\n" +
								$"- From: {this.NodeID}" + "\r\n" +
								$"- To: {node}" + "\r\n" +
								$"- File: {(directory.IsValidUUID() ? $"{this.AttachmentsDirectory.Replace("\\", "/")}/" : "")}{directory}/{filename} ({fileInfo.Length:###,###,###,###,###,##0} bytes)"
							, null, this.ServiceName, "Synchronizers").ConfigureAwait(false);
						}
					}
					catch (Exception ex)
					{
						await this.WriteLogsAsync(requestInfo.CorrelationID, this.Logger, "Sync a file failed" + "\r\n" +
							$"- From: {this.NodeID}" + "\r\n" +
							$"- To: {node}" + "\r\n" +
							$"- File: {(directory.IsValidUUID() ? $"{this.AttachmentsDirectory.Replace("\\", "/")}/" : "")}{directory}/{filename}"
						, ex, this.ServiceName, "Synchronizers").ConfigureAwait(false);
					}
				}).ConfigureAwait(false);
			else
				throw new FileNotFoundException();
		}

		JObject ProcessSyncProbe(RequestInfo requestInfo)
		{
			var node = requestInfo.Header["x-node"];
			if (!node.GetHMACBLAKE512(this.ValidationKey).Equals(requestInfo.Header["x-signature"]))
				throw new InvalidRequestException();

			var directory = requestInfo.Header["x-directory"];
			var filename = requestInfo.Header["x-file-name"];
			var ok = new JObject { ["Status"] = "OK" };
			var path = Path.Combine(directory.IsValidUUID() ? this.AttachmentsDirectory : this.AvatarsDirectory, directory.IsValidUUID() ? directory : "");

			if (!Directory.Exists(path))
			{
				Directory.CreateDirectory(path);
				return ok;
			}

			var fileInfo = new FileInfo(Path.Combine(path, filename));
			if (!fileInfo.Exists)
				return ok;

			if (requestInfo.Header.TryGetValue("x-file-length", out var length) && Int64.TryParse(length, out var fileLength) && !fileLength.Equals(fileInfo.Length))
				return ok;

			if (requestInfo.Header.TryGetValue("x-file-creation-time", out var creationTime) && !creationTime.Equals(fileInfo.CreationTime.ToDTString()))
				return ok;

			if (requestInfo.Header.TryGetValue("x-file-last-write-time", out var lastwriteTime) && !lastwriteTime.Equals(fileInfo.LastWriteTime.ToDTString()))
				return ok;

			return new JObject { ["Status"] = "Cancel" };
		}

		async Task ProcessSyncRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var node = requestInfo.Header["x-node"];
			if (!node.GetHMACBLAKE512(this.ValidationKey).Equals(requestInfo.Header["x-signature"]))
				throw new InvalidRequestException();

			var directory = requestInfo.Header["x-directory"];
			var filename = requestInfo.Header["x-file-name"];

			var path = Path.Combine(directory.IsValidUUID() ? this.AttachmentsDirectory : this.AvatarsDirectory, directory.IsValidUUID() ? directory : "");
			var filePath = Path.Combine(path, filename);
			if (!Directory.Exists(path))
				Directory.CreateDirectory(path);

			try
			{
				var checksum = (string.IsNullOrWhiteSpace(requestInfo.Body) ? $"{directory}/{filename}@{node}" : requestInfo.Body).GetHMACSHA256(this.SyncKey);
				if (!requestInfo.Extra.TryGetValue("x-checksum", out var xchecksum) || !xchecksum.Equals(checksum))
					throw new InvalidDataException("Invalid checksum");

				if (!requestInfo.Extra.TryGetValue("x-step", out var step))
					step = "0";

				var buffer = string.IsNullOrWhiteSpace(requestInfo.Body) ? [] : requestInfo.Body.Base64ToBytes();
				if (buffer.Length > 0)
				{
					using var stream = new FileStream(filePath, step.Equals("1") ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1024 * 64, true);
					await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
				}
				else
				{
					if (requestInfo.Header.TryGetValue("x-file-creation-time", out var time) && DateTime.TryParse(time, out var creationTime))
						try
						{
							File.SetCreationTime(filePath, creationTime);
						}
						catch { }
					if (requestInfo.Header.TryGetValue("x-file-last-write-time", out time) && DateTime.TryParse(time, out var lastwriteTime))
						try
						{
							File.SetLastWriteTime(filePath, lastwriteTime);
						}
						catch { }
				}
			}
			catch (Exception ex)
			{
				try
				{
					File.Delete(filePath);
				}
				catch { }
				await this.WriteLogsAsync(requestInfo.CorrelationID, this.Logger, "Failed to process a request to sync a file" + "\r\n" +
					$"- From: {node}" + "\r\n" +
					$"- To: {this.NodeID}" + "\r\n" +
					$"- File: {(directory.IsValidUUID() ? $"/{this.AttachmentsDirectory.Replace("\\", "/")}" : "")}/{directory}/{filename}"
				, ex, this.ServiceName, "Synchronizers").ConfigureAwait(false);
				throw;
			}
		}
		#endregion

		#region Sync data
		public override async Task<JToken> SyncAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			var stopwatch = Stopwatch.StartNew();
			this.WriteLogs(requestInfo, $"Start sync ({requestInfo.Verb} {requestInfo.GetURI()})");
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.CancellationToken);
			try
			{
				// validate
				var json = await base.SyncAsync(requestInfo, cancellationToken).ConfigureAwait(false);

				// sync
				switch (requestInfo.ObjectName.ToLower())
				{
					case "attachment":
						json = await this.SyncAttachmentAsync(requestInfo, cts.Token).ConfigureAwait(false);
						break;

					case "thumbnail":
						json = await this.SyncThumbnailAsync(requestInfo, cts.Token).ConfigureAwait(false);
						break;

					default:
						throw new InvalidRequestException($"The request for synchronizing is invalid ({requestInfo.Verb} {requestInfo.GetURI()})");
				}

				stopwatch.Stop();
				this.WriteLogs(requestInfo, $"Sync success - Execution times: {stopwatch.GetElapsedTimes()}");
				if (this.IsDebugResultsEnabled)
					this.WriteLogs(requestInfo, $"- Request: {requestInfo.ToString(this.JsonFormat)}" + "\r\n" + $"- Response: {json?.ToString(this.JsonFormat)}");
				return json;
			}
			catch (Exception ex)
			{
				throw this.GetRuntimeException(requestInfo, ex, stopwatch);
			}
		}

		async Task<JToken> SyncAttachmentAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var data = requestInfo.GetBodyExpando();
			var attachment = await Attachment.GetAsync<Attachment>(data.Get<string>("ID"), cancellationToken).ConfigureAwait(false);
			if (attachment == null)
			{
				attachment = Attachment.CreateInstance(data);
				await Attachment.CreateAsync(attachment, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				attachment.Fill(data);
				await Attachment.UpdateAsync(attachment, true, cancellationToken).ConfigureAwait(false);
			}

			var sourceDirectory = data.Get<string>("SourceDirectory");
			if (!string.IsNullOrWhiteSpace(sourceDirectory))
				await this.SendInterCommunicateMessageAsync(new CommunicateMessage("Files")
				{
					Type = "Attachment#Copy",
					Data = attachment.ToJson(false, false, json => json["SourceDirectory"] = sourceDirectory)
				}, this.CancellationToken).ConfigureAwait(false);

			return new JObject
			{
				{ "Sync", "Success" },
				{ "ID", attachment.ID },
				{ "Type", attachment.GetTypeName(true) }
			};
		}

		async Task<JToken> SyncThumbnailAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var data = requestInfo.GetBodyExpando();
			var thumbnail = await Thumbnail.GetAsync<Thumbnail>(data.Get<string>("ID"), cancellationToken).ConfigureAwait(false);
			if (thumbnail == null)
			{
				thumbnail = Thumbnail.CreateInstance(data);
				await Thumbnail.CreateAsync(thumbnail, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				thumbnail.Fill(data);
				await Thumbnail.UpdateAsync(thumbnail, true, cancellationToken).ConfigureAwait(false);
			}

			var sourceDirectory = data.Get<string>("SourceDirectory");
			if (!string.IsNullOrWhiteSpace(sourceDirectory))
				await this.SendInterCommunicateMessageAsync(new CommunicateMessage("Files")
				{
					Type = "Thumbnail#Copy",
					Data = thumbnail.ToJson(false, null, json => json["SourceDirectory"] = sourceDirectory)
				}, this.CancellationToken).ConfigureAwait(false);

			return new JObject
			{
				{ "Sync", "Success" },
				{ "ID", thumbnail.ID },
				{ "Type", thumbnail.GetTypeName(true) }
			};
		}

		protected override Task SendSyncRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> base.SendSyncRequestAsync(requestInfo, cancellationToken);
		#endregion

		#region Prepare cache of images
		void SendPrepareCacheRequest(IAttachment attachment, bool isThumbnail, string correlationID, bool onlyWebP = true, bool writeLogs = false)
			=> new CommunicateMessage(this.ServiceName)
			{
				Type = "PrepareCache",
				Data = new JObject
				{
					{ "ID", attachment.ID },
					{ "ServiceName", attachment.ServiceName },
					{ "SystemID", attachment.SystemID },
					{ "ObjectID", attachment.ObjectID },
					{ "Filename", string.IsNullOrWhiteSpace(attachment.Filename) ? $"{attachment.ObjectID}.jpg" : attachment.Filename },
					{ "ContentType", string.IsNullOrWhiteSpace(attachment.ContentType) ? "image/jpeg" : attachment.ContentType },
					{ "X-Type", isThumbnail ? "Thumbnail" : "Attachment" },
					{ "X-Only-WebP", onlyWebP },
					{ "X-Logs", writeLogs },
					{ "X-Correlation-ID", correlationID }
				}
			}.Send();

		void SendPrepareCacheRequests(RequestInfo requestInfo, IEnumerable<IAttachment> attachments)
		{
			var isThumbnail = requestInfo.ObjectName.IsStartsWith("thumbnail");
			var writeLogs = this.IsDebugResultsEnabled || requestInfo.ContainsKey("x-logs");
			attachments.ForEach(attachment => this.SendPrepareCacheRequest(attachment, isThumbnail, requestInfo.CorrelationID, false, writeLogs));
		}

		async Task PrepareCacheAsync(string correlationID)
		{
			while (this.PrepareCacheRequests.TryDequeue(out var data))
				try
				{
					var stopwatch = Stopwatch.StartNew();
					correlationID ??= data.Get("X-Correlation-ID", UtilityService.NewUUID);
					var request = new JObject
					{
						{ "ID", data.Get<string>("ID") },
						{ "ServiceName", data.Get<string>("ServiceName") },
						{ "SystemID", data.Get<string>("SystemID") },
						{ "ObjectID", data.Get<string>("ObjectID") },
						{ "Filename", data.Get<string>("Filename") },
						{ "ContentType", data.Get<string>("ContentType") },
						{ "Type", data.Get("X-Type", "Thumbnail") }
					}.ToString(Formatting.None);
					var writeLogs = this.IsDebugResultsEnabled || data.Get("X-Logs", false);
					var uri = $"{Utility.FilesHttpURI}/prepare?x-correlation-id={correlationID}&x-node={this.NodeID}&x-signature={request.GetHMACSHA256(this.ValidationKey)}&x-request={request.Url64Encode()}{(writeLogs ? "&x-logs" : "")}{(data.Get("X-Only-WebP", false) ? "&x-only-webp" : "")}";
					await new Uri(uri).FetchHttpAsync(this.CancellationToken).ConfigureAwait(false);
					if (writeLogs)
						await this.WriteLogsAsync(correlationID, $"Send a request to prepare cache successful - Execution times: {stopwatch.GetElapsedTimes()}\r\nURI: {uri}\r\nRequest: {request}", null, this.ServiceName, "Caches").ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					await this.WriteLogsAsync(correlationID, $"Error occurred while sending a request to prepare cache => {ex.Message}", ex, this.ServiceName, "Caches").ConfigureAwait(false);
				}
		}

		async Task PrepareCachesAsync(string correlationID = null)
		{
			correlationID ??= UtilityService.NewUUID;
			var filter = Filters<Attachment>.GreaterOrEquals("Created", DateTime.Now.AddYears(-2));
			var sort = Sorts<Attachment>.Descending("Created");
			var pageSize = 20;
			var pageNumber = 0;
			var totalRecords = await Attachment.CountAsync(null, filter, null, this.CancellationToken).ConfigureAwait(false);
			var totalPages = (totalRecords, pageSize).GetTotalPages();
			await this.WriteLogsAsync(correlationID, $"Start to prepare {totalRecords:###,###,##0} cache of image(s)", null, this.ServiceName, "Caches").ConfigureAwait(false);
			while (pageNumber < totalPages)
			{
				pageNumber++;
				var attachments = await Attachment.FindAsync(filter, sort, pageSize, pageNumber, null, this.CancellationToken).ConfigureAwait(false) ?? [];
				attachments.Where(attachment => attachment.ContentType.IsStartsWith("image/")).ForEach(attachment => this.SendPrepareCacheRequest(attachment, false, correlationID), true);
			}
		}
		#endregion

		#region Helpers for working with JSON
		JObject BuildJson<T>(List<T> objects, Func<T, JObject> toJSON) where T : class
		{
			var json = new JObject();
			var objectID = "";
			JArray children = null;
			objects.ForEach(@object =>
			{
				var child = toJSON(@object);
				var objID = child.Get<string>("ObjectID");
				if (!objID.IsEquals(objectID))
				{
					if (children != null && children.Count > 0 && !string.IsNullOrWhiteSpace(objectID))
						json[objectID] = children;
					objectID = objID;
					children = [];
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
			var asPng = "true".IsEquals(requestInfo.GetParameter("x-thumbnails-as-png") ?? requestInfo.GetParameter("x-is-png"));
			if (!Int32.TryParse(requestInfo.GetParameter("x-thumbnails-width") ?? requestInfo.GetParameter("x-width"), out var width))
				width = 0;
			if (!Int32.TryParse(requestInfo.GetParameter("x-thumbnails-height") ?? requestInfo.GetParameter("x-height"), out var height))
				height = 0;

			// normalize URIs
			thumbnails.ForEach(thumbnail =>
			{
				var uri = thumbnail.Get<string>("URI");
				if (!string.IsNullOrWhiteSpace(uri))
				{
					if (asPng)
						uri = uri.Replace("/thumbnails/", "/thumbnailpngs/");

					if (width != 0 || height != 0)
					{
						uri = uri.Replace("/0/0/0/", $"/0/{width}/{height}/");
						uri = uri.Replace("/1/0/0/", $"/1/{width}/{height}/");
					}

					uri += asPng ? ".png" : ".jpg";

					thumbnail["URI"] = uri;
					if (thumbnail["URIs"] != null)
						thumbnail.Get<JObject>("URIs")["Direct"] = uri;
				}
			});
			return thumbnails;
		}
		#endregion

		#region Process inter-communicate messages
		protected override async Task ProcessInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default)
		{
			var correlationID = message.Data.Get<string>("CorrelationID") ?? message.Data.Get("X-Correlation-ID", UtilityService.NewUUID);
			var writeLogs = this.IsDebugResultsEnabled || message.Data.Get("X-Logs", false);
			if (message.Type.IsEquals("Thumbnail#Rebuild"))
				try
				{
					var objectID = message.Data.Get<string>("ObjectID");
					var thumbnail = "Not-Existed".IsEquals(message.Data.Get<string>("Filename")) ? await Thumbnail.GetAsync<Thumbnail>(message.Data.Get<string>("ID"), this.CancellationToken).ConfigureAwait(false) : null;
					if (thumbnail != null)
						await Thumbnail.DeleteAsync<Thumbnail>(thumbnail.ID, null, this.CancellationToken).ConfigureAwait(false);
					else
					{
						var thumbnails = await Thumbnail.FindAsync(Filters<Thumbnail>.Equals("ObjectID", objectID), null, 0, 1, null, cancellationToken).ConfigureAwait(false);
						thumbnail = thumbnails.FirstOrDefault();
						if (thumbnail == null)
						{
							thumbnail = message.Data.Copy<Thumbnail>("Title,Created,CreatedID,LastModified,LastModifiedID".ToHashSet());
							thumbnail.ID ??= UtilityService.NewUUID;
						}
						thumbnail.CreatedID = thumbnail.LastModifiedID = message.Data.Get<string>("LastModifiedID");
						thumbnail.Created = thumbnail.LastModified = message.Data.Get<DateTime>("LastModified");
						await (thumbnails.Count < 1 ? Thumbnail.CreateAsync(thumbnail, cancellationToken) : Thumbnail.UpdateAsync(thumbnail, true, cancellationToken)).ConfigureAwait(false);
						this.Logger.LogInformation($"Rebuild thumbnail image info successful => {thumbnail.Filename}");
					}
					await Utility.Cache.RemoveAsync($"{objectID}:thumbnails", cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					await this.WriteLogsAsync(correlationID, $"Error occurred while rebuilding thumbnail image info [{message.Data.Get<string>("ObjectID")}] => {ex.Message}", ex, this.ServiceName, "Thumbnails").ConfigureAwait(false);
				}

			else if (message.Type.IsEquals("ClearCache"))
				try
				{
					var objectID = message.Data.Get<string>("ObjectID");
					var cacheKeys = (await Utility.Cache.GetSetMembersAsync($"{objectID}:thumbnails", cancellationToken).ConfigureAwait(false) ?? []).ToList();
					cacheKeys = (await Utility.Cache.GetSetMembersAsync($"{objectID}:attachments", cancellationToken).ConfigureAwait(false) ?? []).Concat([.. cacheKeys, $"{objectID}:thumbnails", $"{objectID}:attachments"]).ToList();
					await Utility.Cache.RemoveAsync(cacheKeys, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					await this.WriteLogsAsync(correlationID, $"Error occurred while clear cache [{message.Data.Get<string>("ObjectID")}] => {ex.Message}", ex, this.ServiceName).ConfigureAwait(false);
				}

			else if (message.Type.IsEquals("PrepareCache"))
			{
				if (this.IsPrepareCacheRequester)
				{
					this.PrepareCacheRequests.Enqueue(message.Data as JObject);
					var numberOfMessagesInQueue = this.PrepareCacheRequests.Count;
					if (!this.IsPreparingCache)
					{
						this.IsPreparingCache = true;
						if (writeLogs)
							await this.WriteLogsAsync(correlationID, $"Send {numberOfMessagesInQueue} request(s) to prepare cache", null, this.ServiceName, "Caches").ConfigureAwait(false);
						await this.PrepareCacheAsync(correlationID).ConfigureAwait(false);
						this.IsPreparingCache = false;
					}
					else if (writeLogs)
						await this.WriteLogsAsync(correlationID, $"Update message to prepare cache successful [{numberOfMessagesInQueue}] => {message.Data}", null, this.ServiceName, "Caches").ConfigureAwait(false);
				}
			}

			else if (message.Type.IsEquals("Sync"))
			{
				var node = message.Data.Get<string>("Node");
				if (!this.NodeID.IsEquals(node))
				{
					var directory = message.Data.Get<string>("Directory");
					var filename = message.Data.Get<string>("Filename");
					if (directory.IsValidUUID() ? Directory.Exists(this.AttachmentsDirectory) : Directory.Exists(this.AvatarsDirectory))
						this.SendSyncRequestAsync(node, directory, filename, correlationID).Execute();
				}
			}
		}
		#endregion

		#region Does synchronous work (send request to sync files)
		static List<string> Directories => ["RecycleBin", "Temporary", "Trash"];

		public override void DoWork(string[] args = null)
		{
			var isRefineDirectories = args?.FirstOrDefault(arg => arg.IsEquals("/rename")) != null || args?.FirstOrDefault(arg => arg.IsEquals("/refine")) != null;
			var isSyncFiles = args?.FirstOrDefault(arg => arg.IsEquals("/sync")) != null;
			var isAvatars = isSyncFiles && args?.FirstOrDefault(arg => arg.IsEquals("/avatar")) != null;

			var dir = args?.FirstOrDefault(arg => arg.IsStartsWith("/dir:"))?[5..].Trim().ToLower() ?? (isAvatars ? this.AvatarsDirectory : this.AttachmentsDirectory);
			if ((isRefineDirectories || isSyncFiles) && !Directory.Exists(dir))
			{
				this.Logger.LogInformation($"Directory is not existed => {dir}");
				return;
			}

			var systemID = args?.FirstOrDefault(arg => arg.IsStartsWith("/system:"))?[8..].Trim().ToLower() ?? "all";
			var directories = (isRefineDirectories || isSyncFiles)
				? isAvatars
					? [dir]
					: systemID.IsEquals("all")
						? Directory.GetDirectories(dir).Where(dirPath => dirPath.Right(32).IsValidUUID()).ToList()
						: [Path.Combine(dir, systemID)]
				: [];

			if (isRefineDirectories || isSyncFiles)
			{
				directories = [.. directories.Where(dirPath => Directory.Exists(dirPath))];
				directories = args?.FirstOrDefault(arg => arg.IsStartsWith("/reverse")) != null
					? [.. directories.OrderDescending()]
					: [.. directories.Order()];
			}

			if (isRefineDirectories)
				directories.ForEach(dirPath =>
				{
					Directories.ForEach(name =>
					{
						try
						{
							Directory.Delete(Path.Combine(dirPath, name), true);
						}
						catch { }
					});
					try
					{
						Directory.Move(dirPath, dirPath.ToLower());
					}
					catch { }
				});

			if (isSyncFiles)
			{
				var lastWriteTime = args?.FirstOrDefault(arg => arg.IsStartsWith("/month:")) != null
					? Int32.TryParse(args.First(arg => arg.IsStartsWith("/month:"))[7..].Trim(), out var month)
						? DateTime.Now.AddMonths(0 - month) as DateTime?
						: null
					: null;
				var nodeID = args?.FirstOrDefault(arg => arg.IsStartsWith("/node:"))?[6..].Trim();
				var syncTask = this.SendSyncRequestsAsync(directories, lastWriteTime, isAvatars, message => this.Logger.LogInformation(message), nodeID);
				syncTask.Execute(true);
				this.Logger.LogInformation($"============================\r\n{syncTask.Result:###,###,###,###,##0} files were synced\r\n============================");
			}
		}
		#endregion

	}
}