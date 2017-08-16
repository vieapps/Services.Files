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

			var request = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length);
			if (request.StartsWith("/"))
				request = request.Right(request.Length - 1);
			if (request.IndexOf("?") > 0)
				request = request.Left(request.IndexOf("?"));
			var info = request.ToArray('/', true);
			var useSmallImage = true;
			if (info.Length > 2)
				try
				{
					useSmallImage = !info[2].Url64Decode().IsEquals("big");
				}
				catch { }
			CaptchaHelper.GenerateCaptchaImage(context.Response, info[1].Url64Decode(), useSmallImage);
			return Task.CompletedTask;
		}
	}
}