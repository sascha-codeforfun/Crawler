using Xunit;
using Crawler.Urls;

namespace Crawler.Tests.Urls
{
	/// <summary>
	/// Tests for Extractor.FromHtml() and FromCss().
	/// All tests are pure in-memory — no file I/O, no Logger dependency.
	/// </summary>
	public class ExtractorTests
	{
		private const string Base = "https://example.com/page";

		// ── data-* attributes ─────────────────────────────────────────────────

		[Fact]
		public void FromHtml_DataAttr_RelativePath_Extracted()
		{
			var html = "<div data-pdf-link=\"/content/assets/doc.pdf\"></div>";
			var results = Extractor.FromHtml(html, Base);
			Assert.Contains(results, r =>
				r.Url == "https://example.com/content/assets/doc.pdf" &&
				r.Source == Extractor.ExtractedSource.DataAttribute);
		}

		[Fact]
		public void FromHtml_DataAttr_AbsoluteUrl_Extracted()
		{
			var html = "<div data-link=\"https://example.com/page.html\"></div>";
			var results = Extractor.FromHtml(html, Base);
			Assert.Contains(results, r =>
				r.Url == "https://example.com/page.html" &&
				r.Source == Extractor.ExtractedSource.DataAttribute);
		}

		[Fact]
		public void FromHtml_DataAttr_SourceDetailContainsAttrName()
		{
			var html = "<div data-link=\"/content/assets/doc.pdf\"></div>";
			var results = Extractor.FromHtml(html, Base);
			var hit = results.Find(r => r.Source == Extractor.ExtractedSource.DataAttribute);
			Assert.NotNull(hit);
			Assert.Contains("data-link", hit!.SourceDetail);
		}

		[Fact]
		public void FromHtml_DataAttr_NumericValue_Skipped()
		{
			// Numeric values are component config (timeouts, pixel sizes) — not URLs
			var html = "<div data-timeout=\"10000\"></div>";
			var results = Extractor.FromHtml(html, Base);
			Assert.DoesNotContain(results, r => r.Source == Extractor.ExtractedSource.DataAttribute);
		}

		[Fact]
		public void FromHtml_DataAttr_NegativeNumericValue_Skipped()
		{
			var html = "<div data-id=\"-1836464028\"></div>";
			var results = Extractor.FromHtml(html, Base);
			Assert.DoesNotContain(results, r => r.Source == Extractor.ExtractedSource.DataAttribute);
		}

		[Fact]
		public void FromHtml_DataAttr_Fragment_Skipped()
		{
			var html = "<div data-link=\"#section\"></div>";
			var results = Extractor.FromHtml(html, Base);
			Assert.DoesNotContain(results, r => r.Source == Extractor.ExtractedSource.DataAttribute
				&& r.Url.Contains("#section"));
		}

		// ── <script src> ──────────────────────────────────────────────────────

		[Fact]
		public void FromHtml_ScriptSrc_Extracted()
		{
			var html = "<script src=\"/etc/clientlibs/app.min.js\"></script>";
			var results = Extractor.FromHtml(html, Base);
			Assert.Contains(results, r =>
				r.Url == "https://example.com/etc/clientlibs/app.min.js" &&
				r.Source == Extractor.ExtractedSource.ScriptSrc);
		}

		[Fact]
		public void FromHtml_ScriptSrc_AbsoluteExternal_Extracted()
		{
			var html = "<script src=\"https://cdn.example.net/lib.js\"></script>";
			var results = Extractor.FromHtml(html, Base);
			Assert.Contains(results, r =>
				r.Url == "https://cdn.example.net/lib.js" &&
				r.Source == Extractor.ExtractedSource.ScriptSrc);
		}

		// ── <link href> ───────────────────────────────────────────────────────

		[Fact]
		public void FromHtml_LinkHref_StylesheetExtracted()
		{
			var html = "<link rel=\"stylesheet\" href=\"/etc/clientlibs/styles.css\">";
			var results = Extractor.FromHtml(html, Base);
			Assert.Contains(results, r =>
				r.Url == "https://example.com/etc/clientlibs/styles.css" &&
				r.Source == Extractor.ExtractedSource.LinkHref);
		}

		[Fact]
		public void FromHtml_LinkHref_HrefBeforeRel_Extracted()
		{
			var html = "<link href=\"/etc/clientlibs/styles.css\" rel=\"stylesheet\">";
			var results = Extractor.FromHtml(html, Base);
			Assert.Contains(results, r =>
				r.Url == "https://example.com/etc/clientlibs/styles.css" &&
				r.Source == Extractor.ExtractedSource.LinkHref);
		}

		[Fact]
		public void FromHtml_LinkHref_CanonicalSkipped()
		{
			var html = "<link rel=\"canonical\" href=\"https://example.com/canonical\">";
			var results = Extractor.FromHtml(html, Base);
			Assert.DoesNotContain(results, r =>
				r.Source == Extractor.ExtractedSource.LinkHref &&
				r.Url.Contains("canonical"));
		}

		// ── <form action> ─────────────────────────────────────────────────────

		[Fact]
		public void FromHtml_FormAction_Extracted()
		{
			var html = "<form action=\"/de/home/login.html\" method=\"post\">";
			var results = Extractor.FromHtml(html, Base);
			Assert.Contains(results, r =>
				r.Url == "https://example.com/de/home/login.html" &&
				r.Source == Extractor.ExtractedSource.FormAction);
		}

		[Fact]
		public void FromHtml_FormAction_BinEndpoint_Skipped()
		{
			var html = "<form action=\"/content/eprivacy/_jcr_content.bin\" method=\"post\">";
			var results = Extractor.FromHtml(html, Base);
			Assert.DoesNotContain(results, r => r.Source == Extractor.ExtractedSource.FormAction);
		}

		[Fact]
		public void FromHtml_FormAction_NoExtension_Skipped()
		{
			var html = "<form action=\"/.eprivacy_optin_accept\" method=\"post\">";
			var results = Extractor.FromHtml(html, Base);
			Assert.DoesNotContain(results, r => r.Source == Extractor.ExtractedSource.FormAction);
		}

		// ── JSON paths in <script> blocks ─────────────────────────────────────

		[Fact]
		public void FromHtml_JsonPath_ContentDamPath_Extracted()
		{
			var html = "<script>{\"pdfUrl\":\"/content/assets/docs/guide.pdf\"}</script>";
			var prefixes = new List<string> { "/content/", "/en/" };
			var results = Extractor.FromHtml(html, Base, prefixes);
			Assert.Contains(results, r =>
				r.Url == "https://example.com/content/assets/docs/guide.pdf" &&
				r.Source == Extractor.ExtractedSource.JsonPath);
		}

		[Fact]
		public void FromHtml_JsonPath_DePath_Extracted()
		{
			var html = "<script>var x = \"/de/home/page.html\";</script>";
			var prefixes = new List<string> { "/content/", "/de/" };
			var results = Extractor.FromHtml(html, Base, prefixes);
			Assert.Contains(results, r =>
				r.Url == "https://example.com/de/home/page.html" &&
				r.Source == Extractor.ExtractedSource.JsonPath);
		}

		[Fact]
		public void FromHtml_JsonPath_ShortPath_Skipped()
		{
			// Paths under 4 chars after prefix are skipped — too short to be real URLs
			var html = "<script>var x = \"/en/x\";</script>";
			var results = Extractor.FromHtml(html, Base);
			Assert.DoesNotContain(results, r => r.Url.EndsWith("/en/x"));
		}

		[Fact]
		public void FromHtml_JsonPath_NoPrefixes_Skipped()
		{
			// JSON path extraction disabled when no prefixes configured
			var html = "<script>{\"pdfUrl\":\"/content/assets/docs/guide.pdf\"}</script>";
			var results = Extractor.FromHtml(html, Base);
			Assert.DoesNotContain(results, r => r.Source == Extractor.ExtractedSource.JsonPath);
		}

		// ── Resolve helpers ───────────────────────────────────────────────────

		[Fact]
		public void FromHtml_MailtoSkipped()
		{
			var html = "<div data-email=\"mailto:test@example.com\"></div>";
			var results = Extractor.FromHtml(html, Base);
			Assert.DoesNotContain(results, r => r.Url.StartsWith("mailto:"));
		}

		[Fact]
		public void FromHtml_JavascriptSkipped()
		{
			var html = "<form action=\"javascript:void(0)\">";
			var results = Extractor.FromHtml(html, Base);
			Assert.DoesNotContain(results, r => r.Url.StartsWith("javascript:"));
		}

		[Fact]
		public void FromHtml_ProtocolRelative_ResolvedAsHttps()
		{
			var html = "<script src=\"//cdn.example.com/lib.js\"></script>";
			var results = Extractor.FromHtml(html, Base);
			Assert.Contains(results, r => r.Url == "https://cdn.example.com/lib.js");
		}

		// ── CSS extraction ────────────────────────────────────────────────────

		[Fact]
		public void FromCss_UrlFunction_Extracted()
		{
			var css = "body { background: url('/content/assets/bg.jpg'); }";
			var results = Extractor.FromCss(css, "https://example.com/styles.css");
			Assert.Contains(results, r =>
				r.Url == "https://example.com/content/assets/bg.jpg" &&
				r.Source == Extractor.ExtractedSource.CssUrl);
		}

		[Fact]
		public void FromCss_DataUri_Skipped()
		{
			var css = "body { background: url('data:image/png;base64,abc'); }";
			var results = Extractor.FromCss(css, "https://example.com/styles.css");
			Assert.Empty(results);
		}

		[Fact]
		public void FromCss_RelativeFont_Resolved()
		{
			var css = "@font-face { src: url('../fonts/myfont.woff2'); }";
			var results = Extractor.FromCss(css, "https://example.com/css/styles.css");
			Assert.Contains(results, r => r.Url.Contains("myfont.woff2"));
		}
	}
}
