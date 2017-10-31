#region Related components
using System;
using System.Collections.Specialized;
using System.Configuration;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Caching;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.Files
{
	public static class Utility
	{

		static int _CacheTime = 0;

		internal static int CacheTime
		{
			get
			{
				if (Utility._CacheTime < 1)
					try
					{
						Utility._CacheTime = UtilityService.GetAppSetting("CacheTime", "30").CastAs<int>();
					}
					catch
					{
						Utility._CacheTime = 30;
					}
				return Utility._CacheTime;
			}
		}

		static Cache _Cache = new Cache("VIEApps-Services-Files-Info", Utility.CacheTime);

		public static Cache Cache { get { return Utility._Cache; } }

		static Cache _DataCache = new Cache("VIEApps-Services-Files-Data", Utility.CacheTime);

		public static Cache DataCache { get { return Utility._DataCache; } }

		static string _HttpFilesUri = null;

		static string HttpFilesUri
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Utility._HttpFilesUri))
					Utility._HttpFilesUri = UtilityService.GetAppSetting("HttpFilesUri", "https://afs.vieapps.net");
				while (Utility._HttpFilesUri.EndsWith("/"))
					Utility._HttpFilesUri = Utility._HttpFilesUri.Left(Utility._HttpFilesUri.Length - 1);
				return Utility._HttpFilesUri;
			}
		}

	}

	//  --------------------------------------------------------------------------------------------

	[Serializable]
	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}