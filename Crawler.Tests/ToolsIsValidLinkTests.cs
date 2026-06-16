using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Locks the behavior of <see cref="Tools.IsValidLink"/>, the security
	/// boundary that decides whether the crawler follows a discovered link.
	///
	/// Two distinct concerns, both locked here:
	///
	///   1. SECURITY GATE — primary-domain or relative-path or allowed-
	///      subdomain match. Unknown schemes (tel:, mailto:, future schemes
	///      like smarty:) and foreign domains fail-close here. This is the
	///      load-bearing security boundary; downstream filters are operational,
	///      not security.
	///
	///   2. OPERATIONAL FILTER — case-insensitive Contains() check against
	///      operator-curated DownloadExclusions entries. Used for skipping
	///      large/uninteresting same-domain sections (forum URLs, CMS stubs,
	///      etc.). Empty or all-disabled DownloadExclusions = no filtering at
	///      this gate (security gate above still applies).
	///
	/// No HTTP, no filesystem — pure-function tests.
	/// </summary>
	public class ToolsIsValidLinkTests
	{
		private const string Site = "https://www.example.com";

		// Helper: build a DownloadExclusions list compactly in test setup.
		private static IReadOnlyList<CrawlLinkExclusion> Exclusions(params string[] values) =>
			values.Select(v => new CrawlLinkExclusion { Value = v }).ToList();

		// Empty exclusions list — natural state when an operator has no
		// operational filtering configured. Distinct from any "all-disabled"
		// or whitespace-entry state; just nothing in the list.
		private static readonly IReadOnlyList<CrawlLinkExclusion> NoExclusions = [];

		// ── Gate 1: SECURITY — domain / subdomain check ──────────────────────

		[Fact]
		public void PrimaryDomainPrefix_Accepted()
		{
			Assert.True(Tools.IsValidLink(
				$"{Site}/page.html",
				Site,
				NoExclusions));
		}

		[Fact]
		public void RelativeSlashPath_AlwaysAccepted()
		{
			// Relative paths starting with '/' resolve against the current page's
			// host and are always allowed regardless of websiteUrl or subdomain
			// configuration.
			Assert.True(Tools.IsValidLink(
				"/about/contact.html",
				Site,
				NoExclusions));
		}

		[Fact]
		public void AbsoluteForeignDomain_Rejected()
		{
			// The hardest line: a link to an arbitrary external host MUST be
			// rejected. Locking this prevents accidental "let everything through"
			// regressions.
			Assert.False(Tools.IsValidLink(
				"https://attacker.example.org/exploit.html",
				Site,
				NoExclusions));
		}

		[Fact]
		public void AbsoluteForeignDomain_NoAllowedSubdomainsConfigured_Rejected()
		{
			// Same as above but with an explicit empty allowedSubdomains argument
			// (rather than the default null). Behavior must be identical.
			Assert.False(Tools.IsValidLink(
				"https://attacker.example.org/exploit.html",
				Site,
				NoExclusions,
				allowedSubdomains: []));
		}

		[Fact]
		public void AllowedSubdomain_ExactPrefixMatch_Accepted()
		{
			// A link to a configured allowed subdomain is accepted.
			Assert.True(Tools.IsValidLink(
				"https://help.example.com/article.html",
				Site,
				NoExclusions,
				allowedSubdomains: ["https://help.example.com"]));
		}

		[Fact]
		public void AllowedSubdomain_CaseInsensitiveMatch_Accepted()
		{
			// The match is case-insensitive. URL hosts are commonly lowercased
			// by user agents but URLs in HTML can be mixed-case — the security
			// boundary must not depend on case for legitimacy.
			Assert.True(Tools.IsValidLink(
				"HTTPS://HELP.EXAMPLE.COM/article.html",
				Site,
				NoExclusions,
				allowedSubdomains: ["https://help.example.com"]));
		}

		[Fact]
		public void AllowedSubdomain_NonMatchingSubdomain_Rejected()
		{
			// A subdomain NOT in the allowed list is rejected even when others
			// ARE allowed. Locks the "exact prefix" semantics.
			Assert.False(Tools.IsValidLink(
				"https://other.example.com/page.html",
				Site,
				NoExclusions,
				allowedSubdomains: ["https://help.example.com"]));
		}

		[Fact]
		public void AllowedSubdomain_WhitespaceEntryIgnored()
		{
			// Whitespace-only entries in the allowed list are skipped (a
			// defensive check inside IsValidLink). Verify by configuring only
			// whitespace entries — the foreign URL must still be rejected.
			Assert.False(Tools.IsValidLink(
				"https://help.example.com/article.html",
				Site,
				NoExclusions,
				allowedSubdomains: ["   ", "\t"]));
		}

		// ── Unknown schemes fail-close at the security gate ──────────────────

		[Fact]
		public void TelScheme_Rejected_AtSecurityGate()
		{
			// tel: URLs don't start with websiteUrl and don't start with '/',
			// so they fail the domain-check at gate 1 and never reach the
			// operational filter. Locked regardless of whether tel: is in
			// DownloadExclusions or not — this is the security guarantee.
			Assert.False(Tools.IsValidLink(
				"tel:+15551234567",
				Site,
				NoExclusions));
		}

		[Fact]
		public void MailtoScheme_Rejected_AtSecurityGate()
		{
			// Same as tel: — rejected by gate 1, not by any operational filter.
			Assert.False(Tools.IsValidLink(
				"mailto:contact@example.com",
				Site,
				NoExclusions));
		}

		[Fact]
		public void HypotheticalFutureScheme_Rejected_AtSecurityGate()
		{
			// Any new scheme appearing in CMS content (smarty:, payby:, intent:,
			// whatever a future ecosystem invents) gets rejected by gate 1
			// without code change. This locks the "unknown = unsafe" defensive
			// posture against future scheme proliferation.
			Assert.False(Tools.IsValidLink(
				"smarty:open?id=42",
				Site,
				NoExclusions));
		}

		// ── Gate 2: OPERATIONAL — DownloadExclusions Contains check ──────────

		[Fact]
		public void DownloadExclusion_SubstringInPath_Rejected()
		{
			// Same-domain URL whose path contains an exclusion substring →
			// rejected by the operational filter. Substring matches anywhere
			// in the link, not just as prefix or suffix.
			Assert.False(Tools.IsValidLink(
				$"{Site}/section/break.html",
				Site,
				Exclusions("break.html")));
		}

		[Fact]
		public void DownloadExclusion_CaseInsensitive_Rejected()
		{
			// Contains match is case-insensitive. URL with mixed case still
			// rejected by lowercase-configured exclusion.
			Assert.False(Tools.IsValidLink(
				$"{Site}/section/Break.HTML",
				Site,
				Exclusions("break.html")));
		}

		[Fact]
		public void DownloadExclusion_ForumPath_Rejected()
		{
			// Realistic use case: operator wants to skip /forum/ because the
			// response sizes are huge or content isn't interesting for the
			// crawl's purpose. Same-domain URL, security gate would accept it,
			// operational filter rejects.
			Assert.False(Tools.IsValidLink(
				$"{Site}/forum/topic/12345",
				Site,
				Exclusions("/forum/")));
		}

		[Fact]
		public void DownloadExclusion_NonMatching_Accepted()
		{
			// Exclusion configured but the link does not match → link passes.
			Assert.True(Tools.IsValidLink(
				$"{Site}/public/page.html",
				Site,
				Exclusions("/forum/", "break.html")));
		}

		[Fact]
		public void DownloadExclusion_DisabledEntry_NotApplied()
		{
			// An entry with Enabled = false is ignored entirely — even if its
			// Value would otherwise match. Verifies the audit-trail semantics:
			// operators can keep an entry around with its Comment intact while
			// temporarily disabling its effect.
			var exclusions = new List<CrawlLinkExclusion>
			{
				new() { Value = "/forum/", Enabled = false, Comment = "temporarily allowed" },
			};
			Assert.True(Tools.IsValidLink(
				$"{Site}/forum/topic/12345",
				Site,
				exclusions));
		}

		[Fact]
		public void DownloadExclusion_EmptyValueOnEnabledEntry_DefensivelySkipped()
		{
			// Belt-and-suspenders: the validator halts at startup on this
			// shape, but the function itself also guards defensively
			// (`!string.IsNullOrEmpty(entry.Value) || continue`). Locks the
			// runtime guard separately from the validator — both must hold.
			// Without the guard, Contains("") would match every link.
			var exclusions = new List<CrawlLinkExclusion>
			{
				new() { Value = "", Enabled = true },
			};
			Assert.True(Tools.IsValidLink(
				$"{Site}/page.html",
				Site,
				exclusions));
		}

		[Fact]
		public void DownloadExclusion_MultipleEntries_AnyMatchRejects()
		{
			// Multiple exclusion entries — any one match rejects.
			Assert.False(Tools.IsValidLink(
				$"{Site}/forum/topic/12345",
				Site,
				Exclusions("/admin/", "/forum/", "/private/")));
		}

		// ── Interaction: subdomain + operational filter ──────────────────────

		[Fact]
		public void AllowedSubdomain_ButMatchingExclusion_Rejected()
		{
			// Link is on an allowed subdomain (passes gate 1), but matches an
			// operational exclusion (rejected by gate 2). Locks the gate
			// ordering: security gate first, operational filter second, and
			// the operational filter's rejection applies.
			Assert.False(Tools.IsValidLink(
				"https://help.example.com/admin/private.html",
				Site,
				Exclusions("/admin/"),
				allowedSubdomains: ["https://help.example.com"]));
		}

		// ── Additional security-gate coverage (ported from older test file) ──

		[Fact]
		public void PrimaryDomainRootUrl_Accepted()
		{
			// The bare primary URL itself (no path beyond the host) must be
			// accepted — it's the canonical entry point. Distinct from the
			// "/relative-path" test because there's no leading slash.
			Assert.True(Tools.IsValidLink(Site, Site, NoExclusions));
		}

		[Fact]
		public void AllowedSubdomain_DeepPath_Accepted()
		{
			// A multi-segment path under an allowed subdomain still matches —
			// the prefix check looks at the URL start only, not the depth.
			Assert.True(Tools.IsValidLink(
				"https://help.example.com/de/home/faq.html",
				Site,
				NoExclusions,
				allowedSubdomains: ["https://help.example.com"]));
		}

		[Fact]
		public void MultipleAllowedSubdomains_SecondMatches_Accepted()
		{
			// Allow list has multiple entries; the URL matches the second
			// (not the first). The Any(...) match must traverse the whole
			// list, not short-circuit on first-only.
			Assert.True(Tools.IsValidLink(
				"https://shop.example.com/product.html",
				Site,
				NoExclusions,
				allowedSubdomains: ["https://help.example.com", "https://shop.example.com"]));
		}

		// ── Empty/whitespace allow-list defense-in-depth (regression guard from #445) ──
		//
		// The original bug: a config like "UrlSubdomainsAllowed": [ "" ] produced
		// an allow-list with a single empty string. Because every consumer tests
		// link.StartsWith(entry) and StartsWith("") is true for ANY string, the
		// empty entry silently turned the allow-list into "permit every URL" —
		// the crawler wandered to hosts outside the configured site.
		//
		// IsValidLink must treat empty/whitespace entries as non-matches so
		// containment holds regardless of config hygiene. Config resolution
		// also sanitises these out at load time — defense in depth.

		[Fact]
		public void AllowedSubdomain_EmptyStringEntry_ExternalDomain_Rejected()
		{
			// Empty-string entry must not poison the allow list — a foreign
			// host stays rejected.
			Assert.False(Tools.IsValidLink(
				"https://evil.com/page.html",
				Site,
				NoExclusions,
				allowedSubdomains: [""]));
		}

		[Fact]
		public void AllowedSubdomain_EmptyStringEntry_ForeignSubdomain_Rejected()
		{
			// Same rationale: a different-root-domain subdomain stays rejected
			// even with an empty entry in the allow list.
			Assert.False(Tools.IsValidLink(
				"https://help.other.org/page.html",
				Site,
				NoExclusions,
				allowedSubdomains: [""]));
		}

		[Fact]
		public void AllowedSubdomain_WhitespaceEntry_ExternalDomain_Rejected()
		{
			// Whitespace-only entry is treated the same as empty — must not
			// poison the allow list.
			Assert.False(Tools.IsValidLink(
				"https://evil.com/page.html",
				Site,
				NoExclusions,
				allowedSubdomains: ["   "]));
		}

		[Fact]
		public void AllowedSubdomain_EmptyEntryAlongsideValidEntry_StillRejectsExternal()
		{
			// Mixed list: empty entry plus genuine entry. The empty entry must
			// not cause the genuine entry's match logic to leak to other hosts.
			Assert.False(Tools.IsValidLink(
				"https://evil.com/page.html",
				Site,
				NoExclusions,
				allowedSubdomains: ["", "https://help.example.com"]));
		}

		[Fact]
		public void AllowedSubdomain_EmptyEntryAlongsideValidEntry_StillAcceptsAllowed()
		{
			// Mixed list reverse-direction: empty entry must not BREAK matching
			// of the genuine allowed subdomain.
			Assert.True(Tools.IsValidLink(
				"https://help.example.com/page.html",
				Site,
				NoExclusions,
				allowedSubdomains: ["", "https://help.example.com"]));
		}

		[Fact]
		public void AllowedSubdomain_EmptyEntry_PrimaryDomainStillAccepted()
		{
			// Primary-domain containment is unaffected by junk entries in the
			// subdomain allow list — primary domain matches at gate 1 before
			// the subdomain check runs.
			Assert.True(Tools.IsValidLink(
				$"{Site}/page.html",
				Site,
				NoExclusions,
				allowedSubdomains: [""]));
		}

		// ── Exclusions still applied even on allowed subdomains ──────────────
		// (Ported from older file; the operational filter applies regardless
		// of which gate-1 path accepted the link.)

		[Fact]
		public void TelScheme_OnAllowedSubdomain_StillRejected()
		{
			// tel: gets rejected by gate 1 (doesn't match websiteUrl, doesn't
			// match '/', doesn't match any subdomain even though one is
			// configured). The allowedSubdomains list doesn't open the door
			// for non-http/https schemes.
			Assert.False(Tools.IsValidLink(
				"tel:+491234567",
				Site,
				NoExclusions,
				allowedSubdomains: ["https://help.example.com"]));
		}
	}
}
