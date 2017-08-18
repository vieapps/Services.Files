#region Related component
using System.Threading;
using System.Threading.Tasks;
using System.Web;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class CaptchaHandler : AbstractHttpHandler
	{
		public override Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (!context.Request.HttpMethod.IsEquals("GET"))
				throw new InvalidRequestException();

			var requestUrl = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length);
			while (requestUrl.StartsWith("/"))
				requestUrl = requestUrl.Right(requestUrl.Length - 1);
			if (requestUrl.IndexOf("?") > 0)
				requestUrl = requestUrl.Left(requestUrl.IndexOf("?"));

			var requestInfo = requestUrl.ToArray('/', true);
			var useSmallImage = true;
			if (requestInfo.Length > 2)
				try
				{
					useSmallImage = !requestInfo[2].Url64Decode().IsEquals("big");
				}
				catch { }
			context.Response.GenerateImage(requestInfo[1].Url64Decode(), useSmallImage);
			return Task.CompletedTask;
		}
	}
}