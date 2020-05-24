#region Related components
using System;
using System.Xml.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MongoDB.Bson.Serialization.Attributes;
using net.vieapps.Components.Security;
using net.vieapps.Components.Repository;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files
{
	[Serializable, BsonIgnoreExtraElements]
	[Entity(CollectionName = "Attachments", TableName = "T_Files_Attachments", CacheClass = typeof(Utility), CacheName = "Cache", Searchable = true)]
	public class Attachment : Repository<Attachment>
	{
		public Attachment() : base()
		{
			this.ID = "";
			this.ServiceName = "";
			this.ObjectName = "";
			this.SystemID = "";
			this.EntityInfo = "";
			this.ObjectID = "";
			this.Filename = "";
			this.Size = 0;
			this.ContentType = "";
			this.Downloads = new CounterInfo();
			this.IsShared = false;
			this.IsTracked = false;
			this.IsTemporary = false;
			this.Title = "";
			this.Description = "";
			this.CreatedID = this.LastModifiedID = "";
			this.Created = this.LastModified = DateTime.Now;
		}

		#region Properties
		/// <summary>
		/// Gets or sets the name of the service that the attachment file is belong/related to
		/// </summary>
		[Property(MaxLength = 50), Sortable(IndexName = "System")]
		public new string ServiceName { get; set; }

		/// <summary>
		/// Gets or sets the name of the service object that the attachment file is belong/related to
		/// </summary>
		[Property(MaxLength = 50), Sortable(IndexName = "System")]
		public new string ObjectName { get; set; }

		/// <summary>
		/// Gets or sets the identity of the business system that the attachment file is belong/related to
		/// </summary>
		[Property(MaxLength = 32), Sortable(IndexName = "System")]
		public override string SystemID { get; set; }

		/// <summary>
		/// Gets or sets the identity of a specified business repository entity (means a business content-type at run-time) or type-name of an entity definition that the attachment file is belong/related to
		/// </summary>
		[Property(MaxLength = 250)]
		public string EntityInfo { get; set; }

		/// <summary>
		/// Gets or sets the identity of the business object that the attachment file is belong/related to
		/// </summary>
		[Property(MaxLength = 32), Sortable(IndexName = "System")]
		public string ObjectID { get; set; }

		/// <summary>
		/// Gets or sets the name of the attachment file
		/// </summary>
		[Property(MaxLength = 250, NotEmpty = true), Searchable, Sortable(IndexName = "File")]
		public string Filename { get; set; }

		/// <summary>
		/// Gets or sets the size (in bytes) of the attachment file
		/// </summary>
		public long Size { get; set; }

		/// <summary>
		/// Gets or sets the MIME content-type of the attachment file
		/// </summary>
		[Property(MaxLength = 250)]
		public string ContentType { get; set; }

		/// <summary>
		/// Gets or sets the downloaded times of the attachment file
		/// </summary>
		[AsJson]
		public CounterInfo Downloads { get; set; }

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
		/// Gets or sets the title of the attachment file
		/// </summary>
		[Property(MaxLength = 250, NotEmpty = true), Searchable, Sortable(IndexName = "Title")]
		public override string Title { get; set; }

		/// <summary>
		/// Gets or sets the description of the attachment file
		/// </summary>
		[Property(MaxLength = 1000), Searchable]
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
		public override string RepositoryID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override string RepositoryEntityID { get; set; }

		[JsonIgnore, BsonIgnore, Ignore]
		public override Privileges OriginalPrivileges { get; set; }
		#endregion

		#region To JSON
		public override JObject ToJson(bool addTypeOfExtendedProperties, Action<JObject> onCompleted)
			=> this.ToJson(true, addTypeOfExtendedProperties, onCompleted);

		public JObject ToJson(bool asNormalized, bool addTypeOfExtendedProperties, Action<JObject> onCompleted = null)
			=> base.ToJson(addTypeOfExtendedProperties, json =>
			{
				if (asNormalized)
					json["URIs"] = new JObject
					{
						{ "Direct", $"{Utility.DirectURI}{(string.IsNullOrWhiteSpace(this.SystemID) || !this.SystemID.IsValidUUID() ? this.ServiceName : this.SystemID).ToLower()}/{this.ContentType.Replace("/", "=")}/{this.ID}/{this.Filename.UrlEncode()}" },
						{ "Download", $"{Utility.DownloadURI}{this.ID}/1/{this.Filename.UrlEncode()}" }
					};
				onCompleted?.Invoke(json);
			});
		#endregion

	}

	[Serializable, BsonIgnoreExtraElements]
	public class CounterInfo
	{
		public CounterInfo() { }

		public DateTime LastUpdated { get; set; } = DateTime.Now;

		public int Month { get; set; } = 0;

		public int Week { get; set; } = 0;

		public int Total { get; set; } = 0;
	}
}