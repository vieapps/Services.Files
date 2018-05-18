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

		public override string ServiceName { get { return "Files"; } }

		public override async Task<JObject> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			try
			{
				switch (requestInfo.ObjectName.ToLower())
				{
					case "thumbnail":
						return new JObject();

					case "attachment":
						return new JObject();

					case "captcha":
						return await UtilityService.ExecuteTask<JObject>(() => this.GenerateCaptcha(requestInfo), cancellationToken).ConfigureAwait(false);

					default:
						throw new InvalidRequestException($"The request is invalid [({requestInfo.Verb}): {requestInfo.URI}]");
				}
			}
			catch (Exception ex)
			{
				throw this.GetRuntimeException(requestInfo, ex);
			}
		}

		JObject GenerateCaptcha(RequestInfo requestInfo)
		{
			if (!requestInfo.Verb.IsEquals("GET"))
				throw new MethodAccessException(requestInfo.Verb);

			var code = CaptchaService.GenerateCode(requestInfo.Extra != null && requestInfo.Extra.ContainsKey("Salt") ? requestInfo.Extra["Salt"] : null);
			return new JObject()
			{
				{ "Code", code },
				{ "Uri", UtilityService.GetAppSetting("HttpUri:Files", "https://afs.vieapps.net") + "/captchas/" + code.Url64Encode() + "/" + UtilityService.GetUUID().Left(13).Url64Encode() + ".jpg" }
			};
		}

	}
}