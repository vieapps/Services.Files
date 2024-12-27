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

		public string ServiceUniqueURI => Extensions.GetUniqueName($"services.{Global.ServiceName}.http");

		internal string SyncKey => UtilityService.GetAppSetting("Keys:Sync", "VIEApps-FD2CD7FA-NGX-40DE-Services-401D-Sync-93D9-Key-A47006F07048");

		public async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			if (requestInfo.Header.TryGetValue("x-signature", out var signature) && signature.Equals(this.SyncKey.GetHMACBLAKE512(Global.ValidationKey)))
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
			else
				throw new InvalidRequestException();
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
						["x-signature"] = this.SyncKey.GetHMACBLAKE512(Global.ValidationKey),
						["x-node"] = Global.NodeID,
						["x-service-name"] = serviceName,
						["x-system-id"] = systemID,
						["x-filename"] = filename,
						["x-temporary"] = isTemporary.ToString().ToLower(),
						["x-avatar"] = isAvatar.ToString().ToLower()
					},
					CorrelationID = correlationID
				}, Global.CancellationToken).ConfigureAwait(false);
				await Global.WriteLogsAsync(this.Logger, "Synchronizers", $"Send a request to sync a file (via HTTP)" + "\r\n" +
					$"- From: {Global.NodeID}" + "\r\n" +
					$"- To: {node}" + "\r\n" +
					$"- Service: {serviceName}" + "\r\n" +
					$"- System ID: {systemID}" + "\r\n" +
					$"- File: {filename}" + "\r\n" +
					$"- Temporary: {isTemporary}" + "\r\n" +
					$"- Avatar: {isAvatar}"
				, null, Global.ServiceName, LogLevel.Information, correlationID).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await Global.WriteLogsAsync(this.Logger, "Synchronizers", "Cannot send a request to sync a file (via HTTP)" + "\r\n" +
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
			var serviceName = requestInfo.Header["x-service-name"];
			var systemID = requestInfo.Header["x-system-id"];
			var filename = requestInfo.Header["x-filename"];
			var isTemporary = "true".IsEquals(requestInfo.Header["x-temporary"]);
			var isAvatar = "true".IsEquals(requestInfo.Header["x-avatar"]);
			var filePath = isAvatar
				? Path.Combine(Handler.UserAvatarFilesPath, filename)
				:	isTemporary
					? Path.Combine(Handler.TempFilesPath, filename)
					: Path.Combine(Handler.AttachmentFilesPath, string.IsNullOrWhiteSpace(systemID) || !systemID.IsValidUUID() ? serviceName.ToLower() : systemID.ToLower(), filename);
			if (File.Exists(filePath))
				Task.Run(async () =>
				{
					try
					{
						var stopwatch = Stopwatch.StartNew();
						var header = new Dictionary<string, string>
						{
							["x-signature"] = this.SyncKey.GetHMACBLAKE512(Global.ValidationKey),
							["x-node"] = Global.NodeID,
							["x-service-name"] = serviceName,
							["x-system-id"] = systemID,
							["x-filename"] = filename,
							["x-temporary"] = isTemporary.ToString().ToLower(),
							["x-avatar"] = isAvatar.ToString().ToLower()
						};
						var service = Router.GetUniqueService(Extensions.GetUniqueName($"{Global.ServiceName}.http", node));
						using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true);
						var buffer = new byte[AspNetCoreUtilityService.BufferSize * 10];
						var read = 0;
						do
						{
							read = await stream.ReadAsync(buffer, Global.CancellationToken).ConfigureAwait(false);
							var data = read > 0 ? buffer.Take(0, read) : [];
							if (read < 1)
							{
								header["x-creation-time"] = File.GetCreationTime(filePath).ToDTString();
								header["x-last-write-time"] = File.GetLastWriteTime(filePath).ToDTString();
							}
							await service.ProcessRequestAsync(new RequestInfo
							{
								ServiceName = Global.ServiceName,
								ObjectName = "Synchronizer",
								Verb = "POST",
								Header = header,
								Body = data.Length > 0 ? data.ToBase64() : "",
								Extra = new Dictionary<string, string>
								{
									["x-checksum"] = data.Length > 0 ? data.GetCheckSum().GetHMACHash(this.SyncKey.ToBytes()).ToHex() : $"{filename}@{Global.NodeID}".GetHMACSHA256(this.SyncKey)
								},
								CorrelationID = requestInfo.CorrelationID
							}, Global.CancellationToken).ConfigureAwait(false);
						} while (read > 0);
						stopwatch.Stop();
						await Global.WriteLogsAsync(this.Logger, "Synchronizers", $"Sync a file (via HTTP) successful - Execution times: {stopwatch.GetElapsedTimes()}" + "\r\n" +
							$"- From: {Global.NodeID}" + "\r\n" +
							$"- To: {node}" + "\r\n" +
							$"- Service: {serviceName}" + "\r\n" +
							$"- System ID: {systemID}" + "\r\n" +
							$"- File: {filename} ({filePath} - {new FileInfo(filePath).Length:###,###,###,###,###,##0} bytes)"
						, null, Global.ServiceName, LogLevel.Information, requestInfo.CorrelationID).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						await Global.WriteLogsAsync(this.Logger, "Synchronizers", "Sync a file (via HTTP) failed" + "\r\n" +
							$"- From: {Global.NodeID}" + "\r\n" +
							$"- To: {node}" + "\r\n" +
							$"- Service: {serviceName}" + "\r\n" +
							$"- System ID: {systemID}" + "\r\n" +
							$"- File: {filename} ({filePath})"
						, ex, Global.ServiceName, LogLevel.Error, requestInfo.CorrelationID).ConfigureAwait(false);
					}
				}).ConfigureAwait(false);
			else
				throw new FileNotFoundException();
		}

		async Task ProcessSyncRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var node = requestInfo.Header["x-node"];
			var serviceName = requestInfo.Header["x-service-name"];
			var systemID = requestInfo.Header["x-system-id"];
			var fileName = requestInfo.Header["x-filename"];
			var isTemporary = "true".IsEquals(requestInfo.Header["x-temporary"]);
			var isAvatar = "true".IsEquals(requestInfo.Header["x-avatar"]);

			var path = isAvatar
				? Handler.UserAvatarFilesPath
				: isTemporary
					? Handler.TempFilesPath
					: Path.Combine(Handler.AttachmentFilesPath, string.IsNullOrWhiteSpace(systemID) || !systemID.IsValidUUID() ? serviceName.ToLower() : systemID.ToLower());

			var filePath = Path.Combine(path, fileName);
			if (!isTemporary && !Directory.Exists(path))
				Directory.CreateDirectory(path);

			try
			{
				var data = Array.Empty<byte>();
				var checksum = "";
				if (!string.IsNullOrWhiteSpace(requestInfo.Body))
				{
					data = requestInfo.Body.Base64ToBytes();
					checksum = data.GetCheckSum().GetHMACHash(this.SyncKey.ToBytes()).ToHex();
				}
				else
					checksum = $"{fileName}@{node}".GetHMACSHA256(this.SyncKey);

				if (!requestInfo.Extra.TryGetValue("x-checksum", out var xchecksum) || !xchecksum.Equals(checksum))
					throw new InvalidDataException("Invalid checksum");

				if (data.Length > 0)
					using (var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true))
					{
						await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
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
				await Global.WriteLogsAsync(this.Logger, "Synchronizers", "Failed to process a sync request (via HTTP)" + "\r\n" +
					$"- From: {node}" + "\r\n" +
					$"- To: {Global.NodeID}" + "\r\n" +
					$"- Service: {serviceName}" + "\r\n" +
					$"- System ID: {systemID}" + "\r\n" +
					$"- File: {fileName} ({filePath})"
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