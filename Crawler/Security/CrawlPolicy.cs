namespace Crawler.Security
{
	using System;
	using System.Collections.Generic;

	/// <summary>
	/// The set of hosts a crawl is permitted to fetch, built once from config and
	/// then read-only. Membership is EXACT scheme+host equality — declaring one
	/// host grants exactly that host and nothing else: no sibling subdomains, no
	/// parent apex, no suffix look-alikes. The canonical key is
	/// "scheme://idnhost" (lowercased), so an apex (domain.tld) and a subdomain
	/// (sub.domain.tld) are the same code path, each matched whole.
	///
	/// Pure: no logging, no I/O. Malformed subdomain entries are surfaced via
	/// <see cref="IgnoredEntries"/> for the caller to report loudly at startup.
	/// </summary>
	public sealed class CrawlPolicy
	{
		// [KEEP] Security boundary — the host allowlist. EXACT match only. A naive
		// suffix/prefix test is the classic leak (it would admit sub2.domain.tld,
		// domain.tld.evil.com, and the apex when only one host was declared), so
		// membership is literal equality on the normalised "scheme://idnhost" key.
		// IdnHost is used so a punycode/homoglyph host cannot masquerade as an
		// allowed one. Default-deny: anything not exactly listed is out of scope.
		private readonly HashSet<string> allowedHostKeys;

		private CrawlPolicy(HashSet<string> keys, IReadOnlyList<string> ignored)
		{
			allowedHostKeys = keys;
			IgnoredEntries = ignored;
		}

		/// <summary>
		/// Subdomain-allowlist entries that were malformed (not an absolute http(s)
		/// URL with a host) and therefore dropped. The caller logs these loudly at
		/// startup; a dropped entry simply means that host is not crawled.
		/// </summary>
		public IReadOnlyList<string> IgnoredEntries { get; }

		/// <summary>The resolved allowlist keys ("scheme://idnhost"). For diagnostics/tests.</summary>
		internal IReadOnlyCollection<string> AllowedHostKeys => allowedHostKeys;

		/// <summary>True iff <paramref name="resolved"/>'s scheme+host exactly matches an allowed host.</summary>
		public bool IsHostAllowed(Uri resolved)
		{
			var key = HostKey(resolved);
			return key != null && allowedHostKeys.Contains(key);
		}

		/// <summary>
		/// Canonical "scheme://idnhost" key (lowercased) for a URL, or null if it is
		/// not http(s) with a non-empty host. Path, port, query and userinfo are
		/// intentionally excluded — scope is host-level.
		/// </summary>
		internal static string? HostKey(Uri uri)
		{
			if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
			{
				return null;
			}

			var host = uri.IdnHost;
			if (string.IsNullOrEmpty(host))
			{
				return null;
			}

			return (uri.Scheme + "://" + host).ToLowerInvariant();
		}

		/// <summary>
		/// Builds the allowlist from the primary site URL plus each declared
		/// subdomain base URL. The host is taken EXACTLY (path/port/userinfo
		/// ignored). A malformed PRIMARY url is fatal — it throws, because a crawl
		/// cannot proceed without a valid scope. A malformed SUBDOMAIN entry is
		/// fail-closed: dropped from the allowlist and recorded in
		/// <see cref="IgnoredEntries"/> for the caller to log.
		/// </summary>
		public static CrawlPolicy FromConfig(string primaryUrl, IReadOnlyList<string>? subdomainBaseUrls)
		{
			var keys = new HashSet<string>(StringComparer.Ordinal);

			var primaryKey = TryHostKey(primaryUrl);
			if (primaryKey == null)
			{
				throw new ArgumentException(
					$"CrawlPolicy: primary site Url '{primaryUrl}' is not a valid absolute http(s) URL with a host.",
					nameof(primaryUrl));
			}

			keys.Add(primaryKey);

			var ignored = new List<string>();
			foreach (var entry in subdomainBaseUrls ?? Array.Empty<string>())
			{
				var key = TryHostKey(entry);
				if (key == null)
				{
					ignored.Add(entry);
					continue;
				}

				keys.Add(key);
			}

			return new CrawlPolicy(keys, ignored);
		}

		private static string? TryHostKey(string? url)
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return null;
			}

			return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? HostKey(uri) : null;
		}
	}
}
