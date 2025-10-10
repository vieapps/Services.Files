using net.vieapps.Components.Caching;
using net.vieapps.Components.Utility;
using net.vieapps.Components.Repository;

namespace net.vieapps.Services.Files
{
	public static class Utility
	{
		public static Cache Cache { get; } = Cache.CreateInstance("VIEApps-Services-Files", Logger.GetLoggerFactory(), "true".IsEquals(UtilityService.GetAppSetting("Files:Cache:L1")));

		public static Cache HttpCache { get; } = Cache.CreateInstance("VIEApps-Services-Files-Http", Logger.GetLoggerFactory());

		public static string FilesHttpURI { get; internal set; }

		public static string CaptchaURI => $"{Utility.FilesHttpURI}/captchas/";

		public static string ThumbnailURI => $"{Utility.FilesHttpURI}/thumbnails/";

		public static string DirectURI => $"{Utility.FilesHttpURI}/files/";

		public static string DownloadURI => $"{Utility.FilesHttpURI}/downloads/";
	}

	//  --------------------------------------------------------------------------------------------

	[Repository(ServiceName = "Files")]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}