namespace Crawler
{
	using System.Net;

	/// <summary>
	/// Single source of truth for turning the configured proxy settings into an
	/// <see cref="IWebProxy"/> with the correct credential posture. Previously the
	/// proxy + credential logic was hand-written in three places — ConnectivityCheck,
	/// CrawlAsset, and Crawl — and they drifted: the connectivity preflight built a
	/// proxy with NO credentials, while the actual crawl used UseDefaultCredentials.
	/// On an authenticating corporate proxy that returns 407 to anonymous requests,
	/// the preflight then failed (and aborted the run in silent mode) even though
	/// the crawl itself would have authenticated fine.
	///
	/// Credential rules (identical for every HTTP path now):
	///   * proxyUrl blank            → no proxy (caller sets UseProxy = false).
	///   * explicit proxyUser given  → NetworkCredential(user, password).
	///   * no explicit user          → UseDefaultCredentials = true (current OS
	///                                  identity: service account in scheduled
	///                                  runs, logged-in user interactively).
	///
	/// BypassOnLocal is false everywhere: local addresses are still routed through
	/// the proxy, matching the pre-consolidation intent of all three call sites.
	/// </summary>
	public static class ProxyConfig
	{
		/// <summary>
		/// Builds an <see cref="IWebProxy"/> for the given settings, or null when
		/// no proxy is configured (proxyUrl null/whitespace). Callers assign the
		/// result to their handler's Proxy property and set UseProxy accordingly:
		/// a null return means "no proxy — set UseProxy = false".
		/// </summary>
		/// <param name="logContext">
		/// Short label (e.g. "Crawler", "CrawlAsset", "Connectivity") used only to
		/// prefix the INFO log line so operators can see which path configured the
		/// proxy. When null, no log line is emitted (keeps quiet paths quiet).
		/// </param>
		public static IWebProxy? Build(
			string? proxyUrl, string? proxyUser, string? proxyPassword,
			string? logContext = null)
		{
			if (string.IsNullOrWhiteSpace(proxyUrl))
			{
				if (logContext != null)
				{
					Logger.LogInfo($"{logContext}: no proxy configured.");
				}

				return null;
			}

			// BypassOnLocal = false → force the proxy even for local addresses.
			var proxy = new WebProxy(proxyUrl, BypassOnLocal: false);

			if (!string.IsNullOrWhiteSpace(proxyUser))
			{
				proxy.Credentials = new NetworkCredential(proxyUser, proxyPassword ?? string.Empty);
				if (logContext != null)
				{
					Logger.LogInfo($"{logContext}: proxy configured as {proxyUrl} (authenticated as {proxyUser})");
				}
			}
			else
			{
				// No explicit credentials — use the current OS identity. This is the
				// branch the connectivity preflight was previously MISSING.
				proxy.UseDefaultCredentials = true;
				if (logContext != null)
				{
					Logger.LogInfo($"{logContext}: proxy configured as {proxyUrl} (using default credentials)");
				}
			}

			return proxy;
		}
	}
}
