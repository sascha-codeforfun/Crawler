using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Crawler
{
	/// <summary>
	/// Checks connectivity to the configured target URL before the crawl starts.
	/// Tests DNS resolution, HTTP HEAD, and proxy reachability.
	/// Results are appended to connectivity.log in the site root folder.
	/// </summary>
	public static class ConnectivityCheck
	{
		public record ConnectivityResult(
			bool IsOk,
			string MachineName,
			string[] MachineIPs,
			string Username,
			string TargetUrl,
			string TargetHostname,
			string[] ResolvedTargetIPs,
			string? ProxyUrl,
			string[] ResolvedProxyIPs,
			int? HeadStatusCode,
			long HeadResponseMs,
			string? FailureReason);

		/// <summary>
		/// Performs connectivity check. Appends result to connectivity.log.
		/// Returns false and exits app on failure in silent mode.
		/// Returns false in interactive mode when user aborts.
		/// </summary>
		public static async Task<bool> RunAsync(
			string targetUrl,
			string? proxyUrl,
			string connectivityLogPath,
			bool silent,
			string? proxyUser = null,
			string? proxyPassword = null)
		{
			var result = await CheckAsync(targetUrl, proxyUrl, proxyUser, proxyPassword);

			// Always append to connectivity.log in silent mode or on failure.
			if (!result.IsOk || silent)
			{
				AppendToLog(connectivityLogPath, result);
			}

			if (result.IsOk)
			{
				return true;
			}

			// Build failure message.
			var lines = BuildFailureLines(result);

			if (silent)
			{
				// Silent mode — log everything and exit.
				Logger.LogError("Connectivity check failed — see connectivity.log for details.");
				foreach (var line in lines)
				{
					Logger.LogError(line);
				}

				return false;
			}

			// Interactive mode — show details and ask user.
			ConsoleUi.WriteBlank();
			ConsoleUi.WriteErrorBlock("CONNECTIVITY CHECK FAILED", lines);

			if (string.IsNullOrEmpty(proxyUrl))
			{
				ConsoleUi.WriteBlank();
				ConsoleUi.WriteWarning("Tip: if you are behind a corporate firewall, configure a proxy");
				ConsoleUi.WriteWarning("in config.private.json — see Section 3 (NETWORK / PROXY).");
			}

			ConsoleUi.WriteBlank();
			var key = ConsoleUi.ReadKey("[C] Continue anyway   [A] Abort > ");

			if (key == ConsoleKey.A)
			{
				Logger.LogInfo("Aborted by user after connectivity check failure.");
				return false;
			}

			Logger.LogWarning("User chose to continue despite connectivity check failure.");
			return true;
		}

		// ── Core check ────────────────────────────────────────────────────────────

		private static async Task<ConnectivityResult> CheckAsync(
			string targetUrl, string? proxyUrl,
			string? proxyUser = null, string? proxyPassword = null)
		{
			// Gather machine identity.
			var machineName = Environment.MachineName;
			var username = GetUsername();
			var machineIPs = GetMachineIPs();

			// Parse target hostname.
			Uri? targetUri = null;
			string hostname = "";
			try
			{
				targetUri = new Uri(targetUrl);
				hostname = targetUri.Host;
			}
			catch
			{
				return Fail(machineName, machineIPs, username, targetUrl, hostname,
					[], proxyUrl, [], null, 0, $"Invalid target URL: {targetUrl}");
			}

			// DNS resolve target.
			string[] resolvedTargetIPs = [];
			try
			{
				var addresses = await Dns.GetHostAddressesAsync(hostname);
				resolvedTargetIPs = [.. addresses.Select(a => a.ToString())];
			}
			catch (Exception ex)
			{
				return Fail(machineName, machineIPs, username, targetUrl, hostname,
					[], proxyUrl, [], null, 0,
					$"DNS resolution failed for '{hostname}': {ex.Message}");
			}

			// DNS resolve proxy if configured.
			string[] resolvedProxyIPs = [];
			if (!string.IsNullOrEmpty(proxyUrl))
			{
				try
				{
					var proxyUri = new Uri(proxyUrl);
					var addresses = await Dns.GetHostAddressesAsync(proxyUri.Host);
					resolvedProxyIPs = [.. addresses.Select(a => a.ToString())];
				}
				catch (Exception ex)
				{
					return Fail(machineName, machineIPs, username, targetUrl, hostname,
						resolvedTargetIPs, proxyUrl, [], null, 0,
						$"DNS resolution failed for proxy '{proxyUrl}': {ex.Message}");
				}
			}

			// HTTP HEAD to target URL (through proxy if configured).
			int? statusCode = null;
			long responseMs = 0;
			try
			{
				var handler = new HttpClientHandler { AllowAutoRedirect = true };
				// Build the proxy with the SAME credential posture as the
				// real crawl (CrawlAsset / Crawl). A credential-less proxy here
				// would let an authenticating corporate proxy return 407 and abort
				// the run — even though the crawl itself authenticates via
				// UseDefaultCredentials. logContext is null: the preflight has its
				// own logging and the crawl path logs the proxy config moments
				// later, so we avoid a duplicate line.
				var proxy = ProxyConfig.Build(proxyUrl, proxyUser, proxyPassword, logContext: null);
				if (proxy != null)
				{
					handler.Proxy = proxy;
					handler.UseProxy = true;
				}

				using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
				var sw = System.Diagnostics.Stopwatch.StartNew();
				var response = await client.SendAsync(
					new HttpRequestMessage(HttpMethod.Head, targetUrl));
				sw.Stop();
				responseMs = sw.ElapsedMilliseconds;
				statusCode = (int)response.StatusCode;

				if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Redirect
					&& response.StatusCode != HttpStatusCode.MovedPermanently)
				{
					return Fail(machineName, machineIPs, username, targetUrl, hostname,
						resolvedTargetIPs, proxyUrl, resolvedProxyIPs, statusCode, responseMs,
						$"HEAD request returned HTTP {statusCode}");
				}
			}
			catch (Exception ex)
			{
				return Fail(machineName, machineIPs, username, targetUrl, hostname,
					resolvedTargetIPs, proxyUrl, resolvedProxyIPs, statusCode, responseMs,
					$"HEAD request failed: {ex.Message}");
			}

			return new ConnectivityResult(
				IsOk: true,
				MachineName: machineName,
				MachineIPs: machineIPs,
				Username: username,
				TargetUrl: targetUrl,
				TargetHostname: hostname,
				ResolvedTargetIPs: resolvedTargetIPs,
				ProxyUrl: proxyUrl,
				ResolvedProxyIPs: resolvedProxyIPs,
				HeadStatusCode: statusCode,
				HeadResponseMs: responseMs,
				FailureReason: null);
		}

		// ── Logging ───────────────────────────────────────────────────────────────

		private static void AppendToLog(string logPath, ConnectivityResult r)
		{
			var sb = new StringBuilder();
			sb.AppendLine($"──────────────────────────────────────────────────────────────");
			sb.AppendLine($"Timestamp   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			sb.AppendLine($"Result      : {(r.IsOk ? "OK" : "FAILED")}");
			sb.AppendLine($"Machine     : {r.MachineName}");
			sb.AppendLine($"Machine IPs : {string.Join(", ", r.MachineIPs)}");
			sb.AppendLine($"Username    : {r.Username}");
			sb.AppendLine($"Target URL  : {r.TargetUrl}");
			sb.AppendLine($"Target host : {r.TargetHostname}");
			sb.AppendLine($"Resolved IPs: {string.Join(", ", r.ResolvedTargetIPs)}");
			if (!string.IsNullOrEmpty(r.ProxyUrl))
			{
				sb.AppendLine($"Proxy URL   : {r.ProxyUrl}");
				sb.AppendLine($"Proxy IPs   : {string.Join(", ", r.ResolvedProxyIPs)}");
			}
			if (r.HeadStatusCode.HasValue)
			{
				sb.AppendLine($"HEAD status : {r.HeadStatusCode} ({r.HeadResponseMs} ms)");
			}

			if (!string.IsNullOrEmpty(r.FailureReason))
			{
				sb.AppendLine($"Failure     : {r.FailureReason}");
			}

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
				File.AppendAllText(logPath, sb.ToString(), Encoding.UTF8);
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not write connectivity.log: {ex.Message}");
			}
		}

		// [internal for test access — pure formatting helper, see ConnectivityCheckTests]
		internal static List<string> BuildFailureLines(ConnectivityResult r)
		{
			List<string> lines =
			[
				$"Machine     : {r.MachineName}",
				$"Machine IPs : {string.Join(", ", r.MachineIPs)}",
				$"Username    : {r.Username}",
				$"Target URL  : {r.TargetUrl}",
				$"Resolved IPs: {(r.ResolvedTargetIPs.Length > 0 ? string.Join(", ", r.ResolvedTargetIPs) : "— DNS failed")}",
			];
			if (!string.IsNullOrEmpty(r.ProxyUrl))
			{
				lines.Add($"Proxy URL   : {r.ProxyUrl}");
				lines.Add($"Proxy IPs   : {(r.ResolvedProxyIPs.Length > 0 ? string.Join(", ", r.ResolvedProxyIPs) : "— DNS failed")}");
			}
			if (r.HeadStatusCode.HasValue)
			{
				lines.Add($"HEAD status : {r.HeadStatusCode} ({r.HeadResponseMs} ms)");
			}

			lines.Add($"Reason      : {r.FailureReason}");
			return lines;
		}

		// ── Helpers ───────────────────────────────────────────────────────────────

		private static string GetUsername()
		{
			try
			{
				if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				{
					// WindowsIdentity gives the impersonated account when run via Task Scheduler.
					return System.Security.Principal.WindowsIdentity.GetCurrent().Name;
				}
				return Environment.UserName;
			}
			catch
			{
				return Environment.UserName;
			}
		}

		private static string[] GetMachineIPs()
		{
			try
			{
				return [..Dns.GetHostAddresses(Dns.GetHostName())
					.Where(a => a.AddressFamily == AddressFamily.InterNetwork
						|| a.AddressFamily == AddressFamily.InterNetworkV6)
					.Select(a => a.ToString())];
			}
			catch
			{
				return [];
			}
		}

		private static ConnectivityResult Fail(
			string machineName, string[] machineIPs, string username,
			string targetUrl, string hostname,
			string[] resolvedTargetIPs, string? proxyUrl, string[] resolvedProxyIPs,
			int? statusCode, long responseMs, string reason) =>
			new(false, machineName, machineIPs, username, targetUrl, hostname,
				resolvedTargetIPs, proxyUrl, resolvedProxyIPs, statusCode, responseMs, reason);
	}
}
