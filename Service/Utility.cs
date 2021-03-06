﻿using net.vieapps.Components.Repository;

namespace net.vieapps.Services.Files
{
	public static class Utility
	{
		public static Components.Caching.Cache Cache { get; internal set; }

		public static string FilesHttpURI { get; internal set; }

		public static string CaptchaURI => $"{Utility.FilesHttpURI}/captchas/";

		public static string ThumbnailURI => $"{Utility.FilesHttpURI}/thumbnails/";

		public static string DirectURI => $"{Utility.FilesHttpURI}/files/";

		public static string DownloadURI => $"{Utility.FilesHttpURI}/downloads/";
	}

	//  --------------------------------------------------------------------------------------------

	[Repository]
	public abstract class Repository<T> : RepositoryBase<T> where T : class { }
}