#region Related components
using System;
using System.Net;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Configuration;
using System.Xml;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WampSharp.V2.Core.Contracts;

using net.vieapps.Components.Caching;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.Files.Storages
{
	public class Handler
	{
		RequestDelegate Next { get; }

		public Handler(RequestDelegate next)
		{
			this.Next = next;
			this.Prepare();
		}

		public async Task Invoke(HttpContext context)
		{
			// process the request
			await this.ProcessRequestAsync(context).ConfigureAwait(false);

			// invoke next middleware
			try
			{
				await this.Next.Invoke(context).ConfigureAwait(false);
			}
			catch (InvalidOperationException) { }
			catch (Exception ex)
			{
				Global.Logger.LogCritical($"Error occurred while invoking the next middleware: {ex.Message}", ex);
			}
		}

		#region Prepare attributes
		bool AlwaysUseSecureConnections { get; set; } = true;
		Dictionary<string, List<string>> Maps { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		string AccountDomain { get; set; } = "company.com";
		string AccountOtp { get; set; } = "AuthenticatorOTP";
		string AccountOtpDomain { get; set; } = "company.com";
		bool AccountOtpSetup { get; set; } = false;
		string DefaultFolder { get; set; } = "~/default";
		bool IncludeSubFolders { get; set; } = false;
		bool ShowNameOfSubFolders { get; set; } = false;
		string SortBy { get; set; } = "Name";
		string SortMode { get; set; } = "Descending";
		bool DirectFlush { get; set; } = false;
		bool OnlyMappedAccounts { get; set; } = false;

		void Prepare()
		{
			// secue connection
			this.AlwaysUseSecureConnections = "true".IsEquals(UtilityService.GetAppSetting("AlwaysUseSecureConnections", "true"));

			// maps
			if (ConfigurationManager.GetSection("net.vieapps.maps") is AppConfigurationSectionHandler config)
			{
				this.AccountDomain = config.Section.Attributes["accountDomain"]?.Value ?? "company.com";
				this.AccountOtp = config.Section.Attributes["accountOtp"]?.Value ?? "AuthenticatorOTP";
				this.AccountOtpDomain = config.Section.Attributes["accountOtpDomain"]?.Value;
				if (string.IsNullOrWhiteSpace(this.AccountOtpDomain))
					this.AccountOtpDomain = this.AccountDomain;
				this.AccountOtpSetup = "adotp".IsEquals(this.AccountOtp) && "true".IsEquals(config.Section.Attributes["accountOtpSetup"]?.Value ?? "false");
				this.DefaultFolder = config.Section.Attributes["defaultFolder"]?.Value ?? "~/default";
				if (this.DefaultFolder.StartsWith("~/"))
					this.DefaultFolder = Global.RootPath + this.DefaultFolder.Right(this.DefaultFolder.Length - 2);
				this.IncludeSubFolders = "true".IsEquals(config.Section.Attributes["includeSubFolders"]?.Value ?? "false");
				this.ShowNameOfSubFolders = "true".IsEquals(config.Section.Attributes["showNameOfSubFolders"]?.Value ?? "false");
				this.SortBy = config.Section.Attributes["sortBy"]?.Value ?? "Name";
				if (string.IsNullOrWhiteSpace(this.SortBy) || (!this.SortBy.IsEquals("Name") && !this.SortBy.IsEquals("Time")))
					this.SortBy = "Name";
				this.SortMode = config.Section.Attributes["sortMode"]?.Value ?? "Descending";
				if (string.IsNullOrWhiteSpace(this.SortMode) || (!this.SortMode.IsEquals("Ascending") && !this.SortMode.IsEquals("ASC") && !this.SortMode.IsEquals("Descending") && !this.SortMode.IsEquals("DESC")))
					this.SortMode = "Descending";
				this.DirectFlush = "true".IsEquals(config.Section.Attributes["directFlush"]?.Value ?? "false");
				this.OnlyMappedAccounts = "true".IsEquals(config.Section.Attributes["onlyMappedAccounts"]?.Value ?? "false");

				if (config.Section.SelectNodes("map") is XmlNodeList maps)
					maps.ToList().ForEach(map =>
					{
						var account = map.Attributes["account"]?.Value;
						var folder = map.Attributes["folder"]?.Value;
						if (string.IsNullOrWhiteSpace(folder))
							folder = map.Attributes["folders"]?.Value;
						if (!string.IsNullOrWhiteSpace(account) && !this.Maps.ContainsKey(account.Trim().ToLower()))
						{
							var folders = string.IsNullOrWhiteSpace(folder)
								? this.DefaultFolder.Trim().ToLower().ToList(';')
								: folder.ToList(';').Select(f => (f.StartsWith("~/") ? Global.RootPath + f.Right(f.Length - 2) : f).Trim().ToLower()).ToList();
							this.Maps.Add(account.Trim().ToLower(), folders);
						}
					});
			}

			Global.Logger.LogInformation(
				$"==> Domain: {this.AccountDomain}" + "\r\n" +
				$"==> OTP: {(this.AccountOtp.Equals("") ? "None" : this.AccountOtp)} [{this.AccountOtpDomain}]" + "\r\n" +
				$"==> Default Folder: {this.DefaultFolder} [Include sub-folders: {this.IncludeSubFolders}]" + "\r\n" +
				$"==> Sort: {this.SortBy} {this.SortMode}" + "\r\n" +
				$"==> Maps: \r\n\t\t{string.Join("\r\n\t\t", this.Maps.Select(m => $"{m.Key} ({m.Value.ToString(" - ")})"))}"
			);
		}
		#endregion

		internal async Task ProcessRequestAsync(HttpContext context)
		{
			// prepare
			context.Items["PipelineStopwatch"] = Stopwatch.StartNew();

			var requestUri = context.GetRequestUri();
			var requestPath = requestUri.GetRequestPathSegments().First().ToLower();

			// favicon.ico
			if (requestPath.IsEquals("favicon.ico"))
				context.ShowHttpError((int)HttpStatusCode.NotFound, "Not Found", "FileNotFoundException", context.GetCorrelationID());

			// static segments
			else if (Global.StaticSegments.Contains(requestPath))
				await this.ProcessStaticFileRequestAsync(context).ConfigureAwait(false);

			// other
			else
			{
				if (this.AlwaysUseSecureConnections && !requestUri.Scheme.IsEquals("https"))
					context.Redirect($"{requestUri}".Replace("http://", "https://"));
				else
					await this.ProcessStorageRequestAsync(context).ConfigureAwait(false);
			}
		}

		#region Static files
		internal async Task ProcessStaticFileRequestAsync(HttpContext context)
		{
			var requestUri = context.GetRequestUri();
			try
			{
				// prepare
				FileInfo fileInfo = null;
				var pathSegments = requestUri.GetRequestPathSegments();

				var filePath = (pathSegments[0].IsEquals("statics")
					? UtilityService.GetAppSetting("Path:StaticFiles", Global.RootPath + "/data-files/statics")
					: Global.RootPath) + ("/" + string.Join("/", pathSegments)).Replace("//", "/").Replace(@"\", "/").Replace('/', Path.DirectorySeparatorChar);
				if (pathSegments[0].IsEquals("statics"))
					filePath = filePath.Replace($"{Path.DirectorySeparatorChar}statics{Path.DirectorySeparatorChar}statics{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}statics{Path.DirectorySeparatorChar}");

				// headers to reduce traffic
				var eTag = "Static#" + $"{requestUri}".ToLower().GenerateUUID();
				if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")))
				{
					var isNotModified = true;
					var lastModifed = DateTime.Now.ToUnixTimestamp();
					if (context.GetHeaderParameter("If-Modified-Since") != null)
					{
						fileInfo = new FileInfo(filePath);
						if (fileInfo.Exists)
						{
							lastModifed = fileInfo.LastWriteTime.ToUnixTimestamp();
							isNotModified = lastModifed <= context.GetHeaderParameter("If-Modified-Since").FromHttpDateTime().ToUnixTimestamp();
						}
						else
							isNotModified = false;
					}
					if (isNotModified)
					{
						context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, lastModifed, "public", context.GetCorrelationID());
						if (Global.IsDebugLogEnabled)
							await context.WriteLogsAsync("StaticFiles", $"Success response with status code 304 to reduce traffic ({filePath})").ConfigureAwait(false);
						return;
					}
				}

				// check existed
				fileInfo = fileInfo ?? new FileInfo(filePath);
				if (!fileInfo.Exists)
					throw new FileNotFoundException($"Not Found [{requestUri}]");

				// prepare body
				var fileMimeType = fileInfo.GetMimeType();
				var fileContent = fileMimeType.IsEndsWith("json")
					? JObject.Parse(await UtilityService.ReadTextFileAsync(fileInfo, null, Global.CancellationTokenSource.Token).ConfigureAwait(false)).ToString(Newtonsoft.Json.Formatting.Indented).ToBytes()
					: await UtilityService.ReadBinaryFileAsync(fileInfo, Global.CancellationTokenSource.Token).ConfigureAwait(false);

				// response
				context.SetResponseHeaders((int)HttpStatusCode.OK, new Dictionary<string, string>
				{
					{ "Content-Type", $"{fileMimeType}; charset=utf-8" },
					{ "ETag", eTag },
					{ "Last-Modified", $"{fileInfo.LastWriteTime.ToHttpString()}" },
					{ "Cache-Control", "public" },
					{ "Expires", $"{DateTime.Now.AddDays(7).ToHttpString()}" },
					{ "X-CorrelationID", context.GetCorrelationID() }
				});
				await Task.WhenAll(
					context.WriteAsync(fileContent, Global.CancellationTokenSource.Token),
					!Global.IsDebugLogEnabled ? Task.CompletedTask : context.WriteLogsAsync("StaticFiles", $"Success response ({filePath} - {fileInfo.Length:#,##0} bytes)")
				).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await context.WriteLogsAsync("StaticFiles", $"Failure response [{requestUri}]", ex).ConfigureAwait(false);
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
			}
		}
		#endregion

		internal async Task ProcessStorageRequestAsync(HttpContext context)
		{
			var requestPath = context.GetRequestPathSegments().First().ToLower();

			if (requestPath.IsEquals("_signin"))
				await this.ProcessRequestOfSignInAsync(context).ConfigureAwait(false);

			else if (requestPath.IsEquals("_signout"))
			{
				await context.SignOutAsync().ConfigureAwait(false);
				context.User = new UserPrincipal();
				context.Redirect("/_signin");
			}

			else
			{
				if (!context.User.Identity.IsAuthenticated)
					context.Redirect("/_signin");

				else if (!requestPath.IsEquals("") && !requestPath.IsEquals("/"))
					await this.ProcessDownloadRequestAsync(context).ConfigureAwait(false);

				else
					await this.ProcessBrowseRequestAsync(context).ConfigureAwait(false);
			}
		}

		#region Show listing of files
		async Task ProcessBrowseRequestAsync(HttpContext context)
		{
			// get files
			var paths = (this.Maps.ContainsKey(context.User.Identity.Name)
				? this.Maps[context.User.Identity.Name]
				: null) ?? this.DefaultFolder.Trim().ToLower().ToList(';');

			List<FileInfo> files = null;
			await paths.ForEachAsync(async (path, index, token) =>
			{
				files = files == null
					? await UtilityService.GetFilesAsync(path, "*.*", this.IncludeSubFolders, null, "Name", "ASC", token).ConfigureAwait(false)
					: files.Concat(await UtilityService.GetFilesAsync(path, "*.*", this.IncludeSubFolders, null, "Name", "ASC", token).ConfigureAwait(false)).ToList();
			}, Global.CancellationTokenSource.Token, true, false).ConfigureAwait(false);

			// sort
			if (this.SortBy.IsEquals("Name"))
				files = this.SortMode.IsEquals("Descending")
					? files.OrderByDescending(file => file.FullName).ThenByDescending(file => file.LastWriteTime).ToList()
					: files.OrderBy(file => file.FullName).ThenByDescending(file => file.LastWriteTime).ToList();
			else
				files = this.SortMode.IsEquals("Descending")
					? files.OrderByDescending(file => file.LastWriteTime).ThenByDescending(file => file.FullName).ToList()
					: files.OrderBy(file => file.LastWriteTime).ThenBy(file => file.FullName).ToList();

			// prepare html
			var folders = new Dictionary<string, string>();
			paths.ForEach((p, i) => folders.Add(p, i.ToString()));

			var html = "";
			files.ForEach(file =>
			{
				var path = file.FullName.Substring(0, file.FullName.Length - file.Name.Length - 1).ToLower();
				var fileUri = folders.ContainsKey(path)
					? folders[path]
					: null;
				if (fileUri == null)
				{
					var folderPath = paths.First(p => path.IsStartsWith(p));
					fileUri = folders[folderPath] + path.Substring(folderPath.Length).Replace(@"\", "/");
				}
				fileUri += "/" + file.Name;
				html += "<div><span><a href=\"" + fileUri + "\">"
					+ (this.ShowNameOfSubFolders ? fileUri : file.Name)
					+ "</a></span>"
					+ "<label>" + file.LastWriteTime.ToString("hh:mm tt @ dd/MM/yyyy") + "</label><label>" + UtilityService.GetFileSize(file) + "</label></div>" + "\r\n";
			});

			await context.WriteAsync(this.GetBeginHtml(context) + html + this.GetEndHtml(context), "text/html", null, 0, "", TimeSpan.Zero).ConfigureAwait(false);
		}
		#endregion

		#region Download a file
		async Task ProcessDownloadRequestAsync(HttpContext context)
		{
			try
			{
				var paths = (this.Maps.ContainsKey(context.User.Identity.Name)
					? this.Maps[context.User.Identity.Name]
					: null) ?? new List<string> { this.DefaultFolder.Trim().ToLower() };
				var folders = new Dictionary<string, string>();
				paths.ForEach((p, i) => folders.Add(i.ToString(), p));

				paths = context.GetRequestPathSegments().ToList();
				var path = folders[paths[0]] + Path.DirectorySeparatorChar.ToString() + paths.Skip(1).ToString(Path.DirectorySeparatorChar.ToString());

				var fileInfo = new FileInfo(path);
				var headers = new Dictionary<string, string>
				{
					{ "Content-Type", fileInfo.GetMimeType() },
					{ "ETag", fileInfo.FullName.GetMD5() }
				};

				if (!this.DirectFlush)
					headers["Content-Disposition"] = $"Attachment; Filename=\"{fileInfo.Name.UrlEncode()}\"";

				using (var stream = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, TextFileReader.BufferSize, true))
				{
					await context.WriteAsync(stream, headers, Global.CancellationTokenSource.Token).ConfigureAwait(false);
				}
			}
			catch (Exception ex)
			{
				await context.WriteLogsAsync("Download", "Error occurred while processing", ex).ConfigureAwait(false);
				context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), UtilityService.NewUUID, ex, true);
			}
		}
		#endregion

		#region Sign in
		async Task ProcessRequestOfSignInAsync(HttpContext context)
		{
			var error = "";
			if (context.Request.Method.IsEquals("POST"))
				try
				{
					// prepare
					var account = context.Request.Form["Account"].First();
					var password = context.Request.Form["Password"].First();
					var otp = context.Request.Form["OTP"].First();

					if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
						throw new WrongAccountException();

					// sign-in with Windows AD
					var body = new JObject()
					{
						{ "Type", "Windows" },
						{ "Email", (account.Trim().ToLower() + "@" + this.AccountDomain).Encrypt(Global.EncryptionKey) },
						{ "Password", password.Encrypt(Global.EncryptionKey) },
					}.ToString(Newtonsoft.Json.Formatting.None);

					await context.CallServiceAsync(new RequestInfo(context.GetSession(), "Users", "Session", "PUT")
					{
						Body = body,
						Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
						{
							{ "Signature", body.GetHMACSHA256(Global.ValidationKey) },
							{ "x-no-account", "" }
						}
					}).ConfigureAwait(false);

					// validate with OTP
					if (!this.AccountOtp.Equals(""))
					{
						if (string.IsNullOrWhiteSpace(otp))
							throw new WrongAccountException();

						await context.CallServiceAsync(new RequestInfo(context.GetSession(), this.AccountOtp)
						{
							Extra = this.AccountOtp.IsEquals("AuthenticatorOTP")
							? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
							{
								{ "ID", account.Trim().ToLower().Encrypt(Global.EncryptionKey) },
								{ "Stamp", $"ADOTP#{this.AccountOtpDomain}".Encrypt(Global.EncryptionKey) },
								{ "Password", otp.Encrypt(Global.EncryptionKey) }
							}
							: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
							{
								{ "Domain", this.AccountOtpDomain },
								{ "Account", account.Trim() },
								{ "OTP", otp }
							}
						}).ConfigureAwait(false);
					}

					// only mapped account
					if (this.OnlyMappedAccounts && !this.Maps.ContainsKey(account.Trim().ToLower()))
						throw new UnauthorizedException("Your account is not authorized to complete the request!");

					// update authenticate ticket
					var userIdentity = new UserIdentity(account.Trim(), context.Session.Get<string>("SessionID") ?? UtilityService.NewUUID, CookieAuthenticationDefaults.AuthenticationScheme);
					var userPrincipal = new UserPrincipal(userIdentity);
					await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, userPrincipal, new AuthenticationProperties { IsPersistent = false });
					context.User = userPrincipal;

					// redirect to home
					context.Redirect("/");
					return;
				}
				catch (WampException exception)
				{
					await context.WriteLogsAsync("SignIn", "Error occurred while signing-in", exception).ConfigureAwait(false);
					error = exception.GetDetails().Item2;
				}
				catch (Exception exception)
				{
					await context.WriteLogsAsync("SignIn", "Error occurred while signing-in", exception).ConfigureAwait(false);
					error = exception.Message;
				}

			var html = @"<form method='post' action='_signin' autocomplete='off'>" + (!error.Equals("") ? "<span>" + error + "</span>" : "") + @"
				<div><label>Account:</label><input type='text' id='Account' name='Account' maxlength='120'/></div>
				<div><label>Password:</label><input type='password' id='Password' name='Password' maxlength='120'/></div>" +
				(!this.AccountOtp.Equals("") ? "<div><label>OTP token:</label><input type='text' id='OTP' name='OTP' maxlength='11'/></div>" : "") + @"
				<section><input type='submit' value='Sign In'/>" + (this.AccountOtpSetup ? "<span><a href='./_setup'>Setup</a></span>" : "") + @"</section>
				</form>
				<script>document.getElementById('Account').focus()</script>";

			await context.WriteAsync(this.GetBeginHtml(context) + html + this.GetEndHtml(context), "text/html", null, 0, "", TimeSpan.Zero).ConfigureAwait(false);
		}
		#endregion

		#region Setup OTP
		#endregion

		#region Begin/End of a HTML page
		string GetBeginHtml(HttpContext context)
		{
			var requestUri = context.GetRequestUri();
			return (@"<!DOCTYPE html>
			<html xmlns='http://www.w3.org/1999/xhtml'>
			<head>
			<title>" + requestUri.Host.ToArray('.').First() + @" - VIEApps NGX File HTTP Storages</title>
			<meta name='viewport' content='width=device-width, initial-scale=1'/>
			<link rel='stylesheet' type='text/css' href='./_assets/style.css'/>
			</head>
			<body>
			<header>
			<h1>" + requestUri.Host.ToArray('.').First() + @"</h1>" + (context.User.Identity.IsAuthenticated ? "<span><a href='./'>Refresh</a></span>" : "") + @"
			</header>
			<div>
			").Replace("\t\t\t", "");
		}

		string GetEndHtml(HttpContext context)
		{
			var requestUri = context.GetRequestUri();
			return ("</div>" + @"			
			<footer>
			<div><span>&copy; " + DateTime.Now.Year + " " + requestUri.Host + @"</span></div>" +
			(context.User.Identity.IsAuthenticated ? "<span><label>" + context.User.Identity.Name + "</label><a href='./_signout'>(sign out)</a></span>" : "") + @"
			</footer>
			</body>
			</html>").Replace("\t\t\t", "");
		}
		#endregion

		#region Helper: WAMP connections
		internal static void OpenWAMPChannels(int waitingTimes = 6789)
		{
			Global.Logger.LogInformation($"Attempting to connect to WAMP router [{WAMPConnections.GetRouterStrInfo()}]");
			Global.OpenWAMPChannels(
				(sender, args) =>
				{
					Global.Logger.LogInformation($"Incomming channel to WAMP router is established - Session ID: {args.SessionId}");
					Global.InterCommunicateMessageUpdater = WAMPConnections.IncommingChannel.RealmProxy.Services
						.GetSubject<CommunicateMessage>("net.vieapps.rtu.communicate.messages.storages")
						.Subscribe(
							async (message) => await Handler.ProcessInterCommunicateMessageAsync(message).ConfigureAwait(false),
							exception => Global.WriteLogs(Global.Logger, "RTU", $"{exception.Message}", exception)
						);
				},
				(sender, args) =>
				{
					Global.Logger.LogInformation($"Outgoing channel to WAMP router is established - Session ID: {args.SessionId}");
					try
					{
						Task.WaitAll(new[] { Global.InitializeLoggingServiceAsync(), Global.InitializeRTUServiceAsync() }, waitingTimes > 0 ? waitingTimes : 6789, Global.CancellationTokenSource.Token);
						Global.Logger.LogInformation("Helper services are succesfully initialized");
					}
					catch (Exception ex)
					{
						Global.Logger.LogError($"Error occurred while initializing helper services: {ex.Message}", ex);
					}
				},
				waitingTimes
			);
		}

		static Task ProcessInterCommunicateMessageAsync(CommunicateMessage message) => Task.CompletedTask;
		#endregion

	}
}