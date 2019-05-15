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

		public string SyncKey => UtilityService.GetAppSetting("Keys:Synchronization", "VIEApps-c98c6942-Default-0ad9-Files-40ed-Synchronization-9e53-Key-65c501fcf7b3");

		public async Task SendRequestAsync(string node, string serviceName, string systemID, string filename, bool isTemporary)
		{
			try
			{
				var service = await Router.GetUniqueServiceAsync(node).ConfigureAwait(false);
				await service.ProcessRequestAsync(new RequestInfo
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
					}
				}, Global.CancellationTokenSource.Token).ConfigureAwait(false);
				if (Global.IsDebugLogEnabled)
					await Global.WriteLogsAsync(this.Logger, "Http.Synchronizers", "Send a request to sync successful" + "\r\n" +
						$"- From: {Handler.NodeName}" + "\r\n" +
						$"- To: {node}" + "\r\n" +
						$"- Service: {serviceName}" + "\r\n" +
						$"- System ID: {systemID}" + "\r\n" +
						$"- File: {filename}" + "\r\n" +
						$"- Temporary: {isTemporary}"
					, null, Global.ServiceName).ConfigureAwait(false);
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
				, ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
			}
		}

		public async Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
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
			var filePath = Path.Combine(isTemporary ? Handler.TempFilesPath : Handler.AttachmentFilesPath, string.IsNullOrWhiteSpace(systemID) || !systemID.IsValidUUID() ? serviceName.ToLower() : systemID.ToLower(), filename);
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
						var service = await Router.GetUniqueServiceAsync(node).ConfigureAwait(false);
						using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true))
						{
							var buffer = new byte[AspNetCoreUtilityService.BufferSize * 10];
							var length = 0l;
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
										["x-checksum"] = data.Length > 0 ? data.GetCheckSum().GetHMACHash(this.SyncKey.ToBytes()).ToHex() : ""
									}
								}, Global.CancellationTokenSource.Token).ConfigureAwait(false);
								length += read;
							} while (read > 0);
							stopwatch.Stop();
							if (Global.IsDebugLogEnabled)
								await Global.WriteLogsAsync(this.Logger, "Http.Synchronizers", $"Sync a file successful - Execution times: {stopwatch.GetElapsedTimes()}" + "\r\n" +
									$"- From: {Handler.NodeName}" + "\r\n" +
									$"- To: {node}" + "\r\n" +
									$"- Service: {serviceName}" + "\r\n" +
									$"- System ID: {systemID}" + "\r\n" +
									$"- File: {filename} ({filePath} - {length:###,###,###,###,##0} bytes)"
								, null, Global.ServiceName).ConfigureAwait(false);
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
						, ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
					}
				}).ConfigureAwait(false);
			else
				throw new FileNotFoundException();
		}

		async Task ReceiveAsync(RequestInfo requestInfo, CancellationToken cancellationToken)
		{
			var serviceName = requestInfo.Header["x-service-name"];
			var systemID = requestInfo.Header["x-system-id"];
			var filename = requestInfo.Header["x-filename"];
			var isTemporary = "true".IsEquals(requestInfo.Header["x-temporary"]);
			var path = Path.Combine(isTemporary ? Handler.TempFilesPath : Handler.AttachmentFilesPath, string.IsNullOrWhiteSpace(systemID) || !systemID.IsValidUUID() ? serviceName.ToLower() : systemID.ToLower());
			if (!Directory.Exists(path))
				Directory.CreateDirectory(path);
			var filePath = Path.Combine(path, filename);

			try
			{
				var data = new byte[0];
				var checksum = "";
				if (!string.IsNullOrWhiteSpace(requestInfo.Body))
				{
					data = requestInfo.Body.Base64ToBytes();
					checksum = data.GetCheckSum().GetHMACHash(this.SyncKey.ToBytes()).ToHex();
				}

				if (!requestInfo.Extra.TryGetValue("x-checksum", out var xchecksum) || !xchecksum.Equals(checksum))
					throw new InvalidDataException();

				if (data.Length > 0)
					using (var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, AspNetCoreUtilityService.BufferSize, true))
					{
						await stream.WriteAsync(data, 0, data.Length, cancellationToken).ConfigureAwait(false);
					}
				else if (Global.IsDebugLogEnabled)
					await Global.WriteLogsAsync(this.Logger, "Http.Synchronizers", "Sync a file successful" + "\r\n" +
						$"- From: {requestInfo.Header["x-node"]}" + "\r\n" +
						$"- To: {Handler.NodeName}" + "\r\n" +
						$"- Service: {serviceName}" + "\r\n" +
						$"- System ID: {systemID}" + "\r\n" +
						$"- File: {filename} ({filePath} - {new FileInfo(filePath).Length:###,###,###,###,##0} bytes)"
					, null, Global.ServiceName).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await Global.WriteLogsAsync(this.Logger, "Http.Synchronizers", "Sync a file failed" + "\r\n" +
					$"- From: {requestInfo.Header["x-node"]}" + "\r\n" +
					$"- To: {Handler.NodeName}" + "\r\n" +
					$"- Service: {serviceName}" + "\r\n" +
					$"- System ID: {systemID}" + "\r\n" +
					$"- File: {filename} ({filePath})"
				, ex, Global.ServiceName, LogLevel.Error).ConfigureAwait(false);
				try
				{
					File.Delete(filePath);
				}
				catch { }
				throw ex;
			}
		}

		public void Dispose() { }
	}
}