#region Related components
using System;
using System.Diagnostics;
using System.Xml.Serialization;

using Newtonsoft.Json;
using MongoDB.Bson.Serialization.Attributes;

using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
#endregion

namespace net.vieapps.Services.Files
{
	[Serializable, BsonIgnoreExtraElements, DebuggerDisplay("ID = {ID}, Name = {Name}")]
	[Entity(CollectionName = "Attachments", TableName = "T_Files_Attachments", CacheClass = typeof(Utility), CacheName = "Cache", Searchable = true)]
	public class Attachment : Repository<Attachment>
	{
		public Attachment() : base()
		{
			this.ID = "";
			this.ServiceName = "";
			this.SystemID = "";
			this.DefinitionID = "";
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
		/// <summary>
		/// Gets or sets the name of the service that the attachment file is belong/related to
		/// </summary>
		[Property(MaxLength = 50), Sortable(IndexName = "System")]
		public new string ServiceName { get; set; }

		/// <summary>
		/// Gets or sets the identity of the business system that the attachment file is belong/related to
		/// </summary>
		[JsonIgnore, XmlIgnore, Property(MaxLength = 32), Sortable(IndexName = "System")]
		public override string SystemID { get; set; }

		/// <summary>
		/// Gets or sets the identity of the entity definition that the attachment file is belong/related to
		/// </summary>
		[JsonIgnore, XmlIgnore, Property(MaxLength = 32), Sortable(IndexName = "System")]
		public string DefinitionID { get; set; }

		/// <summary>
		/// Gets or sets the identity of the business object that the attachment file is belong/related to
		/// </summary>
		[Property(MaxLength = 32), Sortable(IndexName = "System")]
		public string ObjectID { get; set; }

		/// <summary>
		/// Gets or sets the size (in bytes) of the attachment file
		/// </summary>
		public int Size { get; set; }

		/// <summary>
		/// Gets or sets the MIME content-type of the attachment file
		/// </summary>
		[Property(MaxLength = 250)]
		public string ContentType { get; set; }

		/// <summary>
		/// Gets or sets the downloaded times of the attachment file
		/// </summary>
		[Sortable(IndexName = "States")]
		public int DownloadTimes { get; set; }

		/// <summary>
		/// Gets or sets the state that determines to track download activity of the attachment file
		/// </summary>
		[Sortable(IndexName = "States")]
		public bool IsTracked { get; set; }

		/// <summary>
		/// Gets or sets the state that determines to share the attachment file
		/// </summary>
		[Sortable(IndexName = "States")]
		public bool IsShared { get; set; }

		/// <summary>
		/// Gets or sets the state that determines the attachment file is temporay or not
		/// </summary>
		[Sortable(IndexName = "States")]
		public bool IsTemporary { get; set; }

		/// <summary>
		/// Gets or sets the name of the attachment file
		/// </summary>
		[Property(MaxLength = 250, NotNull = true), Sortable]
		public string Name { get; set; }

		/// <summary>
		/// Gets or sets the description of the attachment file
		/// </summary>
		[Property(MaxLength = 1000)]
		public string Description { get; set; }

		/// <summary>
		/// Gets or sets the time when the attachment file is created
		/// </summary>
		[Sortable(IndexName = "Statistics")]
		public DateTime Created { get; set; }

		/// <summary>
		/// Gets or sets the identity of user who upload the attachment file
		/// </summary>
		[Property(MaxLength = 32), Sortable(IndexName = "Statistics")]
		public string CreatedID { get; set; }

		/// <summary>
		/// Gets or sets the time when the attachment file is modified
		/// </summary>
		[Sortable(IndexName = "Statistics")]
		public DateTime LastModified { get; set; }

		/// <summary>
		/// Gets or sets the identity of user who update the attachment file
		/// </summary>
		[Property(MaxLength = 32), Sortable(IndexName = "Statistics")]
		public string LastModifiedID { get; set; }
		#endregion

		#region IBusiness properties
		[JsonIgnore, BsonIgnore, Ignore]
		public override string Title
		{
			get { return this.Name; }
			set { this.Name = value; }
		}

		[JsonIgnore, BsonIgnore, Ignore]
		public override string RepositoryID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string EntityID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override Privileges OriginalPrivileges { get; set; }
		#endregion

	}
}