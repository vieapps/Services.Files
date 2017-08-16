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

			var requestInfo = requestUrl.ToArray('/', true).RemoveAt(0);
			var useSmallImage = true;
			if (requestInfo.Length > 1)
				try
				{
					useSmallImage = !requestInfo[1].Url64Decode().IsEquals("big");
				}
				catch { }
			CaptchaHelper.GenerateCaptchaImage(context.Response, requestInfo[0].Url64Decode(), useSmallImage);
			return Task.CompletedTask;
		}
	}
}