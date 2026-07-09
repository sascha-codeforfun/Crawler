using Xunit;
using Crawler.Security;
using Crawler.Urls;

namespace Crawler.Tests.Security
{
	/// <summary>
	/// Regression for the CSS url() recursion sink: a hostile in-scope stylesheet
	/// must not be able to steer the asset fetch off-host or at an internal /
	/// metadata IP. DownloadAssetAsync now self-gates every URL through CrawlGate;
	/// these pin the decision the gate makes on the URLs Extractor.FromCss pulls
	/// out of a stylesheet — no network I/O. Hosts are invented; the metadata IP is
	/// the well-known public constant.
	/// </summary>
	public class AssetGateTests
	{
		private const string CssBase = "https://example.test/assets/style.css";

		private static readonly CrawlPolicy InScope =
			CrawlPolicy.FromConfig("https://example.test", null);

		[Fact]
		public void CssUrls_OffHostAndInternalIp_Denied_InScopeAdmitted()
		{
			const string css = """
				body { background: url(http://169.254.169.254/latest/meta-data/); }
				.a   { background: url(http://10.0.0.5/internal.png); }
				.b   { background: url(https://evil.example/track.gif); }
				.c   { background: url(https://example.test/fonts/icon.woff); }
				""";

			var extracted = Extractor.FromCss(css, CssBase);

			// Every url() is pulled out — the ungated sink would have fetched each.
			Assert.Equal(4, extracted.Count);

			foreach (var u in extracted)
			{
				var verdict = CrawlGate.TryAdmit(u.Url, new Uri(CssBase), InScope);
				if (u.Url.Contains("example.test"))
				{
					Assert.True(verdict.Admitted, $"in-scope url should admit: {u.Url}");
				}
				else
				{
					Assert.False(verdict.Admitted, $"off-scope url must be denied: {u.Url}");
				}
			}
		}

		[Fact]
		public void MetadataIp_DeniedAsIpLiteral()
		{
			var verdict = CrawlGate.TryAdmit(
				"http://169.254.169.254/latest/meta-data/", new Uri(CssBase), InScope);

			Assert.False(verdict.Admitted);
			Assert.StartsWith("ip-literal", verdict.Reason);
		}

		[Fact]
		public void PrivateRangeIp_DeniedAsIpLiteral()
		{
			var verdict = CrawlGate.TryAdmit(
				"http://10.0.0.5/internal.png", new Uri(CssBase), InScope);

			Assert.False(verdict.Admitted);
			Assert.StartsWith("ip-literal", verdict.Reason);
		}

		[Fact]
		public void OffHostName_DeniedAsOffHost()
		{
			var verdict = CrawlGate.TryAdmit(
				"https://evil.example/track.gif", new Uri(CssBase), InScope);

			Assert.False(verdict.Admitted);
			Assert.StartsWith("off-host", verdict.Reason);
		}

		[Fact]
		public void InScopeAsset_Admitted()
		{
			var verdict = CrawlGate.TryAdmit(
				"https://example.test/fonts/icon.woff", new Uri(CssBase), InScope);

			Assert.True(verdict.Admitted);
			Assert.Equal("https://example.test/fonts/icon.woff", verdict.AbsoluteUrl);
		}
	}
}
