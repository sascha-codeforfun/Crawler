using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ResourceBloatBaselineAnalyzer.ShortenUrl helper.
	/// </summary>
	public class ResourceBloatBaselineAnalyzerTests
	{
		// ── ShortenUrl ────────────────────────────────────────────────────────

		[Fact]
		public void ShortenUrl_ClientlibWithContentHash_StripsHash()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl(
				"https://www.example.com/etc/clientlibs/site/sitebundle.min.7b2032a3df36ccce07e8bd78afdaeb48.css");
			Assert.Equal("sitebundle.min.css", result);
		}

		[Fact]
		public void ShortenUrl_JsFileWithHash_StripsHash()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl(
				"https://www.example.com/etc/clientlibs/site/sitebundle.min.lc-efdeff026fbddf76-lc.js");
			// No 32-char hex hash — returns filename as-is
			Assert.Equal("sitebundle.min.lc-efdeff026fbddf76-lc.js", result);
		}

		[Fact]
		public void ShortenUrl_Exactly32HexHash_Stripped()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl(
				"https://www.example.com/libs/script.min.abcdef1234567890abcdef1234567890.js");
			Assert.Equal("script.min.js", result);
		}

		[Fact]
		public void ShortenUrl_NoHash_ReturnsFilename()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl(
				"https://www.example.com/libs/vendoragentjs_ABCDEFGHIJKL_10331260218130851.js");
			Assert.Equal("vendoragentjs_ABCDEFGHIJKL_10331260218130851.js", result);
		}

		[Fact]
		public void ShortenUrl_EmptyString_ReturnsEmpty()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl("");
			Assert.Equal("", result);
		}

		[Fact]
		public void ShortenUrl_UrlWithNoPath_ReturnsEmptySegment()
		{
			var result = ResourceBloatBaselineAnalyzer.ShortenUrl(
				"https://www.example.com/");
			// Last segment after split('/') is ""
			Assert.Equal("", result);
		}
	}
}
