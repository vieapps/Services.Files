using System.Web;
using System.Threading;
using System.Threading.Tasks;

namespace net.vieapps.Services.Files
{
    public abstract class AbstractHttpHandler
    {
		public abstract Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken));
    }
}