namespace Crawler.Urls
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	/// <summary>
	/// The crawl-scope security gate: decides whether a discovered link is in scope
	/// to follow and download. A pure predicate over the link plus the site domain,
	/// allowed subdomains, and operator download-exclusions. Extracted from Tools.
	/// </summary>
	public static class Validity
	{
		// [KEEP] Security-relevant: this is the plain-language contract for the gate below.
		// A link is in scope when it starts with the website URL, a configured allowed
		// subdomain, or '/' (root-relative), AND is not matched by any enabled download
		// exclusion. Absolute URLs to other schemes or hosts (tel:, mailto:, foreign
		// domains) are rejected because they match none of those prefixes.
		// Keep this in sync with the body if the gate changes.
		/// <summary>
		/// Returns true when <paramref name="link"/> is safe to follow and download.
		/// A link is valid when it either:
		///   - starts with <paramref name="websiteUrl"/> (primary domain), or
		///   - starts with one of the explicitly allowed subdomain base URLs, or
		///   - is a relative path (starts with '/') — resolved against the current page.
		///
		/// [KEEP] Security boundary: absolute URLs that do not match the primary domain
		/// or an explicitly allowed subdomain are rejected. This prevents the crawler
		/// from following links to arbitrary external domains discovered in page content.
		/// Never relax this check without explicit subdomain configuration — blind
		/// subdomain following is a security risk.
		/// </summary>
		public static bool IsInDownloadScope(
			string link,
			string websiteUrl,
			IReadOnlyList<CrawlLinkExclusion> downloadExclusions,
			IReadOnlyList<string>? allowedSubdomains = null)
		{
			// [KEEP] Reject absolute URLs that don't match the primary domain or an
			// explicitly configured subdomain. Relative paths ('/') are always allowed
			// since they are resolved against the current page's host.
			//
			// THIS IS THE SECURITY BOUNDARY. Unknown schemes (tel:, mailto:, smarty:,
			// any future scheme), foreign domains, and any other non-matching prefix
			// fail-close here. Downstream filters (downloadExclusions below) are
			// operational, not security: an empty or misconfigured downloadExclusions
			// cannot relax this gate.
			if (!link.StartsWith(websiteUrl) && !link.StartsWith('/'))
			{
				var matchesSubdomain = allowedSubdomains is { Count: > 0 }
					&& allowedSubdomains.Any(s =>
						!string.IsNullOrWhiteSpace(s)
						&& link.StartsWith(s, StringComparison.OrdinalIgnoreCase));
				if (!matchesSubdomain)
				{
					return false;
				}
			}

			// Operational exclusions — operator-curated substring filter for URLs
			// they don't want crawled (large/uninteresting sections, CMS stubs,
			// forum paths, etc.). Each enabled entry's Value is matched anywhere
			// in the link string (case-insensitive Contains). Disabled entries
			// are skipped. Empty Value on an enabled entry would reject every
			// link (Contains("") is always true) — config validator catches that
			// at startup, but the function still guards defensively at runtime.
			if (downloadExclusions != null)
			{
				foreach (var entry in downloadExclusions)
				{
					if (entry == null || !entry.Enabled || string.IsNullOrEmpty(entry.Value))
					{
						continue;
					}

					if (link.Contains(entry.Value, StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}
				}
			}

			return true;
		}
	}
}
