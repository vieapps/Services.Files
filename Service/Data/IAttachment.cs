using System;
namespace net.vieapps.Services.Files
{
	public interface IAttachment
	{
		/// <summary>
		/// Gets or sets the identity
		/// </summary>
		string ID { get; set; }

		/// <summary>
		/// Gets or sets the name of the service that the attachment file is belong/related to
		/// </summary>
		string ServiceName { get; set; }

		/// <summary>
		/// Gets or sets the name of the service object that the attachment file is belong/related to
		/// </summary>
		string ObjectName { get; set; }

		/// <summary>
		/// Gets or sets the identity of the business system that the attachment file is belong/related to
		/// </summary>
		string SystemID { get; set; }

		/// <summary>
		/// Gets or sets the identity of a specified business repository entity (means a business content-type at run-time) or type-name of an entity definition that the attachment file is belong/related to
		/// </summary>
		public string EntityInfo { get; set; }

		/// <summary>
		/// Gets or sets the identity of the business object that the attachment file is belong/related to
		/// </summary>
		string ObjectID { get; set; }

		/// <summary>
		/// Gets or sets the size (in bytes) of the attachment file
		/// </summary>
		long Size { get; set; }

		/// <summary>
		/// Gets or sets the MIME content-type of the attachment file
		/// </summary>
		string ContentType { get; set; }

		/// <summary>
		/// Gets or sets the state that determines the attachment file is temporay or not
		/// </summary>
		bool IsTemporary { get; set; }

		/// <summary>
		/// Gets or sets the name of the attachment file
		/// </summary>
		string Filename { get; set; }

		/// <summary>
		/// Gets or sets the time when the attachment file is created
		/// </summary>
		DateTime Created { get; set; }

		/// <summary>
		/// Gets or sets the identity of user who upload the attachment file
		/// </summary>
		string CreatedID { get; set; }

		/// <summary>
		/// Gets or sets the time when the attachment file is modified
		/// </summary>
		DateTime LastModified { get; set; }

		/// <summary>
		/// Gets or sets the identity of user who update the attachment file
		/// </summary>
		string LastModifiedID { get; set; }
	}
}