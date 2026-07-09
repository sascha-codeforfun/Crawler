namespace Crawler.Security
{
	using System;

	/// <summary>
	/// Pure check: the resolved host must EXACTLY match a host in the policy
	/// allowlist (scheme + host equality). This is where "one declared host = that
	/// host only" is enforced — sibling subdomains, the parent apex, and suffix
	/// look-alikes are all denied unless listed in their own right. Thin wrapper
	/// over <see cref="CrawlPolicy.IsHostAllowed"/> so the boundary reads as one
	/// named check among the others in the gate.
	/// </summary>
	public static class HostAllowlistCheck
	{
		public static bool IsAllowed(Uri uri, CrawlPolicy policy) => policy.IsHostAllowed(uri);
	}
}
