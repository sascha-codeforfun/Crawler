using HtmlAgilityPack;
using Xunit;
using Crawler.Html;

namespace Crawler.Tests.Html
{
	/// <summary>
	/// Tests for Language.FromMeta, relocated from ToolsHelpersTests
	/// as part of dissolving Tools. The file-reading wrapper FromHtmlFile
	/// is [ExcludeFromCodeCoverage] (filesystem I/O); the resolution logic tested here
	/// lives in FromMeta.
	/// </summary>
	public class LanguageTests
	{
		// Test helper (duplicated from ToolsHelpersTests so this file stays self-contained).

		private static HtmlDocument LoadHtml(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		// ── FromMeta ──────────────────────────────────────────
		// Returns: <html lang=...> normalized → <meta name="language" content=...> → fallback.

		[Fact]
		public void FromMeta_HtmlLangPresent_WinsOverMeta()
		{
			var doc = LoadHtml("<html lang=\"de\"><head><meta name=\"language\" content=\"en\"></head></html>");
			Assert.Equal("de", Language.FromMeta(doc, "xx"));
		}

		[Fact]
		public void FromMeta_HtmlLangRegional_NormalizesToBaseCode()
		{
			// "de-DE" → "de", "en-US" → "en".
			var doc = LoadHtml("<html lang=\"de-DE\"></html>");
			Assert.Equal("de", Language.FromMeta(doc, "xx"));
		}

		[Fact]
		public void FromMeta_HtmlLangInvalidIso_FallsThroughToMeta()
		{
			// "nonsense" isn't a valid ISO 639-1 code, so the meta is consulted.
			var doc = LoadHtml("<html lang=\"nonsense\"><head><meta name=\"language\" content=\"fr\"></head></html>");
			Assert.Equal("fr", Language.FromMeta(doc, "xx"));
		}

		[Fact]
		public void FromMeta_NoHtmlLang_UsesMeta()
		{
			var doc = LoadHtml("<html><head><meta name=\"language\" content=\"it\"></head></html>");
			Assert.Equal("it", Language.FromMeta(doc, "xx"));
		}

		[Fact]
		public void FromMeta_NeitherSource_ReturnsFallback()
		{
			var doc = LoadHtml("<html><head><title>none</title></head></html>");
			Assert.Equal("xx", Language.FromMeta(doc, "xx"));
		}

		[Fact]
		public void FromMeta_HtmlLangEmpty_FallsThroughToMeta()
		{
			var doc = LoadHtml("<html lang=\"  \"><head><meta name=\"language\" content=\"de\"></head></html>");
			Assert.Equal("de", Language.FromMeta(doc, "xx"));
		}

		// ── IsISO6391 ─────────────────────────────────────────────────────────

		[Theory]
		[InlineData("en", true)]
		[InlineData("de", true)]
		[InlineData("EN", false)]   // pattern is lower-case only
		[InlineData("eng", false)]  // three letters
		[InlineData("e", false)]
		[InlineData("e1", false)]
		public void IsISO6391_ValidatesTwoLetterLowercaseCodes(string code, bool expected)
		{
			Assert.Equal(expected, Language.IsISO6391(code));
		}

		// ── NearestElementLanguage ────────────────────────────
		// Walks ancestors from a node; nearest valid lang wins (child overrides parent),
		// "de-DE" → "de", invalid codes skipped, page/fallback as floor.

		[Fact]
		public void NearestElementLanguage_NestedIsland_ChildWins()
		{
			var doc = LoadHtml("<html lang=\"en\"><body><div lang=\"ar\"><p id=\"t\">x</p></div></body></html>");
			Assert.Equal("ar", Language.NearestElementLanguage(doc.GetElementbyId("t"), "xx"));
		}

		[Fact]
		public void NearestElementLanguage_NoNearerLang_WalksToHtmlLang()
		{
			var doc = LoadHtml("<html lang=\"en\"><body><p id=\"t\">x</p></body></html>");
			Assert.Equal("en", Language.NearestElementLanguage(doc.GetElementbyId("t"), "xx"));
		}

		[Fact]
		public void NearestElementLanguage_NoLangAnywhere_ReturnsFallback()
		{
			var doc = LoadHtml("<html><body><p id=\"t\">x</p></body></html>");
			Assert.Equal("de", Language.NearestElementLanguage(doc.GetElementbyId("t"), "de"));
		}

		[Fact]
		public void NearestElementLanguage_RegionalSubtag_NormalisesToBase()
		{
			var doc = LoadHtml("<html><body><div lang=\"de-DE\"><p id=\"t\">x</p></div></body></html>");
			Assert.Equal("de", Language.NearestElementLanguage(doc.GetElementbyId("t"), "xx"));
		}

		[Fact]
		public void NearestElementLanguage_InvalidLang_SkippedKeepsWalking()
		{
			// "klingon" isn't ISO 639-1, so the walk continues past it to <html lang="en">.
			var doc = LoadHtml("<html lang=\"en\"><body><div lang=\"klingon\"><p id=\"t\">x</p></div></body></html>");
			Assert.Equal("en", Language.NearestElementLanguage(doc.GetElementbyId("t"), "xx"));
		}

		[Fact]
		public void NearestElementLanguage_NullNode_ReturnsFallback()
		{
			Assert.Equal("fr", Language.NearestElementLanguage(null, "fr"));
		}
	}
}
