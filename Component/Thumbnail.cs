#region Related components
using System;
using System.Diagnostics;

using Newtonsoft.Json;
using MongoDB.Bson.Serialization.Attributes;

using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.Files
{
	[Serializable, BsonIgnoreExtraElements, DebuggerDisplay("ID = {ID}, Name = {Name}")]
	[Entity(CollectionName = "Thumbnails", TableName = "T_Files_Thumbnails", CacheStorageType = typeof(Utility), CacheStorageName = "Cache")]
	public class Thumbnail : Repository<Thumbnail>
	{
		public Thumbnail()
		{
			this.ID = "";
			this.ServiceName = "";
			this.SystemID = "";
			this.EntityID = "";
			this.ObjectID = "";
			this.Name = "";
			this.Size = 0;
			this.ContentType = "";
			this.IsTemporary = false;
			this.Created = DateTime.Now;
			this.LastModified = DateTime.Now;
		}

		#region Properties
		[Property(MaxLength = 50), Sortable(IndexName = "System")]
		public string ServiceName { get; set; }

		[Property(MaxLength = 32), Sortable(IndexName = "System")]
		public string ObjectID { get; set; }

		[Property(MaxLength = 250, NotNull = true), Sortable]
		public string Name { get; set; }

		public int Size { get; set; }

		[Property(MaxLength = 250)]
		public string ContentType { get; set; }

		[Sortable(IndexName = "States")]
		public int DownloadTimes { get; set; }

		[Sortable(IndexName = "States")]
		public bool IsTemporary { get; set; }

		[Sortable(IndexName = "Statistics")]
		public DateTime Created { get; set; }

		[Property(MaxLength = 32), Sortable(IndexName = "Statistics")]
		public string CreatedID { get; set; }

		[Sortable(IndexName = "Statistics")]
		public DateTime LastModified { get; set; }

		[Property(MaxLength = 32), Sortable(IndexName = "Statistics")]
		public string LastModifiedID { get; set; }
		#endregion

		#region IBusiness properties
		[JsonIgnore, BsonIgnore, Ignore]
		public override string Title { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string RepositoryID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override Privileges OriginalPrivileges { get; set; }
		#endregion

	}
}