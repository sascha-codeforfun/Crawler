using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for UrlExtractor's resolution guards, complementing UrlExtractorTests
	/// (which covers the main extraction happy paths). These target the Resolve
	/// guard arms — reached cleanly through CSS url() since it passes raw values
	/// straight to Resolve — plus the per-extractor resolved==null skips, the
	/// invalid-base-URL guards (TryParseUri catch), and the JSON-path extractor.
	///
	/// SYNTHETIC fixtures. Resolve returns null for fragments, queries, mailto/tel/
	/// javascript schemes, and purely numeric paths; protocol-relative URLs assume
	/// https. url() values avoid nested parens to stay clear of CSS-regex edges.
	/// </summary>
	public class UrlExtractorResolveTests
	{
		private const string Page = "https://site.test/page.html";
		private const string CssUrl = "https://site.test/style.css";

		[Fact]
		public void ExtractFromCss_SchemesAndGuards_ResolvesOnlyRealUrls()
		{
			const string css = @"
				.a { background: url(#frag); }
				.b { background: url(?query); }
				.c { background: url(mailto:a@b.com); }
				.d { background: url(tel:+1234); }
				.e { background: url(javascript:x); }
				.f { background: url(//cdn.test/f.woff); }
				.g { background: url(/10000); }
				.h { background: url(/fonts/real.woff); }
				.i { background: url(data:image/png;base64,AAAA); }
				.j { background: url(); }";

			var results = UrlExtractor.ExtractFromCss(css, CssUrl);

			// Only the protocol-relative and the genuine path survive.
			Assert.Equal(2, results.Count);
			Assert.Contains(results, r => r.Url == "https://cdn.test/f.woff");
			Assert.Contains(results, r => r.Url == "https://site.test/fonts/real.woff");
		}

		[Fact]
		public void ExtractFromHtml_InvalidPageUrl_ReturnsEmpty()
		{
			var results = UrlExtractor.ExtractFromHtml(
				"<link rel=\"stylesheet\" href=\"/style.css\">", "::not a valid uri::");

			Assert.Empty(results);
		}

		[Fact]
		public void ExtractFromCss_InvalidCssUrl_ReturnsEmpty()
		{
			var results = UrlExtractor.ExtractFromCss(".a { background: url(/x.png); }", "::not a valid uri::");

			Assert.Empty(results);
		}

		[Fact]
		public void ExtractFromHtml_NumericPaths_AreSkipped()
		{
			// Both carriers resolve to purely numeric paths → rejected by the guard.
			const string html =
				"<div data-link=\"/10000\"></div>" +
				"<link rel=\"stylesheet\" href=\"/20000\">";

			var results = UrlExtractor.ExtractFromHtml(html, Page);

			Assert.Empty(results);
		}

		[Fact]
		public void ExtractFromHtml_JsonPaths_ExtractedWhenPrefixConfigured()
		{
			const string html = "<script>var cfg = {\"endpoint\":\"/api/items/list\"};</script>";

			var results = UrlExtractor.ExtractFromHtml(html, Page, jsonPathPrefixes: new[] { "/api" });

			Assert.Contains(results, r =>
				r.Source == UrlExtractor.ExtractedSource.JsonPath
				&& r.Url == "https://site.test/api/items/list");
		}
	}
}
