namespace Crawler.Security
{
	using System;

	/// <summary>
	/// Outcome of an admission decision. On admit, <see cref="AbsoluteUrl"/> is the
	/// canonical resolved URL the caller must use downstream (callers never
	/// re-resolve the raw reference — single source of truth). On deny it is null
	/// and <see cref="Reason"/> carries a short machine-readable cause.
	/// </summary>
	public readonly record struct AdmissionVerdict(bool Admitted, string? AbsoluteUrl, string Reason)
	{
		internal static AdmissionVerdict Admit(string absoluteUrl) => new(true, absoluteUrl, "admitted");

		internal static AdmissionVerdict Deny(string reason) => new(false, null, reason);
	}

	/// <summary>
	/// The crawl admission gate — the fetch-side security boundary. Decides whether
	/// a link reference discovered on a given page may be fetched. The decision is
	/// always made on the RESOLVED absolute URL, never the raw string, so
	/// document-relative links resolve against the page and are followed, while
	/// off-host, off-scheme and IP-literal references are denied. Default-deny:
	/// anything that fails to resolve, or fails any check, is denied.
	/// </summary>
	public static class CrawlGate
	{
		// [KEEP] Security boundary — order matters and the decision is on the
		// resolved URL. Resolving first is what lets .NET's spec-correct resolver
		// normalise dot-segments, backslashes and control characters before any
		// judgement, so the raw-string tricks (scheme-relative //evil.com, userinfo
		// confusion, backslash hosts) are decided on their true resolved host. Every
		// queue-insertion site must route through this gate; a reference that does
		// not pass is never enqueued.
		public static AdmissionVerdict TryAdmit(string rawRef, Uri pageBase, CrawlPolicy policy)
		{
			if (string.IsNullOrWhiteSpace(rawRef))
			{
				return AdmissionVerdict.Deny("empty");
			}

			if (!Uri.TryCreate(pageBase, rawRef, out var abs))
			{
				return AdmissionVerdict.Deny("unresolvable");
			}

			if (!SchemeCheck.IsCrawlableScheme(abs))
			{
				return AdmissionVerdict.Deny($"scheme:{abs.Scheme}");
			}

			if (IpLiteralCheck.IsIpLiteral(abs))
			{
				return AdmissionVerdict.Deny($"ip-literal:{abs.Host}");
			}

			if (!HostAllowlistCheck.IsAllowed(abs, policy))
			{
				return AdmissionVerdict.Deny($"off-host:{abs.IdnHost}");
			}

			return AdmissionVerdict.Admit(abs.AbsoluteUri);
		}
	}
}
