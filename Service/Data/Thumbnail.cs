#region Related components
using System;
using System.Linq;
using System.Xml.Serialization;
using System.Collections.Generic;
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
	[Entity(CollectionName = "Thumbnails", TableName = "T_Files_Thumbnails", CacheClass = typeof(Utility), CacheName = "Cache")]
	public class Thumbnail : Repository<Thumbnail>
	{
		public Thumbnail() : base()
		{
			this.ID = "";
			this.ServiceName = "";
			this.ObjectName = "";
			this.SystemID = "";
			this.DefinitionID = "";
			this.ObjectID = "";
			this.Filename = "";
			this.Size = 0;
			this.ContentType = "";
			this.IsTemporary = false;
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
		public string ObjectName { get; set; }

		/// <summary>
		/// Gets or sets the identity of the business system that the attachment file is belong/related to
		/// </summary>
		[Property(MaxLength = 32), Sortable(IndexName = "System")]
		public override string SystemID { get; set; }

		/// <summary>
		/// Gets or sets the identity of the entity definition that the attachment file is belong/related to
		/// </summary>
		[Property(MaxLength = 32), Sortable(IndexName = "System")]
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
		/// Gets or sets the state that determines the attachment file is temporay or not
		/// </summary>
		[Sortable(IndexName = "States")]
		public bool IsTemporary { get; set; }

		/// <summary>
		/// Gets or sets the name of the attachment file
		/// </summary>
		[Property(MaxLength = 250, NotNull = true), Sortable]
		public string Filename { get; set; }

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
		[JsonIgnore,XmlIgnore, BsonIgnore, Ignore]
		public override string Title { get; set; }

		[JsonIgnore, XmlIgnore, BsonIgnore, Ignore]
		public override string RepositoryID { get; set; }

		[JsonIgnore, XmlIgnore, BsonIgnore, Ignore]
		public override string EntityID { get; set; }

		[JsonIgnore, XmlIgnore, BsonIgnore, Ignore]
		public override Privileges OriginalPrivileges { get; set; }
		#endregion

		#region To JSON
		public override JObject ToJson(bool addTypeOfExtendedProperties, Action<JObject> onPreCompleted)
			=> this.ToJson(addTypeOfExtendedProperties, onPreCompleted, true);

		public JObject ToJson(bool addTypeOfExtendedProperties = false, Action<JObject> onPreCompleted = null, bool asNormalized = true, string title = null)
			=> base.ToJson(addTypeOfExtendedProperties, json =>
			{
				json["Filename"] = string.IsNullOrWhiteSpace(this.Filename) ? $"{this.ObjectID}.jpg" : this.Filename;
				if (asNormalized)
				{
					var uri = $"{Utility.ThumbnailURI}{(string.IsNullOrWhiteSpace(this.SystemID) || !this.SystemID.IsValidUUID() ? this.ServiceName : this.SystemID).ToLower()}/0/0/0";
					var index = string.IsNullOrWhiteSpace(this.Filename) || this.Filename.IndexOf("-") < 0 ? 0 : this.Filename.Replace(".jpg", "").Right(2).Replace("-", "").CastAs<int>();
					json["Index"] = index;
					json["URI"] = $"{uri}/{this.ObjectID}/{index}/{this.LastModified.ToString("HHmmss")}/{title ?? UtilityService.NewUUID}.jpg";
					Thumbnail.BeRemoved.ForEach(name => json.Remove(name));
				};
				onPreCompleted?.Invoke(json);
			});

		static IEnumerable<string> BeRemoved { get; } = new[] { "ID", "ServiceName", "SystemID", "DefinitionID", "ObjectID", "Size", "ContentType", "IsTemporary", "Created", "CreatedID", "LastModified", "LastModifiedID" };
		#endregion

	}
}