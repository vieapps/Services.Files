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

		#region Start
		public ServiceComponent() { }

		void WriteInfo(string correlationID, string info, Exception ex = null, bool writeLogs = true)
		{
			// prepare
			var msg = string.IsNullOrWhiteSpace(info)
				? ex?.Message ?? ""
				: info;

			// write to logs
			if (writeLogs)
				this.WriteLog(correlationID ?? UtilityService.NewUID, this.ServiceName, null, msg, ex);

			// write to console
			if (!Program.AsService)
			{
				Console.WriteLine(msg);
				if (ex != null)
					Console.WriteLine("-----------------------\r\n" + "==> [" + ex.GetType().GetTypeName(true) + "]: " + ex.Message + "\r\n" + ex.StackTrace + "\r\n-----------------------");
				else
					Console.WriteLine("~~~~~~~~~~~~~~~~~~~~>");
			}
		}

		internal void Start(string[] args = null, System.Action nextAction = null, Func<Task> nextActionAsync = null)
		{
			// prepare
			var correlationID = UtilityService.NewUID;

			// initialize repository
			try
			{
				this.WriteInfo(correlationID, "Initializing the repository");
				RepositoryStarter.Initialize();
			}
			catch (Exception ex)
			{
				this.WriteInfo(correlationID, "Error occurred while initializing the repository", ex);
			}

			// start the service
			Task.Run(async () =>
			{
				try
				{
					await this.StartAsync(
						service => this.WriteInfo(correlationID, "The service is registered - PID: " + Process.GetCurrentProcess().Id.ToString()),
						exception => this.WriteInfo(correlationID, "Error occurred while registering the service", exception)
					);
				}
				catch (Exception ex)
				{
					this.WriteInfo(correlationID, "Error occurred while starting the service", ex);
				}
			})
			.ContinueWith(async (task) =>
			{
				try
				{
					nextAction?.Invoke();
				}
				catch (Exception ex)
				{
					this.WriteInfo(correlationID, "Error occurred while running the next action (sync)", ex);
				}
				if (nextActionAsync != null)
					try
					{
						await nextActionAsync().ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						this.WriteInfo(correlationID, "Error occurred while running the next action (async)", ex);
					}
			})
			.ConfigureAwait(false);
		}
		#endregion

		public override string ServiceName { get { return "files"; } }

		public override async Task<JObject> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
#if DEBUG
			this.WriteInfo(requestInfo.CorrelationID, "Process the request\r\n==> Request:\r\n" + requestInfo.ToJson().ToString(Formatting.Indented), null, false);
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
						return await this.GenerateCaptchaAsync(requestInfo, cancellationToken);
				}

				// unknown
				var msg = "The request is invalid [" + this.ServiceURI + "]: " + requestInfo.Verb + " /";
				if (!string.IsNullOrWhiteSpace(requestInfo.ObjectName))
					msg += requestInfo.ObjectName + (requestInfo.Query.ContainsKey("object-identity") ? "/" + requestInfo.Query["object-identity"] : "");
				throw new InvalidRequestException(msg);
			}
			catch (Exception ex)
			{
				this.WriteInfo(requestInfo.CorrelationID, "Error occurred while processing\r\n==> Request:\r\n" + requestInfo.ToJson().ToString(Formatting.Indented), ex);
				throw this.GetRuntimeException(requestInfo, ex);
			} 
		}

		Task<JObject> GenerateCaptchaAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (!requestInfo.Verb.IsEquals("GET"))
				throw new MethodAccessException(requestInfo.Verb);

			try
			{
				var code = Captcha.GenerateCode(requestInfo.Extra != null && requestInfo.Extra.ContainsKey("Salt") ? requestInfo.Extra["Salt"] : null);
				return Task.FromResult(new JObject()
				{
					{ "Code", code },
					{ "Uri", UtilityService.GetAppSetting("HttpUri", "https://apis-fs-01.vieapps.net") + "/captchas/" + code.Url64Encode() + "/" + UtilityService.GetUUID().Left(13).Url64Encode() + ".jpg" }
				});
			}
			catch (Exception ex)
			{
				return Task.FromException<JObject>(ex);
			}
		}

		#region Process inter-communicate messages
		protected override void ProcessInterCommunicateMessage(CommunicateMessage message)
		{

		}
		#endregion

	}
}