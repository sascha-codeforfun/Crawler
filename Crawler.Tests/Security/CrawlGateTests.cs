using Xunit;
using Crawler.Security;

namespace Crawler.Tests.Security
{
	/// <summary>
	/// Tests for CrawlGate — the fetch-side admission boundary. Pure, no Logger, no
	/// shared state. Inputs are treated as attacker-chosen; the assertions are our
	/// invariants (resolve-then-judge, http(s)-only, exact host, no IP literals),
	/// never "does it trust the reference".
	/// </summary>
	public class CrawlGateTests
	{
		private static readonly Uri PageBase = new("https://subdomain.domain.tld/section/index.html");

		private static CrawlPolicy Policy(params string[] hosts) =>
			CrawlPolicy.FromConfig(hosts[0], hosts.Length > 1 ? hosts[1..] : null);

		// ── Relative links: the motivating case ──────────────────────────────

		[Fact]
		public void Admit_DocumentRelativeSameHost()
		{
			var v = CrawlGate.TryAdmit("crawler-corpus/", PageBase, Policy("https://subdomain.domain.tld"));
			Assert.True(v.Admitted);
			Assert.Equal("https://subdomain.domain.tld/section/crawler-corpus/", v.AbsoluteUrl);
		}

		[Fact]
		public void Admit_SiblingSectionViaRelative()
		{
			// Discovered on /section/, links to /section-two/ — same host, in scope.
			var v = CrawlGate.TryAdmit("../section-two/index.html", PageBase, Policy("https://subdomain.domain.tld"));
			Assert.True(v.Admitted);
			Assert.Equal("https://subdomain.domain.tld/section-two/index.html", v.AbsoluteUrl);
		}

		[Fact]
		public void Admit_RootRelativeAndAbsoluteSameHost()
		{
			var p = Policy("https://subdomain.domain.tld");
			Assert.True(CrawlGate.TryAdmit("/other/page.html", PageBase, p).Admitted);
			Assert.True(CrawlGate.TryAdmit("https://subdomain.domain.tld/x", PageBase, p).Admitted);
		}

		// ── Off-host smuggling: the security core ────────────────────────────

		[Fact]
		public void Deny_SchemeRelativeOffHost()
		{
			var v = CrawlGate.TryAdmit("//evil.com/x", PageBase, Policy("https://subdomain.domain.tld"));
			Assert.False(v.Admitted);
			Assert.StartsWith("off-host", v.Reason);
		}

		[Fact]
		public void Deny_AbsoluteOffHost()
		{
			Assert.False(CrawlGate.TryAdmit("https://evil.com/x", PageBase, Policy("https://subdomain.domain.tld")).Admitted);
		}

		[Fact]
		public void Deny_UserinfoConfusion()
		{
			// Authority host is evil.com; the userinfo "subdomain.domain.tld@" is a decoy.
			var v = CrawlGate.TryAdmit("https://subdomain.domain.tld@evil.com/x", PageBase, Policy("https://subdomain.domain.tld"));
			Assert.False(v.Admitted);
		}

		// ── CRITICAL: one declared host = that host only ─────────────────────

		[Fact]
		public void Deny_SiblingSubdomain()
		{
			Assert.False(CrawlGate.TryAdmit("https://subdomain2.domain.tld/x", PageBase, Policy("https://subdomain.domain.tld")).Admitted);
		}

		[Fact]
		public void Deny_ParentApex_WhenOnlySubdomainListed()
		{
			Assert.False(CrawlGate.TryAdmit("https://domain.tld/x", PageBase, Policy("https://subdomain.domain.tld")).Admitted);
		}

		[Fact]
		public void Deny_SuffixLookAlike()
		{
			Assert.False(CrawlGate.TryAdmit("https://subdomain.domain.tld.evil.com/x", PageBase, Policy("https://subdomain.domain.tld")).Admitted);
		}

		[Fact]
		public void Apex_AdmitsApex_DeniesSubdomains()
		{
			var apexBase = new Uri("https://domain.tld/index.html");
			var p = Policy("https://domain.tld");
			Assert.True(CrawlGate.TryAdmit("https://domain.tld/x", apexBase, p).Admitted);
			Assert.False(CrawlGate.TryAdmit("https://www.domain.tld/x", apexBase, p).Admitted);
			Assert.False(CrawlGate.TryAdmit("https://sub.domain.tld/x", apexBase, p).Admitted);
		}

		[Fact]
		public void MultipleHosts_EachAdmittedExactly()
		{
			var p = Policy("https://domain.tld", "https://help.domain.tld");
			Assert.True(CrawlGate.TryAdmit("https://domain.tld/x", PageBase, p).Admitted);
			Assert.True(CrawlGate.TryAdmit("https://help.domain.tld/x", PageBase, p).Admitted);
			Assert.False(CrawlGate.TryAdmit("https://other.domain.tld/x", PageBase, p).Admitted);
		}

		// ── Dangerous schemes ────────────────────────────────────────────────

		[Theory]
		[InlineData("javascript:alert(1)")]
		[InlineData("data:text/html,<x>")]
		[InlineData("file:///etc/passwd")]
		[InlineData("mailto:a@b.com")]
		[InlineData("tel:+1234")]
		public void Deny_NonHttpSchemes(string reference)
		{
			var v = CrawlGate.TryAdmit(reference, PageBase, Policy("https://subdomain.domain.tld"));
			Assert.False(v.Admitted);
			Assert.StartsWith("scheme", v.Reason);
		}

		// ── IP literals ──────────────────────────────────────────────────────

		[Theory]
		[InlineData("http://127.0.0.1/x")]
		[InlineData("http://169.254.169.254/latest/meta-data/")]
		[InlineData("http://[::1]/x")]
		public void Deny_IpLiteralHosts(string reference)
		{
			Assert.False(CrawlGate.TryAdmit(reference, PageBase, Policy("https://subdomain.domain.tld")).Admitted);
		}

		[Fact]
		public void Deny_EmptyOrBlankRef()
		{
			var p = Policy("https://subdomain.domain.tld");
			Assert.False(CrawlGate.TryAdmit("", PageBase, p).Admitted);
			Assert.False(CrawlGate.TryAdmit("   ", PageBase, p).Admitted);
		}
	}
}
