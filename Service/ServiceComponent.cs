﻿#region Related components
using System;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.Files
{
	public class ServiceComponent : BaseService
	{

		#region Start
		public ServiceComponent() { }

		internal void Start(string[] args = null, Func<Task> continuationAsync = null)
		{
			// initialize repository
			try
			{
				RepositoryStarter.Initialize();
			}
			catch (Exception ex)
			{
				Console.WriteLine("Error occurred while initializing the repository: " + ex.Message + "\r\n" + ex.StackTrace);
			}

			// start the service
			Task.Run(async () =>
			{
				try
				{
					await this.StartAsync(
						() => {
							Console.WriteLine("The service [" + this.ServiceURI + "] is registered");
						},
						(ex) => {
							Console.WriteLine("Error occurred while registering the service [" + this.ServiceURI + "]: " + ex.Message + "\r\n" + ex.StackTrace);
						},
						this.OnInterCommunicateMessageReceived
					);
				}
				catch (Exception ex)
				{
					Console.WriteLine("Error occurred while starting the service [" + this.ServiceURI + "]: " + ex.Message + "\r\n" + ex.StackTrace);
				}
			})
			.ContinueWith(async (task) =>
			{
				if (continuationAsync != null)
					try
					{
						await continuationAsync().ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						Console.WriteLine("Error occurred while running the continuation function: " + ex.Message + "\r\n" + ex.StackTrace);
					}
			})
			.ConfigureAwait(false);
		}
		#endregion

		public override string ServiceName { get { return "files"; } }

		public override async Task<JObject> ProcessRequestAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
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
				}

				// unknown
				var msg = "The request is invalid [" + this.ServiceURI + "]: " + requestInfo.Verb + " /";
				if (!string.IsNullOrWhiteSpace(requestInfo.ObjectName))
					msg += requestInfo.ObjectName + (requestInfo.Query.ContainsKey("object-identity") ? "/" + requestInfo.Query["object-identity"] : "");
				throw new InvalidRequestException(msg);
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.Message + " - Correlation ID: " + requestInfo.CorrelationID);
				throw this.GetRuntimeException(requestInfo, ex);
			} 
		}

		#region Update with inter-communicate messages
		void OnInterCommunicateMessageReceived(BaseMessage message)
		{

		}
		#endregion

		~ServiceComponent()
		{
			this.Dispose(false);
		}
	}
}