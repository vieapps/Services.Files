#region Related components
using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Configuration;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;
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
		string LoadBalancingHealthCheckUrl { get; } = UtilityService.GetAppSetting("HealthCheckUrl", "/load-balancing-health-check");

		public Handler(RequestDelegate _)
			=> this.Prepare();

		public async Task Invoke(HttpContext context)
		{
			// load balancing health check
			if (context.Request.Path.Value.IsEquals(this.LoadBalancingHealthCheckUrl))
				await context.WriteAsync("OK", "text/plain", null, 0, null, TimeSpan.Zero, null, Global.CancellationTokenSource.Token).ConfigureAwait(false);

			// request of storages
			else
				await this.ProcessRequestAsync(context).ConfigureAwait(false);
		}

		#region Prepare attributes
		Dictionary<string, List<string>> Maps { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		string AccountDomain { get; set; } = "company.com";
		string AccountOtp { get; set; } = "AuthenticatorOTP";
		string AccountOtpDomain { get; set; } = "company.com";
		bool AccountOtpSetup { get; set; } = false;
		string DefaultDirectory { get; set; } = "~/default";
		bool IncludeSubDirectories { get; set; } = false;
		bool ShowNameOfSubDirectories { get; set; } = false;
		string SortBy { get; set; } = "Name";
		string SortMode { get; set; } = "Descending";
		bool DirectFlush { get; set; } = false;
		bool OnlyMappedAccounts { get; set; } = false;
		bool RedirectToHTTPS { get; set; } = false;

		void Prepare()
		{
			if (ConfigurationManager.GetSection("net.vieapps.services.files.http.storages.maps") is AppConfigurationSectionHandler svcConfig)
			{
				this.AccountDomain = svcConfig.Section.Attributes["accountDomain"]?.Value ?? "company.com";
				this.AccountOtp = svcConfig.Section.Attributes["accountOtp"]?.Value ?? "AuthenticatorOTP";
				this.AccountOtpDomain = svcConfig.Section.Attributes["accountOtpDomain"]?.Value;
				if (string.IsNullOrWhiteSpace(this.AccountOtpDomain))
					this.AccountOtpDomain = this.AccountDomain;
				this.AccountOtpSetup = "adotp".IsEquals(this.AccountOtp) && "true".IsEquals(svcConfig.Section.Attributes["accountOtpSetup"]?.Value ?? "false");
				this.DefaultDirectory = svcConfig.Section.Attributes["defaultDirectory"]?.Value ?? "~/default";
				if (this.DefaultDirectory.StartsWith("~/"))
					this.DefaultDirectory = Global.RootPath + this.DefaultDirectory.Right(this.DefaultDirectory.Length - 2);
				this.IncludeSubDirectories = "true".IsEquals(svcConfig.Section.Attributes["includeSubDirectories"]?.Value ?? "false");
				this.ShowNameOfSubDirectories = "true".IsEquals(svcConfig.Section.Attributes["showNameOfDirectories"]?.Value ?? "false");
				this.SortBy = svcConfig.Section.Attributes["sortBy"]?.Value ?? "Name";
				if (string.IsNullOrWhiteSpace(this.SortBy) || (!this.SortBy.IsEquals("Name") && !this.SortBy.IsEquals("Time")))
					this.SortBy = "Name";
				this.SortMode = svcConfig.Section.Attributes["sortMode"]?.Value ?? "Descending";
				if (string.IsNullOrWhiteSpace(this.SortMode) || (!this.SortMode.IsEquals("Ascending") && !this.SortMode.IsEquals("ASC") && !this.SortMode.IsEquals("Descending") && !this.SortMode.IsEquals("DESC")))
					this.SortMode = "Descending";
				this.DirectFlush = "true".IsEquals(svcConfig.Section.Attributes["directFlush"]?.Value ?? "false");
				this.OnlyMappedAccounts = "true".IsEquals(svcConfig.Section.Attributes["onlyMappedAccounts"]?.Value ?? "false");
				this.RedirectToHTTPS = "true".IsEquals(svcConfig.Section.Attributes["redirectToHTTPS"]?.Value);

				if (svcConfig.Section.SelectNodes("map") is XmlNodeList maps)
					maps.ToList().ForEach(map =>
					{
						var account = map.Attributes["account"]?.Value;
						var directory = map.Attributes["directory"]?.Value;
						if (string.IsNullOrWhiteSpace(directory))
							directory = map.Attributes["directories"]?.Value;
						if (!string.IsNullOrWhiteSpace(account) && !this.Maps.ContainsKey(account.Trim().ToLower()))
						{
							var directories = string.IsNullOrWhiteSpace(directory)
								? this.DefaultDirectory.Trim().ToLower().ToList(';')
								: directory.ToList(';').Select(f => (f.StartsWith("~/") ? Global.RootPath + f.Right(f.Length - 2) : f).Trim().ToLower()).ToList();
							this.Maps.Add(account.Trim().ToLower(), directories);
						}
					});
			}

			Global.Logger.LogInformation("Settings:" + "\r\n" +
				$"==> Domain: {this.AccountDomain}" + "\r\n" +
				$"==> OTP: {(this.AccountOtp.Equals("") ? "None" : this.AccountOtp)} [{this.AccountOtpDomain}]" + "\r\n" +
				$"==> Default directory: {this.DefaultDirectory} [Include sub-directories: {this.IncludeSubDirectories}]" + "\r\n" +
				$"==> Sort: {this.SortBy} {this.SortMode}" + "\r\n" +
				$"==> Maps: \r\n\t\t{(this.Maps.Count < 1? "None" : this.Maps.Select(m => $"{m.Key} ({m.Value.ToString(" - ")})").Join("\r\n\t\t"))}" + "\r\n" +
				$"==> Redirect to HTTPS: {this.RedirectToHTTPS}"
			);
		}
		#endregion

		internal async Task ProcessRequestAsync(HttpContext context)
		{
			// prepare
			context.SetItem("PipelineStopwatch", Stopwatch.StartNew());
			var requestUri = context.GetRequestUri();
			var requestPath = requestUri.GetRequestPathSegments(true).First();

			if (Global.IsVisitLogEnabled)
				await context.WriteVisitStartingLogAsync().ConfigureAwait(false);

			// redirect to HTTPs
			if (this.RedirectToHTTPS && !requestUri.Scheme.IsEquals("https"))
			{
				context.Redirect($"{requestUri}".Replace("http://", "https://"));
				return;
			}

			// request to favicon.ico file
			if (requestPath.IsEquals("favicon.ico"))
				await context.ProcessFavouritesIconFileRequestAsync().ConfigureAwait(false);

			// request to static segments
			else if (Global.StaticSegments.Contains(requestPath))
				await context.ProcessStaticFileRequestAsync().ConfigureAwait(false);

			// request to file storages
			else
			{
				if (requestPath.IsEquals("_signin"))
					await this.ProcessRequestOfSignInAsync(context).ConfigureAwait(false);

				else if (requestPath.IsEquals("_signout"))
				{
					await context.SignOutAsync().ConfigureAwait(false);
					context.User = new UserPrincipal();
					context.Redirect("/_signin");
				}

				else if (requestPath.IsEquals("_changepassword"))
				{
					if (!context.User.Identity.IsAuthenticated)
						context.Redirect("/_signin");
					else
						await this.ProcessRequestOfChangePasswordAsync(context).ConfigureAwait(false);
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

			if (Global.IsVisitLogEnabled)
				await context.WriteVisitFinishingLogAsync().ConfigureAwait(false);
		}

		#region Show listing of files
		async Task ProcessBrowseRequestAsync(HttpContext context)
		{
			// get files
			var paths = (this.Maps.ContainsKey(context.User.Identity.Name)
				? this.Maps[context.User.Identity.Name]
				: null) ?? this.DefaultDirectory.Trim().ToLower().ToList(';');

			List<FileInfo> files = null;
			await paths.ForEachAsync(async (path, index, token) =>
			{
				files = files == null
					? await UtilityService.GetFilesAsync(path, "*.*", this.IncludeSubDirectories, null, "Name", "ASC", token).ConfigureAwait(false)
					: files.Concat(await UtilityService.GetFilesAsync(path, "*.*", this.IncludeSubDirectories, null, "Name", "ASC", token).ConfigureAwait(false)).ToList();
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
			var directories = new Dictionary<string, string>();
			paths.ForEach((p, i) => directories.Add(p, i.ToString()));

			var html = "";
			files.ForEach(file =>
			{
				var path = file.FullName.Substring(0, file.FullName.Length - file.Name.Length - 1).ToLower();
				var fileUri = directories.ContainsKey(path)
					? directories[path]
					: null;
				if (fileUri == null)
				{
					var directoryPath = paths.First(p => path.IsStartsWith(p));
					fileUri = directories[directoryPath] + path.Substring(directoryPath.Length).Replace(@"\", "/");
				}
				fileUri += "/" + file.Name;
				html += "<div><span><a href=\"" + fileUri + "\">"
					+ (this.ShowNameOfSubDirectories ? fileUri : file.Name)
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
					: null) ?? new List<string> { this.DefaultDirectory.Trim().ToLower() };
				var folders = new Dictionary<string, string>();
				paths.ForEach((p, i) => folders.Add(i.ToString(), p));

				paths = context.GetRequestPathSegments().ToList();
				var path = folders[paths[0]] + Path.DirectorySeparatorChar.ToString() + paths.Skip(1).ToString(Path.DirectorySeparatorChar.ToString());

				var fileInfo = new FileInfo(path);
				var headers = new Dictionary<string, string>
				{
					{ "Content-Type", $"{fileInfo.GetMimeType()}; charset=utf-8" },
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
				await context.WriteLogsAsync("Http.Download", "Error occurred while processing", ex).ConfigureAwait(false);
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
					var username = context.Request.Form["Username"].First();
					var password = context.Request.Form["Password"].First();
					var otp = context.Request.Form["OTP"].First();

					if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
						throw new WrongAccountException();

					// sign-in with Windows AD
					var body = new JObject
					{
						{ "Domain", this.AccountDomain.Encrypt(Global.EncryptionKey) },
						{ "Username", username.Trim().ToLower().Encrypt(Global.EncryptionKey) },
						{ "Password", password.Encrypt(Global.EncryptionKey) },
					}.ToString(Newtonsoft.Json.Formatting.None);

					var session = context.GetSession();
					session.AppName = "Files Storages HTTP Service";

					await context.CallServiceAsync(new RequestInfo(session, "WindowsAD", "Account")
					{
						Verb = "POST",
						Body = body,
						Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
						{
							{ "Signature", body.GetHMACSHA256(Global.ValidationKey) }
						}
					}).ConfigureAwait(false);

					// validate with OTP
					if (!this.AccountOtp.Equals(""))
					{
						if (string.IsNullOrWhiteSpace(otp))
							throw new WrongAccountException();

						await context.CallServiceAsync(new RequestInfo(session, this.AccountOtp)
						{
							Extra = this.AccountOtp.IsEquals("AuthenticatorOTP")
							? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
							{
								{ "ID", username.Trim().ToLower().Encrypt(Global.EncryptionKey) },
								{ "Stamp", $"ADOTP#{this.AccountOtpDomain}".Encrypt(Global.EncryptionKey) },
								{ "Password", otp.Encrypt(Global.EncryptionKey) }
							}
							: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
							{
								{ "Domain", this.AccountOtpDomain },
								{ "Account", username.Trim() },
								{ "OTP", otp }
							}
						}).ConfigureAwait(false);
					}

					// only mapped account
					if (this.OnlyMappedAccounts && !this.Maps.ContainsKey(username.Trim().ToLower()))
						throw new UnauthorizedException("Your account is not authorized to complete the request!");

					// update authenticate ticket
					var userPrincipal = new UserPrincipal(new UserIdentity(username.Trim(), context.Session.Get<string>("SessionID") ?? UtilityService.NewUUID, CookieAuthenticationDefaults.AuthenticationScheme));
					await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, userPrincipal, new AuthenticationProperties { IsPersistent = false });
					context.User = userPrincipal;

					// remove the awaiting flag
					await Global.Cache.RemoveAsync($"SRP:Attempt#{context.Connection.RemoteIpAddress}").ConfigureAwait(false);

					// redirect to home
					context.Redirect("/");
					return;
				}
				catch (Exception exception)
				{
					// wait
					var attempt = await Global.Cache.ExistsAsync($"SRP:Attempt#{context.Connection.RemoteIpAddress}").ConfigureAwait(false)
						? await Global.Cache.GetAsync<int>($"SRP:Attempt#{context.Connection.RemoteIpAddress}").ConfigureAwait(false)
						: 0;
					attempt++;

					await Task.WhenAll(
						Task.Delay(567 + ((attempt - 1) * 5678)),
						Global.Cache.SetAsync($"SRP:Attempt#{context.Connection.RemoteIpAddress}", attempt),
						context.WriteLogsAsync("Http.SignIn", $"Failure attempt to sign-in ({attempt:#,##0} - {context.Connection.RemoteIpAddress}): {exception.Message}", exception)
					).ConfigureAwait(false);

					// prepare error
					error = exception is WampException
						? (exception as WampException).GetDetails().Item2
						: exception.Message;
				}

			var html = @"<form method='post' action='_signin' autocomplete='off'>" + (!error.Equals("") ? "<span>" + error + "</span>" : "") + @"
				<div><label>Username:</label><input type='text' id='Username' name='Username' maxlength='120'/></div>
				<div><label>Password:</label><input type='password' id='Password' name='Password' maxlength='120'/></div>" +
				(!this.AccountOtp.Equals("") ? "<div><label>OTP token:</label><input type='text' id='OTP' name='OTP' maxlength='11'/></div>" : "") + @"
				<section><input type='submit' value='Sign In'/>" + (this.AccountOtpSetup ? "<span><a href='./_setup'>Setup</a></span>" : "") + @"</section>
				</form>
				<script>document.getElementById('Username').focus()</script>";

			await context.WriteAsync(this.GetBeginHtml(context) + html + this.GetEndHtml(context), "text/html", null, 0, "", TimeSpan.Zero).ConfigureAwait(false);
		}
		#endregion

		#region Change passowrd
		async Task ProcessRequestOfChangePasswordAsync(HttpContext context)
		{
			var error = "";
			if (context.Request.Method.IsEquals("POST"))
				try
				{
					// prepare
					var oldPassword = context.Request.Form["OldPassword"].First();
					var newPassword = context.Request.Form["NewPassword"].First();

					if (string.IsNullOrWhiteSpace(oldPassword) || string.IsNullOrWhiteSpace(newPassword))
						throw new WrongAccountException();

					// sign-in with Windows AD
					var body = new JObject
					{
						{ "Domain", this.AccountDomain.Encrypt(Global.EncryptionKey) },
						{ "Username", context.User.Identity.Name.Encrypt(Global.EncryptionKey) },
						{ "Password", newPassword.Encrypt(Global.EncryptionKey) },
						{ "OldPassword", oldPassword.Trim().ToLower().Encrypt(Global.EncryptionKey) },
					}.ToString(Newtonsoft.Json.Formatting.None);

					var session = context.GetSession();
					session.AppName = "Files Storages HTTP Service";

					await context.CallServiceAsync(new RequestInfo(session, "WindowsAD", "Account")
					{
						Verb = "PUT",
						Body = body,
						Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
						{
							{ "Signature", body.GetHMACSHA256(Global.ValidationKey) }
						}
					}).ConfigureAwait(false);

					// redirect to home
					context.Redirect("/");
					return;
				}
				catch (Exception exception)
				{
					await context.WriteLogsAsync("Http.Password", $"Failure attempt to change password ({context.Connection.RemoteIpAddress})", exception).ConfigureAwait(false);

					// prepare error
					error = exception is WampException
						? (exception as WampException).GetDetails().Item2
						: exception.Message;
				}

			var html = @"<form method='post' action='_changepassword' autocomplete='off'>" + (!error.Equals("") ? "<span>" + error + "</span>" : "") + @"
				<div><label>Old Password:</label><input type='password' id='OldPassword' name='OldPassword' maxlength='120'/></div>
				<div><label>New Password:</label><input type='password' id='NewPassword' name='NewPassword' maxlength='120'/></div>
				<section><input type='submit' value='Change password'/><span><a href='./'>Cancel</a></span></section>
				</form>
				<script>document.getElementById('OldPassword').focus()</script>";

			await context.WriteAsync(this.GetBeginHtml(context) + html + this.GetEndHtml(context), "text/html", null, 0, "", TimeSpan.Zero).ConfigureAwait(false);
		}
		#endregion

		#region Setup OTP
		#endregion

		#region Begin/End of a HTML page
		string GetBeginHtml(HttpContext context)
		{
			var host = context.GetRequestUri().Host.ToArray('.').First();
			return (@"<!DOCTYPE html>
			<html xmlns='http://www.w3.org/1999/xhtml'>
			<head>
			<title>" + host + @" - VIEApps NGX File HTTP Storages</title>
			<meta name='viewport' content='width=device-width, initial-scale=1'/>
			<link rel='stylesheet' type='text/css' href='./_assets/style.css'/>
			</head>
			<body>
			<header>
			<h1>" + host + @"</h1>" + (context.User.Identity.IsAuthenticated ? "<span><a href='./'>Refresh</a></span>" : "") + @"
			</header>
			<div>
			").Replace("\t\t\t", "");
		}

		string GetEndHtml(HttpContext context)
		{
			return ("</div>" + @"			
			<footer>
			<div><span>&copy; " + DateTime.Now.Year + " " + context.GetRequestUri().Host + @"</span></div>" +
			(context.User.Identity.IsAuthenticated ? "<span><label>" + context.User.Identity.Name + "</label>(<a href='./_changepassword'>change password</a> - <a href='./_signout'>sign out</a>)</span>" : "") + @"
			</footer>
			</body>
			</html>").Replace("\t\t\t", "");
		}
		#endregion

		#region API Gateway Router
		internal static void Connect(int waitingTimes = 6789)
		{
			Global.Logger.LogDebug($"Attempting to connect to API Gateway Router [{new Uri(Router.GetRouterStrInfo()).GetResolvedURI()}]");
			Global.Connect(
				(sender, arguments) =>
				{
					Global.PrimaryInterCommunicateMessageUpdater?.Dispose();
					Global.PrimaryInterCommunicateMessageUpdater = Router.IncomingChannel.RealmProxy.Services
						.GetSubject<CommunicateMessage>($"messages.services.{Global.ServiceName.ToLower()}")
						.Subscribe(
							async message =>
							{
								try
								{
									await Handler.ProcessInterCommunicateMessageAsync(message).ConfigureAwait(false);
								}
								catch (Exception ex)
								{
									await Global.WriteLogsAsync(Global.Logger, $"Http.{Global.ServiceName}", $"Error occurred while processing an inter-communicate message: {ex.Message} => {message?.ToJson().ToString(Global.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}", ex, Global.ServiceName).ConfigureAwait(false);
								}
							},
							async exception => await Global.WriteLogsAsync(Global.Logger, $"Http.{Global.ServiceName}", $"Error occurred while fetching an inter-communicate message: {exception.Message}", exception).ConfigureAwait(false)
						);
					Global.SecondaryInterCommunicateMessageUpdater?.Dispose();
					Global.SecondaryInterCommunicateMessageUpdater = Router.IncomingChannel.RealmProxy.Services
						.GetSubject<CommunicateMessage>("messages.services.apigateway")
						.Subscribe(
							async message =>
							{
								try
								{
									await Handler.ProcessAPIGatewayCommunicateMessageAsync(message).ConfigureAwait(false);
								}
								catch (Exception ex)
								{
									await Global.WriteLogsAsync(Global.Logger, $"Http.{Global.ServiceName}", $"Error occurred while processing an inter-communicate message of API Gateway: {ex.Message} => {message?.ToJson().ToString(Global.IsDebugLogEnabled ? Newtonsoft.Json.Formatting.Indented : Newtonsoft.Json.Formatting.None)}", ex, Global.ServiceName).ConfigureAwait(false);
								}
							},
							async exception => await Global.WriteLogsAsync(Global.Logger, $"Http.{Global.ServiceName}", $"Error occurred while fetching an inter-communicate message of API Gateway: {exception.Message}", exception).ConfigureAwait(false)
						);
				},
				(sender, arguments) => Global.RegisterService($"Http.{Global.ServiceName}"),
				waitingTimes,
				exception => Global.Logger.LogError($"Cannot connect to API Gateway Router in period of times => {exception.Message}", exception),
				exception => Global.Logger.LogError($"Error occurred while connecting to API Gateway Router => {exception.Message}", exception)
			);
		}

		internal static void Disconnect(int waitingTimes = 1234)
		{
			Global.UnregisterService($"Http.{Global.ServiceName}", waitingTimes);
			Global.PrimaryInterCommunicateMessageUpdater?.Dispose();
			Global.SecondaryInterCommunicateMessageUpdater?.Dispose();
			Global.Disconnect(waitingTimes);
		}

		static Task ProcessInterCommunicateMessageAsync(CommunicateMessage message)
			=> Task.CompletedTask;

		static Task ProcessAPIGatewayCommunicateMessageAsync(CommunicateMessage message)
			=> message.Type.IsEquals("Service#RequestInfo")
				? Global.SendServiceInfoAsync($"Http.{Global.ServiceName}")
				: Task.CompletedTask;
		#endregion

	}
}