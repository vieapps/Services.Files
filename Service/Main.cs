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
#endregion

namespace net.vieapps.Services.Files
{
	public class ServiceComponent : ServiceBase
	{

		public ServiceComponent() : base() { }

		public override string ServiceName { get { return "files"; } }

		public override async Task<JObject> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			// track
			var stopwatch = new Stopwatch();
			stopwatch.Start();
			var logs = new List<string>() { $"Process the request ({requestInfo.Verb}): {requestInfo.URI}" };
#if DEBUG || REQUESTLOGS
			logs.Add($"Request ==> {requestInfo.ToJson().ToString(Formatting.Indented)}");
#endif
			await this.WriteLogsAsync(requestInfo.CorrelationID, logs).ConfigureAwait(false);

			// process
			try
			{
				switch (requestInfo.ObjectName.ToLower())
				{
					case "thumbnail":
						await Task.Delay(0);
						break;

					case "attachment":
						await Task.Delay(0);
						break;

					case "captcha":
						return await UtilityService.ExecuteTask<JObject>(() => this.GenerateCaptcha(requestInfo), cancellationToken).ConfigureAwait(false);
				}
				throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.URI}]");
			}
			catch (Exception ex)
			{
				await this.WriteLogAsync(requestInfo.CorrelationID, "Error occurred while processing", ex).ConfigureAwait(false);
				throw this.GetRuntimeException(requestInfo, ex);
			}
			finally
			{
				stopwatch.Stop();
				await this.WriteLogAsync(requestInfo.CorrelationID, $"The request is completed - Execution times: {stopwatch.GetElapsedTimes()}").ConfigureAwait(false);
			}
		}

		JObject GenerateCaptcha(RequestInfo requestInfo)
		{
			if (!requestInfo.Verb.IsEquals("GET"))
				throw new MethodAccessException(requestInfo.Verb);

			var code = Captcha.GenerateCode(requestInfo.Extra != null && requestInfo.Extra.ContainsKey("Salt") ? requestInfo.Extra["Salt"] : null);
			return new JObject()
			{
				{ "Code", code },
				{ "Uri", UtilityService.GetAppSetting("FilesHttpUri", "https://afs.vieapps.net") + "/captchas/" + code.Url64Encode() + "/" + UtilityService.GetUUID().Left(13).Url64Encode() + ".jpg" }
			};
		}

		#region Process inter-communicate messages
		protected override void ProcessInterCommunicateMessage(CommunicateMessage message)
		{

		}
		#endregion

	}
}