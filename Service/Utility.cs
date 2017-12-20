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

		static int _CacheTime = 0;

		internal static int CacheExpirationTime
		{
			get
			{
				if (Utility._CacheTime < 1)
					try
					{
						Utility._CacheTime = UtilityService.GetAppSetting("CacheExpirationTime", "30").CastAs<int>();
					}
					catch
					{
						Utility._CacheTime = 30;
					}
				return Utility._CacheTime;
			}
		}

		static Cache _Cache = null;

		public static Cache Cache
		{
			get
			{
				return Utility._Cache ?? (Utility._Cache = new Cache("VIEApps-Services-Files-Info", Utility.CacheExpirationTime, UtilityService.GetAppSetting("CacheProvider")));
			}
		}

		static Cache _DataCache = null;

		public static Cache DataCache
		{
			get
			{
				return Utility._DataCache ?? (Utility._DataCache = new Cache("VIEApps-Services-Files-Data", Utility.CacheExpirationTime, UtilityService.GetAppSetting("CacheProvider")));
			}
		}

		static string _FilesHttpUri = null;

		static string FilesHttpUri
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Utility._FilesHttpUri))
					Utility._FilesHttpUri = UtilityService.GetAppSetting("FilesHttpUri", "https://afs.vieapps.net");
				while (Utility._FilesHttpUri.EndsWith("/"))
					Utility._FilesHttpUri = Utility._FilesHttpUri.Left(Utility._FilesHttpUri.Length - 1);
				return Utility._FilesHttpUri;
			}
		}

	}

	//  --------------------------------------------------------------------------------------------

	[Serializable]
	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}