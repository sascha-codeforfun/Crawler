namespace Crawler.SpellCheck
{
	using System;
	using System.Text.RegularExpressions;

	/// <summary>
	/// Derives the STABLE identity of a script asset from its URL, by stripping the
	/// build-time cache-buster fingerprint that changes every deploy. The fingerprint exists to
	/// make caching safe (a new URL when content changes); for locating a defect across crawls it
	/// is pure churn — the durable identity is the PATH minus the fingerprint. Keying on this is
	/// what lets a finding survive a re-deploy without the ticket churning, and what keeps two
	/// different bundles that happen to share a fingerprint distinct.
	///
	/// The cache-buster pattern handled is likely a hashed-bundle shape: a 32-hex segment directly
	/// before ".js". Anything that does NOT match is returned unchanged
	/// — a hashless name ("abc.min.js", "some_script.js") is
	/// already stable, and an unrecognised cache-bust scheme is left intact rather than mangled:
	/// the key may then churn, but it never WRONG-joins two distinct assets. [REVIEW] if other
	/// fingerprint shapes appear, promote the pattern to config rather than widening it blindly.
	/// </summary>
	public static class ScriptUrlKey
	{
		// ".<32 hex>.js" at the very end — the content-hash fingerprint. Case-insensitive for
		// safety though observed hashes are most of the time lowercase.
		private static readonly Regex CacheBuster =
			new(@"\.[0-9a-fA-F]{32}\.js$", RegexOptions.Compiled);

		/// <summary>
		/// Stable key for a script URL: its absolute path with the trailing cache-buster removed.
		/// Host is intentionally dropped so the same asset served from page-host vs a CDN host
		/// still resolves to one identity (single-site assumption; [REVIEW] make host-aware if a
		/// deployment ever serves genuinely distinct same-path assets from different hosts). Falls
		/// back to the raw string (cache-buster still stripped) if the URL will not parse.
		/// </summary>
		public static string StableKey(string url)
		{
			if (string.IsNullOrWhiteSpace(url))
			{
				return string.Empty;
			}

			string basis = url;
			if (Uri.TryCreate(url, UriKind.Absolute, out var u))
			{
				basis = u.AbsolutePath;
			}

			return CacheBuster.Replace(basis, ".js");
		}
	}
}
