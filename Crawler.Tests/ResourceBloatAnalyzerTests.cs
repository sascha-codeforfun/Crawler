using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ResourceBloatAnalyzer internal helpers:
	/// ShortenAssetName, NormaliseUrl, ResolveRef.
	/// </summary>
	public class ResourceBloatAnalyzerTests
	{
		// ── ShortenAssetName ──────────────────────────────────────────────────

		[Fact]
		public void ShortenAssetName_FullHashPrefixAndContentHash_StripsBoths()
		{
			var hash64 = new string('a', 64);
			var filename = hash64 + "egs.min.efdeff026fbddf7623760c6560514031__2.png";
			var result = ResourceBloatAnalyzer.ShortenAssetName(filename);
			Assert.Equal("egs.min__2.png", result);
		}

		[Fact]
		public void ShortenAssetName_NoLeadingHash_ReturnsOriginal()
		{
			var result = ResourceBloatAnalyzer.ShortenAssetName("simple.png");
			Assert.Equal("simple.png", result);
		}

		[Fact]
		public void ShortenAssetName_LeadingHashOnly_NoContentHash_ReturnsNamePart()
		{
			var hash64 = new string('b', 64);
			var filename = hash64 + "widget.min.js";
			var result = ResourceBloatAnalyzer.ShortenAssetName(filename);
			Assert.Equal("widget.min.js", result);
		}

		[Fact]
		public void ShortenAssetName_DifferentExtensions_WorksForJpg()
		{
			var hash64 = new string('c', 64);
			var filename = hash64 + "image.efdeff026fbddf7623760c6560514031__17.jpg";
			var result = ResourceBloatAnalyzer.ShortenAssetName(filename);
			Assert.Equal("image__17.jpg", result);
		}

		[Fact]
		public void ShortenAssetName_EmptyString_ReturnsEmpty()
		{
			var result = ResourceBloatAnalyzer.ShortenAssetName("");
			Assert.Equal("", result);
		}

		// ── NormaliseUrl ──────────────────────────────────────────────────────

		[Fact]
		public void NormaliseUrl_QueryString_Stripped()
		{
			var result = ResourceBloatAnalyzer.NormaliseUrl(
				"https://example.com/script.js?v=123");
			Assert.Equal("https://example.com/script.js", result);
		}

		[Fact]
		public void NormaliseUrl_Fragment_Stripped()
		{
			var result = ResourceBloatAnalyzer.NormaliseUrl(
				"https://example.com/page.html#section");
			Assert.Equal("https://example.com/page.html", result);
		}

		[Fact]
		public void NormaliseUrl_TrailingSlash_Stripped()
		{
			var result = ResourceBloatAnalyzer.NormaliseUrl(
				"https://example.com/path/");
			Assert.Equal("https://example.com/path", result);
		}

		[Fact]
		public void NormaliseUrl_CleanUrl_Unchanged()
		{
			var url = "https://example.com/etc/clientlibs/site.min.js";
			var result = ResourceBloatAnalyzer.NormaliseUrl(url);
			Assert.Equal(url, result);
		}

		[Fact]
		public void NormaliseUrl_QueryAndFragment_BothStripped()
		{
			var result = ResourceBloatAnalyzer.NormaliseUrl(
				"https://example.com/script.js?v=1#top");
			Assert.Equal("https://example.com/script.js", result);
		}

		// ── ResolveRef ────────────────────────────────────────────────────────

		[Fact]
		public void ResolveRef_AbsoluteUrl_ReturnedUnchanged()
		{
			var result = ResourceBloatAnalyzer.ResolveRef(
				"https://cdn.example.com/lib.js",
				"https://www.example.com");
			Assert.Equal("https://cdn.example.com/lib.js", result);
		}

		[Fact]
		public void ResolveRef_RootRelative_PrependsSiteUrl()
		{
			var result = ResourceBloatAnalyzer.ResolveRef(
				"/etc/clientlibs/site.min.js",
				"https://www.example.com");
			Assert.Equal("https://www.example.com/etc/clientlibs/site.min.js", result);
		}

		[Fact]
		public void ResolveRef_RelativePath_PrependsSiteUrlWithSlash()
		{
			var result = ResourceBloatAnalyzer.ResolveRef(
				"scripts/lib.js",
				"https://www.example.com");
			Assert.Equal("https://www.example.com/scripts/lib.js", result);
		}

		[Fact]
		public void ResolveRef_SiteUrlTrailingSlash_NoDuplicateSlash()
		{
			var result = ResourceBloatAnalyzer.ResolveRef(
				"/etc/clientlibs/site.min.js",
				"https://www.example.com/");
			Assert.Equal("https://www.example.com/etc/clientlibs/site.min.js", result);
		}

		[Fact]
		public void ResolveRef_HttpAbsolute_ReturnedUnchanged()
		{
			var result = ResourceBloatAnalyzer.ResolveRef(
				"http://legacy.example.com/old.js",
				"https://www.example.com");
			Assert.Equal("http://legacy.example.com/old.js", result);
		}
	}
}
