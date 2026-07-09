namespace Crawler.Security
{
	using System;

	/// <summary>
	/// Pure check: a crawlable URL must use http or https. Rejects javascript:,
	/// data:, file:, ftp:, mailto:, tel:, and any other scheme — a "fetch
	/// everything we resolve" loop must never reach file:// (local read) or other
	/// non-web schemes. Single-purpose so the refusal is explicit and testable.
	/// </summary>
	public static class SchemeCheck
	{
		public static bool IsCrawlableScheme(Uri uri) =>
			uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
	}
}
