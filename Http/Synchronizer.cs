#region Related components
using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	public class Synchronizer : IUniqueService
	{
		public ILogger Logger { get; } = Components.Utility.Logger.CreateLogger<Synchronizer>();

		public string ServiceUniqueName => Extensions.GetUniqueName($"{Global.ServiceName}.http");

		public string ServiceUniqueURI => $"services.{this.ServiceUniqueName}";

		internal string SyncKey { get; } = UtilityService.GetAppSetting("Keys:Sync", "VIEApps-FD2CD7FA-NGX-40DE-Services-401D-Sync-93D9-Key-A47006F07048");

		public async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			switch (requestInfo.Verb)
			{
				case "GET":
					this.ProcessSyncRequest(requestInfo);
					break;

				case "POST":
					await this.ProcessSyncRequestAsync(requestInfo, cancellationToken).ConfigureAwait(false);
					break;

				default:
					throw new InvalidRequestException();
			}
			return new JObject();
		}

		internal async Task SendSyncRequestAsync(string node, string serviceName, string systemID, string filename, bool isTemporary, bool isAvatar, string correlationID)
		{
			correlationID ??= UtilityService.NewUUID;
			try
			{
				await Router.GetUniqueService(Extensions.GetUniqueName($"{Global.ServiceName}.http", node)).ProcessRequestAsync(new RequestInfo
				{
					ServiceName = Global.ServiceName,
					ObjectName = "Synchronizer",
					Verb = "GET",
					Header = new Dictionary<string, string>
					{
						["x-node"] = Global.NodeID,
						["x-node-signature"] = Global.NodeID.GetHMACSHA256(this.SyncKey),
						["x-service-name"] = serviceName,
						["x-system-id"] = systemID,
						["x-filename"] = filename,
						["x-temporary"] = isTemporary.ToString().ToLower(),
						["x-avatar"] = isAvatar.ToString().ToLower()
					},
					CorrelationID = correlationID
				}, Global.CancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await Global.WriteLogsAsync(this.Logger, "Synchronizers", "Cannot send a request to sync a file" + "\r\n" +
					$"- From: {Global.NodeID}" + "\r\n" +
					$"- To: {node}" + "\r\n" +
					$"- Service: {serviceName}" + "\r\n" +
					$"- System ID: {systemID}" + "\r\n" +
					$"- File: {filename}" + "\r\n" +
					$"- Temporary: {isTemporary}" + "\r\n" +
					$"- Avatar: {isAvatar}"
				, ex, Global.ServiceName, LogLevel.Error, correlationID).ConfigureAwait(false);
			}
		}

		void ProcessSyncRequest(RequestInfo requestInfo)
		{
			var node = requestInfo.Header["x-node"];
			if (!node.GetHMACSHA256(this.SyncKey).IsEquals(requestInfo.Header["x-node-signature"]))
				throw new InvalidRequestException();

			var serviceName = requestInfo.Header["x-service-name"];
			var systemID = requestInfo.Header["x-system-id"];
			var filename = requestInfo.Header["x-filename"];
			var isTemporary = "true".IsEquals(requestInfo.Header["x-temporary"]);
			var isAvatar = "true".IsEquals(requestInfo.Header["x-avatar"]);

			var directory = (string.IsNullOrWhiteSpace(systemID) || !systemID.IsValidUUID() ? serviceName : systemID).Trim().ToLower();
			var filePath = isAvatar
				? Path.Combine(Handler.UserAvatarFilesPath, filename)
				:	isTemporary
					? Path.Combine(Handler.TempFilesPath, filename)
					: Path.Combine(Handler.AttachmentFilesPath, directory, filename);

			async Task syncFile()
			{
				try
				{
					var stopwatch = Stopwatch.StartNew();
					var fileInfo = new FileInfo(filePath);
					var header = new Dictionary<string, string>
					{
						["x-node"] = Global.NodeID,
						["x-node-signature"] = Global.NodeID.GetHMACSHA256(this.SyncKey),
						["x-service-name"] = serviceName,
						["x-system-id"] = systemID,
						["x-filename"] = filename,
						["x-temporary"] = isTemporary.ToString().ToLower(),
						["x-avatar"] = isAvatar.ToString().ToLower(),
						["x-creation-time"] = fileInfo.CreationTime.ToDTString(),
						["x-last-write-time"] = fileInfo.LastWriteTime.ToDTString()
					};
					var service = Router.GetUniqueService(Extensions.GetUniqueName($"{Global.ServiceName}.http", node));
					using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true);
					var buffer = new byte[AspNetCoreUtilityService.BufferSize];
					var read = 0;
					var step = 0;
					do
					{
						step++;
						read = await stream.ReadAsync(buffer, Global.CancellationToken).ConfigureAwait(false);
						var body = read > 0 ? buffer.Take(0, read).ToBase64() : "";
						await service.ProcessRequestAsync(new RequestInfo
						{
							ServiceName = Global.ServiceName,
							ObjectName = "Synchronizer",
							Verb = "POST",
							Header = header,
							Body = body,
							Extra = new Dictionary<string, string>
							{
								["x-checksum"] = (body != "" ? body : $"{directory}/{filename}@{Global.NodeID}").GetHMACSHA256(this.SyncKey),
								["x-step"] = step.ToString()
							},
							CorrelationID = requestInfo.CorrelationID
						}, Global.CancellationToken).ConfigureAwait(false);
					} while (read > 0);
					stopwatch.Stop();
					await Global.WriteLogsAsync(this.Logger, "Synchronizers", $"Sync a file successful - Execution times: {stopwatch.GetElapsedTimes()}" + "\r\n" +
						$"- From: {Global.NodeID}" + "\r\n" +
						$"- To: {node}" + "\r\n" +
						$"- Service: {serviceName}" + "\r\n" +
						$"- System ID: {systemID}" + "\r\n" +
						$"- File: {filePath} ({fileInfo.Length:###,###,###,###,###,##0} bytes)"
					, null, Global.ServiceName, LogLevel.Information, requestInfo.CorrelationID).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					await Global.WriteLogsAsync(this.Logger, "Synchronizers", "Sync a file failed" + "\r\n" +
						$"- From: {Global.NodeID}" + "\r\n" +
						$"- To: {node}" + "\r\n" +
						$"- Service: {serviceName}" + "\r\n" +
						$"- System ID: {systemID}" + "\r\n" +
						$"- File: {filePath}"
					, ex, Global.ServiceName, LogLevel.Error, requestInfo.CorrelationID).ConfigureAwait(false);
				}
			}

			if (File.Exists(filePath))
			{
				requestInfo.SendSessionState(systemID);
				syncFile().Run();
			}
			else
				throw new FileNotFoundException();
		}

		async Task ProcessSyncRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var node = requestInfo.Header["x-node"];
			if (!node.GetHMACSHA256(this.SyncKey).IsEquals(requestInfo.Header["x-node-signature"]))
				throw new InvalidRequestException();

			var serviceName = requestInfo.Header["x-service-name"];
			var systemID = requestInfo.Header["x-system-id"];
			var filename = requestInfo.Header["x-filename"];
			var isTemporary = "true".IsEquals(requestInfo.Header["x-temporary"]);
			var isAvatar = "true".IsEquals(requestInfo.Header["x-avatar"]);

			requestInfo.SendSessionState(systemID);

			var directory = (string.IsNullOrWhiteSpace(systemID) || !systemID.IsValidUUID() ? serviceName : systemID).Trim().ToLower();
			var path = isAvatar
				? Handler.UserAvatarFilesPath
				: isTemporary
					? Handler.TempFilesPath
					: Path.Combine(Handler.AttachmentFilesPath, directory);

			var filePath = Path.Combine(path, filename);
			if (!isTemporary && !Directory.Exists(path))
				Directory.CreateDirectory(path);

			try
			{
				var buffer = string.IsNullOrWhiteSpace(requestInfo.Body) ? null : requestInfo.Body.Base64ToBytes();
				var checksum = (string.IsNullOrWhiteSpace(requestInfo.Body) ? $"{directory}/{filename}@{node}" : requestInfo.Body).GetHMACSHA256(this.SyncKey);

				if (!requestInfo.Extra.TryGetValue("x-checksum", out var xchecksum) || !xchecksum.Equals(checksum))
					throw new InvalidDataException("Invalid checksum");

				if (!requestInfo.Extra.TryGetValue("x-step", out var step))
					step = "0";

				if (buffer != null)
				{
					using var stream = new FileStream(filePath, step.Equals("1") ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true);
					await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
				}
				else
				{
					if (requestInfo.Header.TryGetValue("x-creation-time", out var time) && DateTime.TryParse(time, out var creationTime))
						try
						{
							File.SetCreationTime(filePath, creationTime);
						}
						catch { }
					if (requestInfo.Header.TryGetValue("x-last-write-time", out time) && DateTime.TryParse(time, out var lastwriteTime))
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
				await Global.WriteLogsAsync(this.Logger, "Synchronizers", "Failed to process a sync request" + "\r\n" +
					$"- From: {node}" + "\r\n" +
					$"- To: {Global.NodeID}" + "\r\n" +
					$"- Service: {serviceName}" + "\r\n" +
					$"- System ID: {systemID}" + "\r\n" +
					$"- File: {filePath}"
				, ex, Global.ServiceName, LogLevel.Error, requestInfo.CorrelationID).ConfigureAwait(false);
				throw;
			}
		}

		public Task<JToken> ProcessWebHookMessageAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> Task.FromResult<JToken>(null);

		public Task<JToken> FetchTemporaryFileAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> requestInfo.FetchTemporaryFileAsync(cancellationToken);

		public ValueTask DisposeAsync()
		{
			GC.SuppressFinalize(this);
			return new(Task.CompletedTask);
		}

		public void Dispose()
			=> this.DisposeAsync().Run(true);
	}
}