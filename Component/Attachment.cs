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
	[Entity(CollectionName = "Attachments", TableName = "T_Files_Attachments", CacheStorageType = typeof(Utility), CacheStorageName = "Cache", Searchable = true)]
	public class Attachment : Repository<Attachment>
	{
		public Attachment()
		{
			this.ID = "";
			this.ServiceName = "";
			this.SystemID = "";
			this.EntityID = "";
			this.ObjectID = "";
			this.Name = "";
			this.Size = 0;
			this.ContentType = "";
			this.DownloadTimes = 0;
			this.IsShared = false;
			this.IsTracked = false;
			this.IsTemporary = false;
			this.Title = "";
			this.Description = "";
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
		public bool IsTracked { get; set; }

		[Sortable(IndexName = "States")]
		public bool IsShared { get; set; }

		[Sortable(IndexName = "States")]
		public bool IsTemporary { get; set; }

		[Property(MaxLength = 1000)]
		public string Description { get; set; }

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
		public override string RepositoryID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override Privileges OriginalPrivileges { get; set; }
		#endregion

	}
}