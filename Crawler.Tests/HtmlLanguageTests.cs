using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for HtmlLanguage.GetLanguageFromMeta, relocated from ToolsHelpersTests
	/// as part of dissolving Tools. The file-reading wrapper GetLanguageFromHtmlFile
	/// is [ExcludeFromCodeCoverage] (filesystem I/O); the resolution logic tested here
	/// lives in GetLanguageFromMeta.
	/// </summary>
	public class HtmlLanguageTests
	{
		// Test helper (duplicated from ToolsHelpersTests so this file stays self-contained).

		private static HtmlDocument LoadHtml(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		// ── GetLanguageFromMeta ──────────────────────────────────────────
		// Returns: <html lang=...> normalized → <meta name="language" content=...> → fallback.

		[Fact]
		public void GetLanguageFromMeta_HtmlLangPresent_WinsOverMeta()
		{
			var doc = LoadHtml("<html lang=\"de\"><head><meta name=\"language\" content=\"en\"></head></html>");
			Assert.Equal("de", HtmlLanguage.GetLanguageFromMeta(doc, "xx"));
		}

		[Fact]
		public void GetLanguageFromMeta_HtmlLangRegional_NormalizesToBaseCode()
		{
			// "de-DE" → "de", "en-US" → "en".
			var doc = LoadHtml("<html lang=\"de-DE\"></html>");
			Assert.Equal("de", HtmlLanguage.GetLanguageFromMeta(doc, "xx"));
		}

		[Fact]
		public void GetLanguageFromMeta_HtmlLangInvalidIso_FallsThroughToMeta()
		{
			// "nonsense" isn't a valid ISO 639-1 code, so the meta is consulted.
			var doc = LoadHtml("<html lang=\"nonsense\"><head><meta name=\"language\" content=\"fr\"></head></html>");
			Assert.Equal("fr", HtmlLanguage.GetLanguageFromMeta(doc, "xx"));
		}

		[Fact]
		public void GetLanguageFromMeta_NoHtmlLang_UsesMeta()
		{
			var doc = LoadHtml("<html><head><meta name=\"language\" content=\"it\"></head></html>");
			Assert.Equal("it", HtmlLanguage.GetLanguageFromMeta(doc, "xx"));
		}

		[Fact]
		public void GetLanguageFromMeta_NeitherSource_ReturnsFallback()
		{
			var doc = LoadHtml("<html><head><title>none</title></head></html>");
			Assert.Equal("xx", HtmlLanguage.GetLanguageFromMeta(doc, "xx"));
		}

		[Fact]
		public void GetLanguageFromMeta_HtmlLangEmpty_FallsThroughToMeta()
		{
			var doc = LoadHtml("<html lang=\"  \"><head><meta name=\"language\" content=\"de\"></head></html>");
			Assert.Equal("de", HtmlLanguage.GetLanguageFromMeta(doc, "xx"));
		}
	}
}
