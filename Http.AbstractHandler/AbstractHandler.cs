﻿#region Related components
using System.Web;
using System.Threading;
using System.Threading.Tasks;

using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
    public abstract class AbstractHttpHandler
    {
		/// <summary>
		/// Process the request
		/// </summary>
		/// <param name="context"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		public abstract Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken));

		static IRTUService _RTUService = null;

		/// <summary>
		/// Send an inter-communicate message
		/// </summary>
		/// <param name="message"></param>
		/// <param name="cancellationToken"></param>
		/// <returns></returns>
		protected virtual async Task SendInterCommunicateMessageAsync(CommunicateMessage message, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (AbstractHttpHandler._RTUService == null)
				AbstractHttpHandler._RTUService = ObjectService.GetStaticObject("net.vieapps.Services.Files.Global, VIEApps.Services.Files.Http", "RTUService") as IRTUService;

			if (AbstractHttpHandler._RTUService != null)
				await AbstractHttpHandler._RTUService.SendInterCommunicateMessageAsync(message, cancellationToken);
		}
    }
}