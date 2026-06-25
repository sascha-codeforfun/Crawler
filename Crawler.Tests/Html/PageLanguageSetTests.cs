using System.Collections.Generic;
using Xunit;

namespace Crawler.Tests.Html
{
	/// <summary>
	/// D049 — the centralized page-language-set resolver (Crawler.Html.PageLanguageSet).
	/// Pins the resolution chain (override → &lt;html lang&gt; → &lt;meta language&gt; →
	/// default), longest-prefix override precedence, and the set semantics the quote
	/// system check relies on: exactly one declared language is an anchor; zero (empty
	/// default) or many is "no anchor" (empty / multi-element set).
	/// </summary>
	public class PageLanguageSetTests
	{
		private static Dictionary<string, List<string>> Overrides() => new()
		{
			["/fi/home/"] = new() { "de" },                                   // mislabelled branch → corrected
			["/de/home/service.html"] = new() { "de", "en" },
			["/de/home/service/quick-guides.html"] = new() { "de", "en", "cs" },
		};

		private static HtmlAgilityPack.HtmlDocument Doc(string html)
		{
			var d = new HtmlAgilityPack.HtmlDocument();
			d.LoadHtml(html);
			return d;
		}

		// ── core overload (pre-resolved branch language) ─────────────────────

		[Fact]
		public void Core_NoOverride_NonEmptyBranch_ReturnsBranchSingleton()
		{
			var set = Crawler.Html.PageLanguageSet.Resolve("https://x/de/home/p.html", "de", null);
			Assert.Equal(new[] { "de" }, set);
		}

		[Fact]
		public void Core_NoOverride_EmptyBranch_ReturnsEmptySet()
		{
			// The agnostic lever: nothing declared + empty default ⇒ no anchor.
			var set = Crawler.Html.PageLanguageSet.Resolve("https://x/de/home/p.html", "", null);
			Assert.Empty(set);
		}

		[Fact]
		public void Core_OverrideWins_LongestPrefix()
		{
			var set = Crawler.Html.PageLanguageSet.Resolve(
				"https://x/de/home/service/quick-guides.html", "de", Overrides());
			Assert.Equal(new[] { "de", "en", "cs" }, set);
		}

		[Fact]
		public void Core_OverrideWins_OverDeclaredBranch()
		{
			// /fi/home/ page is really German; the override forces de regardless of branch.
			var set = Crawler.Html.PageLanguageSet.Resolve(
				"https://x/fi/home/foo.html", "fi", Overrides());
			Assert.Equal(new[] { "de" }, set);
		}

		// ── doc overload (branch resolved via <html lang> / <meta language>) ─

		[Fact]
		public void Doc_HtmlLangWins_OverMeta()
		{
			var doc = Doc("<html lang=\"de\"><head><meta name=\"language\" content=\"en\"></head><body></body></html>");
			var set = Crawler.Html.PageLanguageSet.Resolve("https://x/p.html", doc, null, "");
			Assert.Equal(new[] { "de" }, set);
		}

		[Fact]
		public void Doc_MetaUsed_WhenNoHtmlLang()
		{
			var doc = Doc("<html><head><meta name=\"language\" content=\"en\"></head><body></body></html>");
			var set = Crawler.Html.PageLanguageSet.Resolve("https://x/p.html", doc, null, "");
			Assert.Equal(new[] { "en" }, set);
		}

		[Fact]
		public void Doc_Undeclared_EmptyDefault_ReturnsEmptySet()
		{
			var doc = Doc("<html><body><p>no language declared</p></body></html>");
			var set = Crawler.Html.PageLanguageSet.Resolve("https://x/p.html", doc, null, "");
			Assert.Empty(set);
		}

		[Fact]
		public void Doc_Undeclared_NonEmptyDefault_ReturnsDefaultSingleton()
		{
			// Spelling passes a real default, so it always gets at least one language.
			var doc = Doc("<html><body><p>no language declared</p></body></html>");
			var set = Crawler.Html.PageLanguageSet.Resolve("https://x/p.html", doc, null, "de");
			Assert.Equal(new[] { "de" }, set);
		}

		[Fact]
		public void Doc_OverrideWins_OverHonestHtmlLang()
		{
			var doc = Doc("<html lang=\"fi\"><body></body></html>");
			var set = Crawler.Html.PageLanguageSet.Resolve(
				"https://x/fi/home/foo.html", doc, Overrides(), "");
			Assert.Equal(new[] { "de" }, set);
		}

		[Fact]
		public void Doc_MultiLanguageOverride_YieldsNoSingleAnchor()
		{
			// de+en page → set size 2 → the quote system check must NOT anchor on one.
			var doc = Doc("<html lang=\"de\"><body></body></html>");
			var set = Crawler.Html.PageLanguageSet.Resolve(
				"https://x/de/home/service.html", doc, Overrides(), "");
			Assert.Equal(new[] { "de", "en" }, set);
			Assert.NotEqual(1, set.Count);
		}
	}
}
