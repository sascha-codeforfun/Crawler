using Xunit;
using Crawler.Security;

namespace Crawler.Tests.Security
{
	/// <summary>
	/// Tests for CrawlPolicy — the exact-host allowlist. Pure, no Logger, no shared
	/// state. The load-bearing invariant under test: one declared host grants that
	/// host only — no sibling subdomain, no parent apex, no suffix look-alike.
	/// </summary>
	public class CrawlPolicyTests
	{
		[Fact]
		public void FromConfig_PrimaryHost_Allowed()
		{
			var p = CrawlPolicy.FromConfig("https://domain.tld", null);
			Assert.True(p.IsHostAllowed(new Uri("https://domain.tld/anything")));
		}

		[Fact]
		public void FromConfig_PrimaryWithSeedPath_StillHostScoped()
		{
			// A seed path is a start point, not a fence — scope is the host.
			var p = CrawlPolicy.FromConfig("https://domain.tld/section/index.html", null);
			Assert.True(p.IsHostAllowed(new Uri("https://domain.tld/section-two/x")));
		}

		[Fact]
		public void FromConfig_MalformedPrimary_Throws()
		{
			Assert.Throws<ArgumentException>(() => CrawlPolicy.FromConfig("not-a-url", null));
			Assert.Throws<ArgumentException>(() => CrawlPolicy.FromConfig("", null));
			Assert.Throws<ArgumentException>(() => CrawlPolicy.FromConfig("ftp://domain.tld", null));
		}

		[Fact]
		public void FromConfig_MalformedSubdomain_IgnoredAndRecorded()
		{
			var p = CrawlPolicy.FromConfig("https://domain.tld",
				new[] { "https://help.domain.tld", "not-a-url", "" });
			Assert.True(p.IsHostAllowed(new Uri("https://help.domain.tld/x")));
			Assert.Contains("not-a-url", p.IgnoredEntries);
			Assert.False(p.IsHostAllowed(new Uri("https://other.domain.tld/x")));
		}

		[Fact]
		public void IsHostAllowed_ExactHostOnly()
		{
			var p = CrawlPolicy.FromConfig("https://sub.domain.tld", null);
			Assert.True(p.IsHostAllowed(new Uri("https://sub.domain.tld/x")));
			Assert.False(p.IsHostAllowed(new Uri("https://sub2.domain.tld/x")));   // sibling
			Assert.False(p.IsHostAllowed(new Uri("https://domain.tld/x")));        // apex
			Assert.False(p.IsHostAllowed(new Uri("https://sub.domain.tld.evil.com/x"))); // suffix
		}

		[Fact]
		public void IsHostAllowed_SchemeIsPartOfIdentity()
		{
			var p = CrawlPolicy.FromConfig("https://domain.tld", null);
			Assert.False(p.IsHostAllowed(new Uri("http://domain.tld/x")));
		}

		[Fact]
		public void IsHostAllowed_HostCaseInsensitive()
		{
			var p = CrawlPolicy.FromConfig("https://Domain.TLD", null);
			Assert.True(p.IsHostAllowed(new Uri("https://domain.tld/x")));
		}
	}
}
