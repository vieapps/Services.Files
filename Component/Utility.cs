﻿#region Related components
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

		#region Caching mechanism
		static int _CacheTime = 0;

		/// <summary>
		/// Gets the default time for caching data
		/// </summary>
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

		static CacheManager _Cache = new CacheManager("VIEApps-Services-Files-Info", "Sliding", Utility.CacheTime);

		/// <summary>
		/// Gets the default cache storage
		/// </summary>
		public static CacheManager Cache { get { return Utility._Cache; } }

		static CacheManager _DataCache = new CacheManager("VIEApps-Services-Files-Data", "Sliding", Utility.CacheTime);

		/// <summary>
		/// Gets the cache storage for storing binary data
		/// </summary>
		public static CacheManager DataCache { get { return Utility._DataCache; } }
		#endregion

	}

	//  --------------------------------------------------------------------------------------------

	[Serializable]
	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}