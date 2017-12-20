#region Related components
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

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
#if DEBUG
			this.WriteLog(requestInfo.CorrelationID, "Process the request\r\n==> Request:\r\n" + requestInfo.ToJson().ToString(Formatting.Indented));
#endif
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

				// unknown
				var msg = "The request is invalid [" + this.ServiceURI + "]: " + requestInfo.Verb + " /";
				if (!string.IsNullOrWhiteSpace(requestInfo.ObjectName))
					msg += requestInfo.ObjectName + (requestInfo.Query.ContainsKey("object-identity") ? "/" + requestInfo.Query["object-identity"] : "");
				throw new InvalidRequestException(msg);
			}
			catch (Exception ex)
			{
				this.WriteLog(requestInfo.CorrelationID, $"Error occurred while processing; {ex.Message} [{ex.GetType().ToString()}]", ex);
				throw this.GetRuntimeException(requestInfo, ex);
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