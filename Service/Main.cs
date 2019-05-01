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
			base.Start(args, initializeRepository, nextAsync);
		}

		public override Task<JToken> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			var stopwatch = Stopwatch.StartNew();
			this.WriteLogs(requestInfo, $"Begin request ({requestInfo.Verb} {requestInfo.GetURI()})");
			try
			{
				JToken json = null;
				switch (requestInfo.ObjectName.ToLower())
				{
					case "thumbnail":
						json = new JObject();
						break;

					case "attachment":
						json = new JObject();
						break;

					case "captcha":
						json = this.GenerateCaptcha(requestInfo);
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
				return Task.FromResult(json);
			}
			catch (Exception ex)
			{
				return Task.FromException<JToken>(this.GetRuntimeException(requestInfo, ex, stopwatch));
			}
		}

		JObject GenerateCaptcha(RequestInfo requestInfo)
		{
			if (!requestInfo.Verb.IsEquals("GET"))
				throw new MethodAccessException(requestInfo.Verb);

			var code = CaptchaService.GenerateCode(requestInfo.Extra != null && requestInfo.Extra.ContainsKey("Salt") ? requestInfo.Extra["Salt"] : null);
			return new JObject
			{
				{ "Code", code },
				{ "Uri", $"{this.GetHttpURI("Files", "https://fs.vieapps.net")}/captchas/{code.Url64Encode()}/{UtilityService.GetUUID().Left(13).Url64Encode()}.jpg" }
			};
		}

	}
}