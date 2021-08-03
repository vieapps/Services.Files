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

		public string ServiceUniqueName => Handler.NodeName;

		public string ServiceUniqueURI => $"services.{Handler.NodeName}";

		public string SyncKey => UtilityService.GetAppSetting("Sync", "VIEApps-FD2CD7FA-NGX-40DE-Services-401D-Sync-93D9-Key-A47006F07048");

		public async Task SendRequestAsync(string node, string serviceName, string systemID, string filename, bool isTemporary)
		{
			var correlationID = UtilityService.NewUUID;
			try
			{
				await Router.GetUniqueService(node).ProcessRequestAsync(new RequestInfo
				{
					ServiceName = "Files.Http",
					Verb = "GET",
					Header = new Dictionary<string, string>
					{
						["x-signature"] = this.SyncKey.GetHMACBLAKE512(Global.ValidationKey),
						["x-node"] = Handler.NodeName,
						["x-service-name"] = serviceName,
						["x-system-id"] = systemID,
						["x-filename"] = filename,
						["x-temporary"] = isTemporary.ToString().ToLower()
					},
					CorrelationID = correlationID
				}, Global.CancellationTokenSource.Token).ConfigureAwait(false);
				if (Global.IsDebugLogEnabled)
					await Global.WriteLogsAsync(this.Logger, "Http.Synchronizers", "Send a request to sync successful" + "\r\n" +
						$"- From: {Handler.NodeName}" + "\r\n" +
						$"- To: {node}" + "\r\n" +
						$"- Service: {serviceName}" + "\r\n" +
						$"- System ID: {systemID}" + "\r\n" +
						$"- File: {filename}" + "\r\n" +
						$"- Temporary: {isTemporary}"
					, null, Global.ServiceName, LogLevel.Debug, correlationID).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await Global.WriteLogsAsync(this.Logger, "Http.Synchronizers", "Send a request to sync failed" + "\r\n" +
					$"- From: {Handler.NodeName}" + "\r\n" +
					$"- To: {node}" + "\r\n" +
					$"- Service: {serviceName}" + "\r\n" +
					$"- System ID: {systemID}" + "\r\n" +
					$"- File: {filename}" + "\r\n" +
					$"- Temporary: {isTemporary}"
				, ex, Global.ServiceName, LogLevel.Error, correlationID).ConfigureAwait(false);
			}
		}

		public void SendRequest(string node, string serviceName, string systemID, string filename, bool isTemporary)
			=> Task.Run(() => this.SendRequestAsync(node, serviceName, systemID, filename, isTemporary)).ConfigureAwait(false);

		public async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
		{
			if (requestInfo.Header.TryGetValue("x-signature", out var signature) && signature.Equals(this.SyncKey.GetHMACBLAKE512(Global.ValidationKey)))
				switch (requestInfo.Verb)
				{
					case "GET":
						this.Send(requestInfo);
						break;

					case "POST":
						await this.ReceiveAsync(requestInfo, cancellationToken).ConfigureAwait(false);
						break;

					default:
						throw new InvalidRequestException();
				}
			else
				throw new InvalidRequestException();
			return new JObject();
		}

		void Send(RequestInfo requestInfo)
		{
			var node = requestInfo.Header["x-node"];
			var serviceName = requestInfo.Header["x-service-name"];
			var systemID = requestInfo.Header["x-system-id"];
			var filename = requestInfo.Header["x-filename"];
			var isTemporary = "true".IsEquals(requestInfo.Header["x-temporary"]);
			var filePath = isTemporary
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
							["x-node"] = Handler.NodeName,
							["x-service-name"] = serviceName,
							["x-system-id"] = systemID,
							["x-filename"] = filename,
							["x-temporary"] = isTemporary.ToString().ToLower()
						};
						var service = Router.GetUniqueService(node);
						using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true))
						{
							var buffer = new byte[AspNetCoreUtilityService.BufferSize * 10];
							var read = 0;
							do
							{
								read = await stream.ReadAsync(buffer, 0, buffer.Length, Global.CancellationTokenSource.Token).ConfigureAwait(false);
								var data = read > 0 ? buffer.Take(0, read) : new byte[0];
								await service.ProcessRequestAsync(new RequestInfo
								{
									ServiceName = "Files.Http",
									Verb = "POST",
									Header = header,
									Body = data.Length > 0 ? data.ToBase64() : "",
									Extra = new Dictionary<string, string>
									{
										["x-checksum"] = data.Length > 0 ? data.GetCheckSum().GetHMACHash(this.SyncKey.ToBytes()).ToHex() : $"{filename}@{Handler.NodeName}".GetHMACSHA256(this.SyncKey)
									},
									CorrelationID = requestInfo.CorrelationID
								}, Global.CancellationTokenSource.Token).ConfigureAwait(false);
							} while (read > 0);
							stopwatch.Stop();
							if (Global.IsDebugLogEnabled)
								await Global.WriteLogsAsync(this.Logger, "Http.Synchronizers", $"Sync a file successful - Execution times: {stopwatch.GetElapsedTimes()}" + "\r\n" +
									$"- From: {Handler.NodeName}" + "\r\n" +
									$"- To: {node}" + "\r\n" +
									$"- Service: {serviceName}" + "\r\n" +
									$"- System ID: {systemID}" + "\r\n" +
									$"- File: {filename} ({filePath} - {new FileInfo(filePath).Length:###,###,###,###,##0} bytes)"
								, null, Global.ServiceName, LogLevel.Debug, requestInfo.CorrelationID).ConfigureAwait(false);
						}
					}
					catch (Exception ex)
					{
						await Global.WriteLogsAsync(this.Logger, "Http.Synchronizers", "Sync a file failed" + "\r\n" +
							$"- From: {Handler.NodeName}" + "\r\n" +
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

		async Task ReceiveAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var node = requestInfo.Header["x-node"];
			var serviceName = requestInfo.Header["x-service-name"];
			var systemID = requestInfo.Header["x-system-id"];
			var fileName = requestInfo.Header["x-filename"];
			var isTemporary = "true".IsEquals(requestInfo.Header["x-temporary"]);

			var path = isTemporary
				? Handler.TempFilesPath
				: Path.Combine(Handler.AttachmentFilesPath, string.IsNullOrWhiteSpace(systemID) || !systemID.IsValidUUID() ? serviceName.ToLower() : systemID.ToLower());
			if (!isTemporary && !Directory.Exists(path))
				Directory.CreateDirectory(path);

			var filePath = Path.Combine(path, fileName);
			try
			{
				var data = new byte[0];
				var checksum = "";
				if (!string.IsNullOrWhiteSpace(requestInfo.Body))
				{
					data = requestInfo.Body.Base64ToBytes();
					checksum = data.GetCheckSum().GetHMACHash(this.SyncKey.ToBytes()).ToHex();
				}
				else
					checksum = $"{fileName}@{node}".GetHMACSHA256(this.SyncKey);

				if (!requestInfo.Extra.TryGetValue("x-checksum", out var xchecksum) || !xchecksum.Equals(checksum))
					throw new InvalidDataException();

				if (data.Length > 0)
					using (var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true))
					{
						await stream.WriteAsync(data, 0, data.Length, cancellationToken).ConfigureAwait(false);
					}
				else if (Global.IsDebugLogEnabled)
					await Global.WriteLogsAsync(this.Logger, "Http.Synchronizers", "Sync a file successful" + "\r\n" +
						$"- From: {node}" + "\r\n" +
						$"- To: {Handler.NodeName}" + "\r\n" +
						$"- Service: {serviceName}" + "\r\n" +
						$"- System ID: {systemID}" + "\r\n" +
						$"- File: {fileName} ({filePath} - {new FileInfo(filePath).Length:###,###,###,###,##0} bytes)"
					, null, Global.ServiceName, LogLevel.Debug, requestInfo.CorrelationID).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				try
				{
					File.Delete(filePath);
				}
				catch { }
				await Global.WriteLogsAsync(this.Logger, "Http.Synchronizers", "Sync a file failed" + "\r\n" +
					$"- From: {node}" + "\r\n" +
					$"- To: {Handler.NodeName}" + "\r\n" +
					$"- Service: {serviceName}" + "\r\n" +
					$"- System ID: {systemID}" + "\r\n" +
					$"- File: {fileName} ({filePath})"
				, ex, Global.ServiceName, LogLevel.Error, requestInfo.CorrelationID).ConfigureAwait(false);
				throw;
			}
		}

		public Task ProcessWebHookMessageAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		public Task<JToken> FetchTemporaryFileAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default)
			=> requestInfo.FetchTemporaryFileAsync(cancellationToken);

		public ValueTask DisposeAsync()
			=> new(Task.CompletedTask);

		public void Dispose()
		{
			GC.SuppressFinalize(this);
			this.DisposeAsync().Run(true);
		}
	}
}