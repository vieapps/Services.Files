#region Related components
using System;
using System.Configuration;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Linq;
using System.Xml;
using System.Web;
using System.Web.Security;
using System.Web.Configuration;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using WampSharp.Core.Listener;
using WampSharp.V2;
using WampSharp.V2.Realm;
using WampSharp.V2.Core.Contracts;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public static class Global
	{

		#region Attributes
		internal static CancellationTokenSource CancellationTokenSource = new CancellationTokenSource();
		internal static HashSet<string> HiddenSegments = null, BypassSegments = null, StaticSegments = null;
		internal static Dictionary<string, Type> Handlers = new Dictionary<string, Type>();

		internal static IWampChannel IncommingChannel = null, OutgoingChannel = null;
		internal static long IncommingChannelSessionID = 0, OutgoingChannelSessionID = 0;
		internal static bool ChannelsAreClosedBySystem = false;

		internal static Dictionary<string, IService> Services = new Dictionary<string, IService>();
		internal static IManagementService ManagementService = null;
		internal static IDisposable InterCommunicationMessageUpdater = null;
		internal static IRTUService _RTUService = null;

		static Queue<Tuple<string, List<string>, string, string>> Logs = new Queue<Tuple<string, List<string>, string, string>>();

		static string _AESKey = null, _JWTKey = null, _PublicJWTKey = null, _RSAKey = null, _RSAExponent = null, _RSAModulus = null;
		static RSACryptoServiceProvider _RSA = null;
		#endregion

		#region Get the app info
		internal static Tuple<string, string, string> GetAppInfo(NameValueCollection header, NameValueCollection query, string agentString, string ipAddress, Uri urlReferrer = null)
		{
			var name = UtilityService.GetAppParameter("x-app-name", header, query, "Generic App");

			var platform = UtilityService.GetAppParameter("x-app-platform", header, query);
			if (string.IsNullOrWhiteSpace(platform))
				platform = string.IsNullOrWhiteSpace(agentString)
					? "N/A"
					: agentString.IsContains("iPhone") || agentString.IsContains("iPad") || agentString.IsContains("iPod")
						? "iOS PWA"
						: agentString.IsContains("Android")
							? "Android PWA"
							: agentString.IsContains("Windows Phone")
								? "Windows Phone PWA"
								: agentString.IsContains("BlackBerry") || agentString.IsContains("BB10")
									? "BlackBerry PWA"
									: agentString.IsContains("IEMobile") || agentString.IsContains("Opera Mini")
										? "Mobile PWA"
										: "Desktop PWA";

			var origin = header?["origin"];
			if (string.IsNullOrWhiteSpace(origin))
				origin = urlReferrer?.AbsoluteUri;
			if (string.IsNullOrWhiteSpace(origin) || origin.IsStartsWith("file://") || origin.IsStartsWith("http://localhost"))
				origin = ipAddress;

			return new Tuple<string, string, string>(name, platform, origin);
		}

		internal static Tuple<string, string, string> GetAppInfo(this HttpContext context)
		{
			return Global.GetAppInfo(context.Request.Headers, context.Request.QueryString, context.Request.UserAgent, context.Request.UserHostAddress, context.Request.UrlReferrer);
		}
		#endregion

		#region Encryption keys
		/// <summary>
		/// Geths the key for working with AES
		/// </summary>
		internal static string AESKey
		{
			get
			{
				if (Global._AESKey == null)
					Global._AESKey = UtilityService.GetAppSetting("AESKey", "VIEApps-c98c6942-Default-0ad9-AES-40ed-Encryption-9e53-Key-65c501fcf7b3");
				return Global._AESKey;
			}
		}

		internal static byte[] GenerateEncryptionKey(string additional = null)
		{
			return (Global.AESKey + (string.IsNullOrWhiteSpace(additional) ? "" : ":" + additional)).GenerateEncryptionKey(false, false, 256);
		}

		internal static byte[] GenerateEncryptionIV(string additional = null)
		{
			return (Global.AESKey + (string.IsNullOrWhiteSpace(additional) ? "" : ":" + additional)).GenerateEncryptionKey(true, true, 128);
		}

		/// <summary>
		/// Geths the key for working with JSON Web Token
		/// </summary>
		internal static string JWTKey
		{
			get
			{
				if (Global._JWTKey == null)
					Global._JWTKey = UtilityService.GetAppSetting("JWTKey", "VIEApps-49d8bd8c-Default-babc-JWT-43f4-Sign-bc30-Key-355b0891dc0f");
				return Global._JWTKey;
			}
		}

		internal static string GenerateJWTKey()
		{
			if (Global._PublicJWTKey == null)
				Global._PublicJWTKey = Global.JWTKey.GetHMACSHA512(Global.AESKey).ToBase64Url(false, true);
			return Global._PublicJWTKey;
		}

		/// <summary>
		/// Geths the key for working with RSA
		/// </summary>
		internal static string RSAKey
		{
			get
			{
				if (Global._RSAKey == null)
					Global._RSAKey = UtilityService.GetAppSetting("RSAKey", "FU4UoaKHeOYHOYDFlxlcSnsAelTHcu2o0eMAyzYwdWXQCpHZO8DRA2OLesV/JAilDRKILDjEBkTWbkghvLnlss4ymoqZzzJrpGn/cUjRP2/4P2Q18IAYYdipP65nMg4YXkyKfZC/MZfArm8pl51+FiPtQoSG0fHkmoXlq5xJ0g7jhzyMJelZjsGq+3QPji3stj89o5QK5WZZhxOmcGWvjsSLMTrV9bF4Gd9Si5UG8Wzs9/iybvu/yt3ZvIjo9kxrLceVpW/cQjDEhqQzRogpQPtSfkTgeEBtjkp91B+ISGquWWAPUt/bMjBR94zQWCBneIB6bEHY9gMDjabyZDsiSKSuKlvDWpEEx8j2DJLcqstXHs9akw5k44pusVapamk2TCSjcCnEX9SFUbyHrbb3ODJPBqVL4sAnKLl8dv54+ihvb6Oooeq+tiAx6LVwmSCTRZmGrgdURO110eewrEAbKcF+DxHe7wfkuKYLDkzskjQ44/BWzlWydxzXHAL3r59/1P/t7AtP9CAZVv9MXQghafkCJfEx+Q94gfyzl79PwCFrKa4YcEUAjif55aVaJcWdPWWBIaIgELlf/NgCzGRleTKG0KP1dcdkpbpQZb7lik6JLUWlPD0YaFpEomjpwNeblK+KElUWhqgh2SPtsDyISYB22ZsThWI4kdKHsngtR+SF7gsnuR4DUcsew99R3hFtC/9jtRxNgvVukMWy5q17gWcQQPRf4zbWgLfqe3uJwz7bitf9O5Okd+2INMb5iHKxW7uxemVfMUKKCT+60PUtsbKgd+oqOpOLhfwC2LbTE3iCOkPuKkKQAIor1+CahhZ7CWzxFaatiAVKzfSTdHna9gcfewZlahWQv4+frqWa6rfmEs8EbJt8sKimXlehY8oZf3TaHqS5j/8Pu7RLVpF7Yt3El+vdkbzEphS5P5fQdcKZCxGCWFl2WtrP+Njtw/J/ifjMuxrjppo4CxIGPurEODTTE3l+9rGQN0tm7uhjjdRiOLEK/ulXA04s5qMDfZTgZZowS1/379S1ImflGSLXGkmOjU42KsoI6v17dXXQ/MwWd7wilHC+ZRLsvZC5ts0F7pc4Qq4KmDZG4HKKf4SIiJpbpHgovKfVJdVXrTL/coHpg+FzBNvCO02TUBqJytD4dV4wZomSYwuWdo5is4xYjpOdMMZfzipEcDn0pNM7TzNonLAjUlefCAjJONl+g3s1tHdNZ6aSsLF63CpRhEchN3HFxSU4KGj0EbaR96Fo8PMwhrharF/QKWDfRvOK+2qsTqwZPqVFygObZq6RUfp6wWZwP8Tj+e1oE9DrvVMoNwhfDXtZm7d2Yc4eu+PyvJ7louy5lFGdtIuc9u3VUtw/Y0K7sRS383T+SHXBHJoLjQOK65TjeAzrYDUJF1UMV3UvuBrfVMUErMGlLzJdj/TqYDQdJS5+/ehaAnK4aDYSHCI8DQXF5NWLFlOSDy/lHIjN5msz/tfJTM70YqMQgslQmE5yH78HEQytlTsd+7WlhcLd1LpjylXQJhXYLRM8RX9zoKi7gJxNYe1GpnpQhfPpIg28trSwvs4zMPqf3YWf12HM1F7M9OUIkQoUtwyEUE5DUv2ZkDjYrMHbTN9xuJTDH/5FNsyUYCAER0Cgt/p1H+08fFFdrdZNIVRwI2s7mcMgIXtAcDLagcf0cxn1qYyc1vC9wmX7Ad/Sy69D+Yfhr2aJGgxSN1m7VIGncBfWGiVMwoaJi//pDRkmfkusAq+LypEZHy83HWf3hvpxvZBLjxRZeYXA4SMcTRMrPlkfzpGPd8Pe5JtYotUvJHJ/QRk/GqTnJuiB+hwvB7d73P+jwpE4gXpJszHHbYwQEpsdLg0xOTWDHMxF08IfLipuM7d9yTEziMfBApJ9R3+fTOMJ0h7BgCWiYp6DmNwPbmrmHbbXhwNJ2dSWS15+x/iWKEV+zz1rJTpZpqWyo4/EGg8Ao4DIXHSV8cHk4vOywsC2Kff/d7tE1jXKpWDLEo6Yo0NIgHG6gehWPSbnHWQNw6hkyKh/sO6IT0PGgM2A/FgYrsALTxbBoakMuCh+FPS/y4FXWQB80ABmKQTwql0jBAMhhBJTjdH0mS21WOj0wQ8gZgddpyePc5VPXuT9Tf6KqFwFs29f6IZDRrQs609aM/QNgfJqfhSlmzYnuDUJxzXpSzUmU9lejvu/GqO2T1XmY/ergxK9SI7aAah3TQIyZ36umMpUtsoN6hFy5RyMBnNJ/Cvt56pS5wLaq0Gl8WjctHmxAHy+UfIOh0P3HATlp2cto+w=");
				return Global._RSAKey;
			}
		}

		internal static RSACryptoServiceProvider RSA
		{
			get
			{
				if (Global._RSA == null)
					try
					{
						Global._RSA = CryptoService.CreateRSAInstance(Global.RSAKey.Decrypt());
					}
					catch (Exception)
					{
						throw;
					}
				return Global._RSA;
			}
		}

		internal static string RSAExponent
		{
			get
			{
				if (Global._RSAExponent == null)
				{
					var xmlDoc = new XmlDocument();
					xmlDoc.LoadXml(Global.RSA.ToXmlString(false));
					Global._RSAExponent = xmlDoc.DocumentElement.ChildNodes[1].InnerText.ToHexa(true);
				}
				return Global._RSAExponent;
			}
		}

		internal static string RSAModulus
		{
			get
			{
				if (Global._RSAModulus == null)
				{
					var xmlDoc = new XmlDocument();
					xmlDoc.LoadXml(Global.RSA.ToXmlString(false));
					Global._RSAModulus = xmlDoc.DocumentElement.ChildNodes[0].InnerText.ToHexa(true);
				}
				return Global._RSAModulus;
			}
		}
		#endregion

		#region WAMP channels
		static Tuple<string, string, bool> GetLocationInfo()
		{
			var address = UtilityService.GetAppSetting("RouterAddress", "ws://127.0.0.1:26429/");
			var realm = UtilityService.GetAppSetting("RouterRealm", "VIEAppsRealm");
			var mode = UtilityService.GetAppSetting("RouterChannelsMode", "MsgPack");
			return new Tuple<string, string, bool>(address, realm, mode.IsEquals("json"));
		}

		internal static async Task OpenIncomingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null)
		{
			if (Global.IncommingChannel != null)
				return;

			var info = Global.GetLocationInfo();
			var address = info.Item1;
			var realm = info.Item2;
			var useJsonChannel = info.Item3;

			Global.IncommingChannel = useJsonChannel
				? (new DefaultWampChannelFactory()).CreateJsonChannel(address, realm)
				: (new DefaultWampChannelFactory()).CreateMsgpackChannel(address, realm);

			Global.IncommingChannel.RealmProxy.Monitor.ConnectionEstablished += (sender, arguments) =>
			{
				Global.IncommingChannelSessionID = arguments.SessionId;
				Global.IncommingChannel.RealmProxy.Services
					.GetSubject<CommunicateMessage>("net.vieapps.rtu.communicate.messages.files")
					.Subscribe(
						message => Global.ProcessInterCommunicateMessage(message),
						exception => Global.WriteLogs(UtilityService.BlankUID, "Error occurred while fetching inter-communicate message", exception)
					);
			};

			if (onConnectionEstablished != null)
				Global.IncommingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				Global.IncommingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				Global.IncommingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await Global.IncommingChannel.Open().ConfigureAwait(false);
		}

		internal static void CloseIncomingChannel()
		{
			if (Global.IncommingChannel != null)
			{
				Global.IncommingChannel.Close("The incoming channel is closed when stop the HTTP Files", new GoodbyeDetails());
				Global.IncommingChannel = null;
			}
		}

		internal static void ReOpenIncomingChannel(int delay = 123, System.Action onSuccess = null, Action<Exception> onError = null)
		{
			if (Global.IncommingChannel != null)
				(new WampChannelReconnector(Global.IncommingChannel, async () =>
				{
					await Task.Delay(delay > 0 ? delay : 0);
					try
					{
						await Global.IncommingChannel.Open().ConfigureAwait(false);
						onSuccess?.Invoke();
					}
					catch (Exception ex)
					{
						onError?.Invoke(ex);
					}
				})).Start();
		}

		internal static async Task OpenOutgoingChannelAsync(Action<object, WampSessionCreatedEventArgs> onConnectionEstablished = null, Action<object, WampSessionCloseEventArgs> onConnectionBroken = null, Action<object, WampConnectionErrorEventArgs> onConnectionError = null)
		{
			if (Global.OutgoingChannel != null)
				return;

			var info = Global.GetLocationInfo();
			var address = info.Item1;
			var realm = info.Item2;
			var useJsonChannel = info.Item3;

			Global.OutgoingChannel = useJsonChannel
				? (new DefaultWampChannelFactory()).CreateJsonChannel(address, realm)
				: (new DefaultWampChannelFactory()).CreateMsgpackChannel(address, realm);

			Global.OutgoingChannel.RealmProxy.Monitor.ConnectionEstablished += (sender, arguments) =>
			{
				Global.OutgoingChannelSessionID = arguments.SessionId;
				Task.Run(async () =>
				{
					try
					{
						await Global.InitializeManagementServiceAsync().ConfigureAwait(false);
						await Global.InitializeRTUServiceAsync().ConfigureAwait(false);
					}
					catch { }
				}).ConfigureAwait(false);
			};

			if (onConnectionEstablished != null)
				Global.OutgoingChannel.RealmProxy.Monitor.ConnectionEstablished += new EventHandler<WampSessionCreatedEventArgs>(onConnectionEstablished);

			if (onConnectionBroken != null)
				Global.OutgoingChannel.RealmProxy.Monitor.ConnectionBroken += new EventHandler<WampSessionCloseEventArgs>(onConnectionBroken);

			if (onConnectionError != null)
				Global.OutgoingChannel.RealmProxy.Monitor.ConnectionError += new EventHandler<WampConnectionErrorEventArgs>(onConnectionError);

			await Global.OutgoingChannel.Open().ConfigureAwait(false);
		}

		internal static void CloseOutgoingChannel()
		{
			if (Global.OutgoingChannel != null)
			{
				Global.OutgoingChannel.Close("The outgoing channel is closed when stop the HTTP Files", new GoodbyeDetails());
				Global.OutgoingChannel = null;
			}
		}

		internal static void ReOpenOutgoingChannel(int delay = 123, System.Action onSuccess = null, Action<Exception> onError = null)
		{
			if (Global.OutgoingChannel != null)
				(new WampChannelReconnector(Global.OutgoingChannel, async () =>
				{
					await Task.Delay(delay > 0 ? delay : 0);
					try
					{
						await Global.OutgoingChannel.Open().ConfigureAwait(false);
						onSuccess?.Invoke();
					}
					catch (Exception ex)
					{
						onError?.Invoke(ex);
					}
				})).Start();
		}

		internal static async Task OpenChannelsAsync()
		{
			await Global.OpenIncomingChannelAsync(
				(sender, arguments) =>
				{
					Global.Logs.Enqueue(new Tuple<string, List<string>, string, string>(UtilityService.NewUID, new List<string>() { "The outgoing connection is established - Session ID: " + arguments.SessionId }, null, null));
				},
				(sender, arguments) =>
				{
					if (arguments.CloseType.Equals(SessionCloseType.Disconnection))
						Global.WriteLogs("The incoming connection is broken because the router is not found or the router is refused - Session ID: " + arguments.SessionId + "\r\n" + "- Reason: " + (string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason) + " - " + arguments.CloseType.ToString());
					else
					{
						if (Global.ChannelsAreClosedBySystem)
							Global.WriteLogs("The incoming connection is closed - Session ID: " + arguments.SessionId + "\r\n" + "- Reason: " + (string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason) + " - " + arguments.CloseType.ToString());
						else
							Global.ReOpenIncomingChannel(
								123,
								() =>
								{
									Global.WriteLogs("Re-connect the incoming connection successful");
								},
								(ex) =>
								{
									Global.WriteLogs("Error occurred while re-connecting the incoming connection", ex);
								}
							);
					}
				},
				(sender, arguments) =>
				{
					Global.WriteLogs("Got an error of incoming connection: " + (arguments.Exception != null ? arguments.Exception.Message : "None"), arguments.Exception);
				}
			).ConfigureAwait(false);

			await Global.OpenOutgoingChannelAsync(
				(sender, arguments) =>
				{
					Global.WriteLogs("The outgoing connection is established - Session ID: " + arguments.SessionId);
				},
				(sender, arguments) =>
				{
					if (arguments.CloseType.Equals(SessionCloseType.Disconnection))
						Global.WriteLogs("The outgoing connection is broken because the router is not found or the router is refused - Session ID: " + arguments.SessionId + "\r\n" + "- Reason: " + (string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason) + " - " + arguments.CloseType.ToString());
					else
					{
						if (Global.ChannelsAreClosedBySystem)
							Global.WriteLogs("The outgoing connection is closed - Session ID: " + arguments.SessionId + "\r\n" + "- Reason: " + (string.IsNullOrWhiteSpace(arguments.Reason) ? "Unknown" : arguments.Reason) + " - " + arguments.CloseType.ToString());
						else
							Global.ReOpenOutgoingChannel(
								123,
								() =>
								{
									Global.WriteLogs("Re-connect the outgoing connection successful");
								},
								(ex) =>
								{
									Global.WriteLogs("Error occurred while re-connecting the outgoing connection", ex);
								}
							);
					}
				},
				(sender, arguments) =>
				{
					Global.WriteLogs("Got an error of outgoing connection: " + (arguments.Exception != null ? arguments.Exception.Message : "None"), arguments.Exception);
				}
			).ConfigureAwait(false);
		}
		#endregion

		#region Working with logs
		internal static string GetCorrelationID(IDictionary items)
		{
			if (items == null)
				return UtilityService.GetUUID();

			var id = items.Contains("Correlation-ID")
				? items["Correlation-ID"] as string
				: null;

			if (string.IsNullOrWhiteSpace(id))
			{
				id = UtilityService.GetUUID();
				items.Add("Correlation-ID", id);
			}

			return id;
		}

		internal static string GetCorrelationID()
		{
			return Global.GetCorrelationID(HttpContext.Current?.Items);
		}

		internal static async Task InitializeManagementServiceAsync()
		{
			if (Global.ManagementService == null)
			{
				await Global.OpenOutgoingChannelAsync().ConfigureAwait(false);
				Global.ManagementService = Global.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IManagementService>();
			}
		}

		internal static async Task WriteLogsAsync(string correlationID, List<string> logs, Exception exception = null)
		{
			// prepare
			var simpleStack = exception != null
				? exception.StackTrace
				: "";

			var fullStack = "";
			if (exception != null)
			{
				fullStack = exception.StackTrace;
				var inner = exception.InnerException;
				var counter = 0;
				while (inner != null)
				{
					counter++;
					fullStack += "\r\n" + "-> Inner (" + counter.ToString() + "): ---->>>>" + "\r\n" + inner.StackTrace;
					inner = inner.InnerException;
				}
				fullStack += "\r\n" + "-------------------------------------" + "\r\n";
			}

			// write logs
			try
			{
				await Global.InitializeManagementServiceAsync().ConfigureAwait(false);
				while (Global.Logs.Count > 0)
				{
					var log = Global.Logs.Dequeue();
					await Global.ManagementService.WriteLogsAsync(log.Item1, "files", "http", log.Item2, log.Item3, log.Item4, Global.CancellationTokenSource.Token).ConfigureAwait(false);
				}
				await Global.ManagementService.WriteLogsAsync(correlationID, "files", "http", logs, simpleStack, fullStack, Global.CancellationTokenSource.Token).ConfigureAwait(false);
			}
			catch
			{
				Global.Logs.Enqueue(new Tuple<string, List<string>, string, string>(correlationID, logs, simpleStack, fullStack));
			}
		}

		internal static Task WriteLogsAsync(string correlationID, string log, Exception exception = null)
		{
			var logs = !string.IsNullOrEmpty(log)
				? new List<string>() { log }
				: exception != null
					? new List<string>() { exception.Message + " [" + exception.GetType().ToString() + "]" }
					: new List<string>();
			return Global.WriteLogsAsync(correlationID, logs, exception);
		}

		internal static Task WriteLogsAsync(List<string> logs, Exception exception = null)
		{
			return Global.WriteLogsAsync(Global.GetCorrelationID(), logs, exception);
		}

		internal static Task WriteLogsAsync(string log, Exception exception = null)
		{
			return Global.WriteLogsAsync(Global.GetCorrelationID(), log, exception);
		}

		internal static void WriteLogs(string correlationID, List<string> logs, Exception exception = null)
		{
			Task.Run(async () =>
			{
				await Global.WriteLogsAsync(correlationID, logs, exception);
			}).ConfigureAwait(false);
		}

		internal static void WriteLogs(string correlationID, string log, Exception exception = null)
		{
			var logs = !string.IsNullOrEmpty(log)
				? new List<string>() { log }
				: exception != null
					? new List<string>() { exception.Message + " [" + exception.GetType().ToString() + "]" }
					: new List<string>();
			Global.WriteLogs(correlationID, logs, exception);
		}

		internal static void WriteLogs(List<string> logs, Exception exception = null)
		{
			Global.WriteLogs(Global.GetCorrelationID(), logs, exception);
		}

		internal static void WriteLogs(string log, Exception exception = null)
		{
			Global.WriteLogs(Global.GetCorrelationID(), log, exception);
		}
		#endregion

		#region Start/End the app
		internal static void OnAppStart(HttpContext context)
		{
			var stopwatch = new Stopwatch();
			stopwatch.Start();

			// Json.NET
			JsonConvert.DefaultSettings = () => new JsonSerializerSettings()
			{
				Formatting = Newtonsoft.Json.Formatting.Indented,
				ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
				DateTimeZoneHandling = DateTimeZoneHandling.Local
			};

			// open WAMP channels
			Task.Run(async () =>
			{
				await Global.OpenChannelsAsync();
			}).ConfigureAwait(false);

			// special segments
			Global.BypassSegments = UtilityService.GetAppSetting("BypassSegments")?.Trim().ToLower().ToHashSet('|', true) ?? new HashSet<string>();
			Global.HiddenSegments = UtilityService.GetAppSetting("HiddenSegments")?.Trim().ToLower().ToHashSet('|', true) ?? new HashSet<string>();
			Global.StaticSegments = UtilityService.GetAppSetting("StaticSegments")?.Trim().ToLower().ToHashSet('|', true) ?? new HashSet<string>();

			// default handlers
			Global.Handlers = new Dictionary<string, Type>()
			{
				{ "avatars", Type.GetType("net.vieapps.Services.Files.AvatarHandler, VIEApps.Services.Files.Http") },
				{ "captchas", Type.GetType("net.vieapps.Services.Files.CaptchaHandler, VIEApps.Services.Files.Http") },
				{ "thumbnails", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler, VIEApps.Services.Files.Http") },
				{ "thumbnailbigs", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler, VIEApps.Services.Files.Http") },
				{ "thumbnailpngs", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler, VIEApps.Services.Files.Http") },
				{ "thumbnailbigpngs", Type.GetType("net.vieapps.Services.Files.ThumbnailHandler, VIEApps.Services.Files.Http") },
				{ "files", Type.GetType("net.vieapps.Services.Files.FileHandler, VIEApps.Services.Files.Http") },
				{ "downloads", Type.GetType("net.vieapps.Services.Files.DownloadHandler, VIEApps.Services.Files.Http") }
			};

			// additional handlers
			if (ConfigurationManager.GetSection("net.vieapps.files.handlers") is AppConfigurationSectionHandler config)
				if (config.Section.SelectNodes("handler") is XmlNodeList nodes)
					foreach (XmlNode node in nodes)
					{
						var info = node.ToJson();

						var keyName = info["key"] != null && info["key"] is JValue && (info["key"] as JValue).Value != null
							? (info["key"] as JValue).Value.ToString().ToLower()
							: null;

						var typeName = info["type"] != null && info["type"] is JValue && (info["type"] as JValue).Value != null
							? (info["type"] as JValue).Value.ToString()
							: null;

						if (!string.IsNullOrWhiteSpace(keyName) && !string.IsNullOrWhiteSpace(typeName) && !Global.Handlers.ContainsKey(keyName))
							try
							{
								var type = Type.GetType(typeName);
								if (type != null && type.CreateInstance() is AbstractHttpHandler)
									Global.Handlers.Add(keyName, type);
							}
							catch { }
					}

			// handling unhandled exception
			AppDomain.CurrentDomain.UnhandledException += (sender, arguments) =>
			{
				Global.WriteLogs("An unhandled exception is thrown", arguments.ExceptionObject as Exception);
			};

			stopwatch.Stop();
			Global.WriteLogs("*** The HTTP Files is ready for serving. The app is initialized in " + stopwatch.GetElapsedTimes());
		}

		internal static void OnAppEnd()
		{
			Global.CancellationTokenSource.Cancel();
			Global.CancellationTokenSource.Dispose();
			Global.InterCommunicationMessageUpdater?.Dispose();

			Global.ChannelsAreClosedBySystem = true;
			Global.CloseIncomingChannel();
			Global.CloseOutgoingChannel();
		}
		#endregion

		#region Begin/End the request
		internal static void OnAppBeginRequest(HttpApplication app)
		{
			// update default headers to allow access from everywhere
			app.Context.Response.HeaderEncoding = Encoding.UTF8;
			app.Context.Response.Headers.Add("access-control-allow-origin", "*");
			app.Context.Response.Headers.Add("x-correlation-id", Global.GetCorrelationID(app.Context.Items));

			// update special headers on OPTIONS request
			if (app.Context.Request.HttpMethod.Equals("OPTIONS"))
			{
				app.Context.Response.Headers.Add("access-control-allow-methods", "HEAD,GET,POST");

				var allowHeaders = app.Context.Request.Headers.Get("access-control-request-headers");
				if (!string.IsNullOrWhiteSpace(allowHeaders))
					app.Context.Response.Headers.Add("access-control-allow-headers", allowHeaders);

				return;
			}

			// authentication: process passport token
			if (!string.IsNullOrWhiteSpace(app.Context.Request.QueryString["x-passport-token"]))
				try
				{
					// parse
					var token = User.ParsePassportToken(app.Context.Request.QueryString["x-passport-token"], Global.AESKey, Global.GenerateJWTKey());
					var userID = token.Item1;
					var accessToken = token.Item2;
					var sessionID = token.Item3;
					var deviceID = token.Item4;

					var ticket = AspNetSecurityService.ParseAuthenticateToken(accessToken, Global.RSA, Global.AESKey);
					accessToken = ticket.Item2;

					var user = User.ParseAccessToken(accessToken, Global.RSA, Global.AESKey);
					if (!user.ID.Equals(ticket.Item1) || !user.ID.Equals(userID))
						throw new InvalidTokenException("Token is invalid (User identity is invalid)");

					// assign user credential
					app.Context.User = new UserPrincipal(user);
					var authCookie = new HttpCookie(FormsAuthentication.FormsCookieName)
					{
						Value = AspNetSecurityService.GetAuthenticateToken(userID, accessToken, sessionID, deviceID, FormsAuthentication.Timeout.Minutes),
						HttpOnly = true
					};
					app.Context.Response.SetCookie(authCookie);

					// assign session/device identity
					Global.SetSessionID(app.Context, sessionID);
					Global.SetDeviceID(app.Context, deviceID);
				}
				catch { }

			// prepare
			var requestTo = app.Request.AppRelativeCurrentExecutionFilePath;
			if (requestTo.StartsWith("~/"))
				requestTo = requestTo.Right(requestTo.Length - 2);
			requestTo = string.IsNullOrEmpty(requestTo)
				? ""
				: requestTo.ToLower().ToArray('/', true).First();

			// by-pass segments
			if (Global.BypassSegments.Count > 0 && Global.BypassSegments.Contains(requestTo))
				return;

			// hidden segments
			else if (Global.HiddenSegments.Count > 0 && Global.HiddenSegments.Contains(requestTo))
			{
				app.Context.ShowError(403, "Forbidden", "AccessDeniedException", null);
				app.Context.Response.End();
				return;
			}

			// 403/404 errors
			else if (requestTo.IsEquals("global.ashx"))
			{
				var errorElements = app.Context.Request.QueryString != null && app.Context.Request.QueryString.Count > 0
					? app.Context.Request.QueryString.ToString().UrlDecode().ToArray(';')
					: new string[] { "500", "" };
				var errorMessage = errorElements[0].Equals("403")
					? "Forbidden"
					: errorElements[0].Equals("404")
						? "Not Found"
						: "Unknown (" + errorElements[0] + " : " + (errorElements.Length > 1 ? errorElements[1].Replace(":80", "").Replace(":443", "") : "unknown") + ")";
				var errorType = errorElements[0].Equals("403")
					? "AccessDeniedException"
					: errorElements[0].Equals("404")
						? "FileNotFoundException"
						: "Unknown";
				app.Context.ShowError(errorElements[0].CastAs<int>(), errorMessage, errorType, null);
				app.Context.Response.End();
				return;
			}

#if DEBUG || REQUESTLOGS
			var appInfo = app.Context.GetAppInfo();
			Global.WriteLogs(new List<string>() {
					"Begin process [" + app.Context.Request.HttpMethod + "]: " + app.Context.Request.Url.Scheme + "://" + app.Context.Request.Url.Host + app.Context.Request.RawUrl,
					"- Origin: " + appInfo.Item1 + " / " + appInfo.Item2 + " - " + appInfo.Item3,
					"- IP: " + app.Context.Request.UserHostAddress,
					"- Agent: " + app.Context.Request.UserAgent,
				});

			app.Context.Items["StopWatch"] = new Stopwatch();
			(app.Context.Items["StopWatch"] as Stopwatch).Start();
#endif

			// rewrite url
			var query = "";
			foreach (string key in app.Request.QueryString)
				if (!string.IsNullOrWhiteSpace(key))
					query += (query.Equals("") ? "" : "&") + key + "=" + app.Request.QueryString[key].UrlEncode();

			app.Context.RewritePath(app.Request.ApplicationPath + "Global.ashx", null, query);
		}

		internal static void OnAppEndRequest(HttpApplication app)
		{
#if DEBUG || REQUESTLOGS
			// add execution times
			if (!app.Context.Request.HttpMethod.Equals("OPTIONS") && app.Context.Items.Contains("StopWatch"))
			{
				(app.Context.Items["StopWatch"] as Stopwatch).Stop();
				var executionTimes = (app.Context.Items["StopWatch"] as Stopwatch).GetElapsedTimes();
				Global.WriteLogs("End process - Execution times: " + executionTimes);
				try
				{
					app.Context.Response.Headers.Add("x-execution-times", executionTimes);
				}
				catch { }
			}
#endif
		}
		#endregion

		#region Authenticate request
		internal static void OnAppAuthenticateRequest(HttpApplication app)
		{
			if (app.Context.User == null || !(app.Context.User is UserPrincipal))
			{
				var authCookie = app.Context.Request.Cookies?[FormsAuthentication.FormsCookieName];
				if (authCookie != null)
				{
					var ticket = AspNetSecurityService.ParseAuthenticateToken(authCookie.Value, Global.RSA, Global.AESKey);
					var userID = ticket.Item1;
					var accessToken = ticket.Item2;
					var sessionID = ticket.Item3;
					var deviceID = ticket.Item4;

					app.Context.User = new UserPrincipal(User.ParseAccessToken(accessToken, Global.RSA, Global.AESKey));
					app.Context.Items["Session-ID"] = sessionID;
					app.Context.Items["Device-ID"] = deviceID;
				}
				else
				{
					app.Context.User = new UserPrincipal();
					Global.GetSessionID(app.Context);
					Global.GetDeviceID(app.Context);
				}
			}
		}

		internal static void SetSessionID(HttpContext context, string sessionID)
		{
			context = context ?? HttpContext.Current;
			context.Items["Session-ID"] = sessionID;
			var cookie = new HttpCookie(".VIEApps-Authenticated-Session-ID")
			{
				Value = "VIEApps|" + sessionID.Encrypt(Global.AESKey),
				HttpOnly = true,
				Expires = DateTime.Now.AddDays(180)
			};
			context.Response.SetCookie(cookie);
		}

		internal static string GetSessionID(HttpContext context)
		{
			context = context ?? HttpContext.Current;
			if (!context.Items.Contains("Session-ID"))
			{
				var cookie = context.Request.Cookies?[".VIEApps-Authenticated-Session-ID"];
				if (cookie != null && cookie.Value.StartsWith("VIEApps|"))
					try
					{
						context.Items["Session-ID"] = cookie.Value.ToArray('|').Last().Decrypt(Global.AESKey);
					}
					catch { }
			}
			return context.Items["Session-ID"] as string;
		}

		internal static void SetDeviceID(HttpContext context, string sessionID)
		{
			context = context ?? HttpContext.Current;
			context.Items["Device-ID"] = sessionID;
			var cookie = new HttpCookie(".VIEApps-Authenticated-Device-ID")
			{
				Value = "VIEApps|" + sessionID.Encrypt(Global.AESKey),
				HttpOnly = true,
				Expires = DateTime.Now.AddDays(180)
			};
			context.Response.SetCookie(cookie);
		}

		internal static string GetDeviceID(HttpContext context)
		{
			context = context ?? HttpContext.Current;
			if (!context.Items.Contains("Device-ID"))
			{
				var cookie = context.Request.Cookies?[".VIEApps-Authenticated-Device-ID"];
				if (cookie != null && cookie.Value.StartsWith("VIEApps|"))
					try
					{
						context.Items["Device-ID"] = cookie.Value.ToArray('|').Last().Decrypt(Global.AESKey);
					}
					catch { }
			}
			return context.Items["Device-ID"] as string;
		}
		#endregion

		#region Pre send headers
		internal static void OnAppPreSendHeaders(HttpApplication app)
		{
			// remove un-nessesary headers
			app.Context.Response.Headers.Remove("allow");
			app.Context.Response.Headers.Remove("public");
			app.Context.Response.Headers.Remove("x-powered-by");

			// add special headers
			if (app.Response.Headers["server"] != null)
				app.Response.Headers.Set("server", "VIEApps NGX");
			else
				app.Response.Headers.Add("server", "VIEApps NGX");
		}
		#endregion

		#region Error handlings
		static string ShowErrorStacks = null;

		internal static bool IsShowErrorStacks
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Global.ShowErrorStacks))
#if DEBUG
					Global.ShowErrorStacks = "true";
#else
					Global.ShowErrorStacks = UtilityService.GetAppSetting("ShowErrorStacks", "false");
#endif
				return Global.ShowErrorStacks.IsEquals("true");
			}
		}

		internal static void ShowError(this HttpContext context, int code, string message, string type, string stack)
		{
			context.ShowHttpError(code, message, type, Global.GetCorrelationID(context.Items), stack, Global.IsShowErrorStacks);
		}

		internal static void ShowError(this HttpContext context, Exception exception)
		{
			context.ShowError(exception != null ? exception.GetHttpStatusCode() : 0, exception != null ? exception.Message : "Unknown", exception != null ? exception.GetType().ToString().ToArray('.').Last() : "Unknown", exception != null && Global.IsShowErrorStacks ? exception.StackTrace : null);
		}

		internal static void OnAppError(HttpApplication app)
		{
			var exception = app.Server.GetLastError();
			app.Server.ClearError();

			Global.WriteLogs("", exception);
			app.Context.ShowError(exception);
		}
		#endregion

		#region Get & call services
		internal static async Task<IService> GetServiceAsync(string name)
		{
			if (!Global.Services.TryGetValue(name, out IService service))
			{
				await Global.OpenOutgoingChannelAsync();
				lock (Global.Services)
				{
					if (!Global.Services.TryGetValue(name, out service))
					{
						service = Global.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IService>(new CachedCalleeProxyInterceptor(new ProxyInterceptor(name)));
						Global.Services.Add(name, service);
					}
				}
			}
			return service;
		}

		internal static async Task<JObject> CallServiceAsync(RequestInfo requestInfo, CancellationToken cancellationToken = default(CancellationToken))
		{
			var name = requestInfo.ServiceName.Trim().ToLower();

#if DEBUG
			Global.WriteLogs(requestInfo.CorrelationID, "Call the service [net.vieapps.services." + name + "]" + "\r\n" + requestInfo.ToJson().ToString(Newtonsoft.Json.Formatting.Indented));
#endif

			JObject json = null;
			try
			{
				var service = await Global.GetServiceAsync(name);
				json = await service.ProcessRequestAsync(requestInfo, cancellationToken);
			}
			catch (Exception)
			{
				throw;
			}

#if DEBUG
			Global.WriteLogs(requestInfo.CorrelationID, "Result of the service [net.vieapps.services." + name + "]" + "\r\n" + json.ToString(Newtonsoft.Json.Formatting.Indented));
#endif

			return json;
		}

		internal static async Task<JObject> CallServiceAsync(Session session, string serviceName, string objectName, string verb = "GET", Dictionary<string, string> header = null, Dictionary<string, string> query = null, string body = null, Dictionary<string, string> extra = null, string correlationID = null)
		{
			return await Global.CallServiceAsync(new RequestInfo()
			{
				Session = session ?? Global.GetSession(),
				ServiceName = serviceName ?? "unknown",
				ObjectName = objectName ?? "unknown",
				Verb = string.IsNullOrWhiteSpace(verb) ? "GET" : verb,
				Query = query ?? new Dictionary<string, string>(),
				Header = header ?? new Dictionary<string, string>(),
				Body = body,
				Extra = extra ?? new Dictionary<string, string>(),
				CorrelationID = correlationID ?? Global.GetCorrelationID()
			},
			Global.CancellationTokenSource.Token);
		}
		#endregion

		#region Authentication
		internal static Session GetSession(NameValueCollection header, NameValueCollection query, string agentString, string ipAddress, Uri urlReferrer = null)
		{
			var appInfo = Global.GetAppInfo(header, query, agentString, ipAddress, urlReferrer);
			return new Session()
			{
				IP = ipAddress,
				AppAgent = agentString,
				DeviceID = UtilityService.GetAppParameter("x-device-id", header, query, ""),
				AppName = appInfo.Item1,
				AppPlatform = appInfo.Item2,
				AppOrigin = appInfo.Item3
			};
		}

		internal static Session GetSession(HttpContext context = null)
		{
			context = context ?? HttpContext.Current;
			var session = Global.GetSession(context.Request.Headers, context.Request.QueryString, context.Request.UserAgent, context.Request.UserHostAddress, context.Request.UrlReferrer);
			session.User = context.User as User;
			if (string.IsNullOrWhiteSpace(session.SessionID))
				session.SessionID = Global.GetSessionID(context);
			if (string.IsNullOrWhiteSpace(session.DeviceID))
				session.DeviceID = Global.GetDeviceID(context);
			return session;
		}

		internal static string GetTransferToPassportUrl(HttpContext context = null, string url = null)
		{
			context = context ?? HttpContext.Current;
			url = url ?? context.Request.Url.Scheme + "://" + context.Request.Url.Host + context.Request.RawUrl;
			return Global.HttpUsersUri + "validator"
				+ "?aut=" + (UtilityService.NewUID.Left(5) + "-" + (context.Request.IsAuthenticated ? "ON" : "OFF")).Encrypt(Global.AESKey).ToBase64Url(true)
				+ "&uid=" + (context.Request.IsAuthenticated ? (context.User as User).ID : "").Encrypt(Global.AESKey).ToBase64Url(true)
				+ "&uri=" + url.Encrypt(Global.AESKey).ToBase64Url(true)
				+ "&rdr=" + UtilityService.GetRandomNumber().ToString().Encrypt(Global.AESKey).ToBase64Url(true);
		}

		internal static async Task<bool> ExistsAsync(this Session session)
		{
			var result = await Global.CallServiceAsync(session, "users", "session", "GET", null, null, null, new Dictionary<string, string>()
			{
				{ "Exist", "" }
			});
			var isExisted = result?["Existed"];
			return isExisted != null && isExisted is JValue && (isExisted as JValue).Value != null && (isExisted as JValue).Value.CastAs<bool>() == true;
		}
		#endregion

		#region Authorization
		internal static async Task<bool> CanUploadAsync(string serviceName, string systemID, string definitionID, string objectID)
		{
			var service = await Global.GetServiceAsync(serviceName);
			return await service.CanContributeAsync(HttpContext.Current.User as User, systemID, definitionID, objectID);
		}

		internal static async Task<bool> CanDownloadAsync(string serviceName, string systemID, string definitionID, string objectID)
		{
			var service = await Global.GetServiceAsync(serviceName);
			return await service.CanDownloadAsync(HttpContext.Current.User as User, systemID, definitionID, objectID);
		}

		internal static async Task<bool> CanDeleteAsync(string serviceName, string systemID, string definitionID, string objectID)
		{
			var service = await Global.GetServiceAsync(serviceName);
			return await service.CanEditAsync(HttpContext.Current.User as User, systemID, definitionID, objectID);
		}

		internal static async Task<bool> CanRestoreAsync(string serviceName, string systemID, string definitionID, string objectID)
		{
			var service = await Global.GetServiceAsync(serviceName);
			return await service.CanEditAsync(HttpContext.Current.User as User, systemID, definitionID, objectID);
		}
		#endregion

		#region Send & process inter-communicate message
		static async Task InitializeRTUServiceAsync()
		{
			if (Global._RTUService == null)
			{
				await Global.OpenOutgoingChannelAsync();
				Global._RTUService = Global.OutgoingChannel.RealmProxy.Services.GetCalleeProxy<IRTUService>();
			}
		}

		public static IRTUService RTUService
		{
			get
			{
				if (Global._RTUService == null)
					try
					{
						var task = Global.InitializeRTUServiceAsync();
						task.Wait(1234, Global.CancellationTokenSource.Token);
					}
					catch { }
				return Global._RTUService;
			}
		}

		internal static async Task SendInterCommunicateMessageAsync(CommunicateMessage message)
		{
			try
			{
				await Global.InitializeRTUServiceAsync();
				await Global._RTUService.SendInterCommunicateMessageAsync(message, Global.CancellationTokenSource.Token);
			}
			catch { }
		}

		static void ProcessInterCommunicateMessage(CommunicateMessage message)
		{

		}
		#endregion

		#region Attachment information
		internal static async Task<Attachment> GetAttachmentAsync(string id, Session session = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return string.IsNullOrWhiteSpace(id)
				? null
				: (await Global.CallServiceAsync(new RequestInfo(session ?? Global.GetSession())
					{
						ServiceName = "files",
						ObjectName = "attachment", 
						Verb = "GET",
						Query = new Dictionary<string, string>() { { "object-identity", id } },
						CorrelationID = Global.GetCorrelationID()
					}, cancellationToken)
				 ).FromJson<Attachment>();
		}

		internal static bool IsReadable(this string mime)
		{
			return string.IsNullOrWhiteSpace(mime)
				? false
				: mime.IsStartsWith("image/") || mime.IsStartsWith("video/")
					|| mime.IsStartsWith("text/") || mime.IsStartsWith("application/x-shockwave-flash")
					|| mime.IsEquals("application/pdf") || mime.IsEquals("application/x-pdf");
		}

		internal static bool IsReadable(this Attachment @object)
		{
			return @object != null && @object.ContentType.IsReadable();
		}

		internal static bool IsReadable(this AttachmentInfo @object)
		{
			return @object.ContentType.IsReadable();
		}

		internal static async Task UpdateCounterAsync(HttpContext context, Attachment attachment)
		{
			context = context ?? HttpContext.Current;
			var session = Global.GetSession(context);
			await Task.Delay(0);
		}
		#endregion

		#region  Global settings of the app
		static string _UserAvatarFilesPath = null;

		internal static string UserAvatarFilesPath
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Global._UserAvatarFilesPath))
				{
					Global._UserAvatarFilesPath = UtilityService.GetAppSetting("UserAvatarFilesPath");
					if (string.IsNullOrWhiteSpace(Global._UserAvatarFilesPath))
						Global._UserAvatarFilesPath = HttpRuntime.AppDomainAppPath + @"\data-files\user-avatars";
					if (!Global._UserAvatarFilesPath.EndsWith(@"\"))
						Global._UserAvatarFilesPath += @"\";
				}
				return Global._UserAvatarFilesPath;
			}
		}

		static string _DefaultUserAvatarFilename = null;

		internal static string DefaultUserAvatarFilename
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Global._DefaultUserAvatarFilename))
					Global._DefaultUserAvatarFilename = UtilityService.GetAppSetting("DefaultUserAvatarFileName", "@default.png");
				return Global._DefaultUserAvatarFilename;
			}
		}

		static string _AttachmentFilesPath = null;

		internal static string AttachmentFilesPath
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Global._AttachmentFilesPath))
				{
					Global._AttachmentFilesPath = UtilityService.GetAppSetting("AttachmentFilesPath");
					if (string.IsNullOrWhiteSpace(Global._AttachmentFilesPath))
						Global._AttachmentFilesPath = HttpRuntime.AppDomainAppPath + @"\data-files\attachments";
					if (!Global._AttachmentFilesPath.EndsWith(@"\"))
						Global._AttachmentFilesPath += @"\";
				}
				return Global._AttachmentFilesPath;
			}
		}

		static string _HttpUsersUri = null;

		internal static string HttpUsersUri
		{
			get
			{
				if (string.IsNullOrWhiteSpace(Global._HttpUsersUri))
					Global._HttpUsersUri = UtilityService.GetAppSetting("HttpUsersUri", "https://aid.vieapps.net");
				if (!Global._HttpUsersUri.EndsWith("/"))
					Global._HttpUsersUri += "/";
				return Global._HttpUsersUri;
			}
		}
		#endregion

	}

	// ------------------------------------------------------------------------------

	#region Global.ashx
	public class GlobalHandler : HttpTaskAsyncHandler
	{
		public GlobalHandler() : base() { }

		public override async Task ProcessRequestAsync(HttpContext context)
		{
			// stop process request is OPTIONS
			if (context.Request.HttpMethod.Equals("OPTIONS"))
				return;

			// authentication: process app token
			var appToken = context.Request.Headers["x-app-token"] ?? context.Request.QueryString["x-app-token"];
			if (!string.IsNullOrWhiteSpace(appToken))
				try
				{
					// parse token
					var info = User.ParseJSONWebToken(appToken, Global.AESKey, Global.GenerateJWTKey());
					var userID = info.Item1;
					var accessToken = info.Item2;
					var sessionID = info.Item3;

					// prepare session
					var session = Global.GetSession(context);
					session.SessionID = sessionID;
					context.Items["Session-ID"] = session.SessionID;
					context.Items["Device-ID"] = session.DeviceID;

					// prepare user
					session.User = User.ParseAccessToken(accessToken, Global.RSA, Global.AESKey);
					if (session.User.ID.Equals(userID) && await session.ExistsAsync())
						context.User = new UserPrincipal(session.User);
				}
				catch (Exception ex)
				{
					Global.WriteLogs("Error occurred while processing with authentication", ex);
				}

			// prepare
			var requestTo = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length);
			if (requestTo.StartsWith("/"))
				requestTo = requestTo.Right(requestTo.Length - 1);
			if (requestTo.IndexOf("?") > 0)
				requestTo = requestTo.Left(requestTo.IndexOf("?"));
			requestTo = string.IsNullOrEmpty(requestTo)
				? ""
				: requestTo.ToLower().ToArray('/', true).First();

			// static resources
			if (Global.StaticSegments.Contains(requestTo))
			{
				// check "If-Modified-Since" request to reduce traffict
				var eTag = "StaticResource#" + context.Request.RawUrl.ToLower().GetMD5();
				if (context.Request.Headers["If-Modified-Since"] != null && eTag.Equals(context.Request.Headers["If-None-Match"]))
				{
					context.Response.Cache.SetCacheability(HttpCacheability.Public);
					context.Response.StatusCode = (int)HttpStatusCode.NotModified;
					context.Response.StatusDescription = "Not Modified";
					context.Response.Headers.Add("ETag", "\"" + eTag + "\"");
					return;
				}

				// prepare
				var path = context.Request.RawUrl;
				if (path.IndexOf("?") > 0)
					path = path.Left(path.IndexOf("?"));

				try
				{
					// check exist
					var fileInfo = new FileInfo(context.Server.MapPath(path));
					if (!fileInfo.Exists)
						throw new FileNotFoundException();

					// set cache policy
					context.Response.Cache.SetCacheability(HttpCacheability.Public);
					context.Response.Cache.SetExpires(DateTime.Now.AddDays(1));
					context.Response.Cache.SetSlidingExpiration(true);
					context.Response.Cache.SetOmitVaryStar(true);
					context.Response.Cache.SetValidUntilExpires(true);
					context.Response.Cache.SetLastModified(fileInfo.LastWriteTime);
					context.Response.Cache.SetETag(eTag);

					// write content
					var contentType = path.IsEndsWith(".json") || path.IsEndsWith(".js")
						? "application/" + (path.IsEndsWith(".js") ? "javascript" : "json")
						: "text/"
							+ (path.IsEndsWith(".css")
								? "css"
								: path.IsEndsWith(".html") || path.IsEndsWith(".htm")
									? "html"
									: "plain");
					var staticContent = await UtilityService.ReadTextFileAsync(fileInfo.FullName).ConfigureAwait(false);
					context.Response.ContentType = contentType;
					await context.Response.Output.WriteAsync(contentType.IsEquals("application/json") ? JObject.Parse(staticContent).ToString(Newtonsoft.Json.Formatting.Indented) : staticContent).ConfigureAwait(false);
				}
				catch (FileNotFoundException ex)
				{
					context.ShowError((int)HttpStatusCode.NotFound, "Not found [" + path + "]", "FileNotFoundException", ex.StackTrace);
				}
				catch (Exception ex)
				{
					context.ShowError(ex);
				}
			}

			// files
			else
			{
				// get the handler
				var type = Global.Handlers.ContainsKey(requestTo)
					? Global.Handlers[requestTo]
					: null;

				// do the process
				if (type != null)
					try
					{
						await (type.CreateInstance() as AbstractHttpHandler).ProcessRequestAsync(context, Global.CancellationTokenSource.Token).ConfigureAwait(false);
					}
					catch (OperationCanceledException) { }
					catch (Exception ex)
					{
						context.ShowError(ex);
					}
				else
					context.ShowError(404, "Not Found", "FileNotFoundException", null);
			}
		}
	}
	#endregion

	#region Global.asax
	public class GlobalApp : HttpApplication
	{

		protected void Application_Start(object sender, EventArgs args)
		{
			Global.OnAppStart(sender as HttpContext);
		}

		protected void Application_BeginRequest(object sender, EventArgs args)
		{
			Global.OnAppBeginRequest(sender as HttpApplication);
		}

		protected void Application_AuthenticateRequest(object sender, EventArgs args)
		{
			Global.OnAppAuthenticateRequest(sender as HttpApplication);
		}

		protected void Application_PreSendRequestHeaders(object sender, EventArgs args)
		{
			Global.OnAppPreSendHeaders(sender as HttpApplication);
		}

		protected void Application_EndRequest(object sender, EventArgs args)
		{
			Global.OnAppEndRequest(sender as HttpApplication);
		}

		protected void Application_Error(object sender, EventArgs args)
		{
			Global.OnAppError(sender as HttpApplication);
		}

		protected void Application_End(object sender, EventArgs args)
		{
			Global.OnAppEnd();
		}
	}
	#endregion

}