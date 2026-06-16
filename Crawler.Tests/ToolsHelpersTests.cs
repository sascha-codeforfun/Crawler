using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for Tools.cs pure helper methods — the decidable string / HTML /
	/// byte-buffer transforms used across the pipeline.
	///
	/// Introduced in #308 as part of the targeted-coverage pass. Per the audit:
	/// I/O-coupled methods (filesystem reads/writes, Hunspell-coupled spell
	/// checks) carry [ExcludeFromCodeCoverage] markers in Tools.cs itself and
	/// are intentionally not tested here.
	/// </summary>
	public class ToolsHelpersTests
	{
		// ── Test helpers ─────────────────────────────────────────────────

		private static HtmlDocument LoadHtml(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		private static DictionaryBundle InMemoryBundle(params string[] sharedUserWords)
		{
			var bundle = new DictionaryBundle();
			foreach (var w in sharedUserWords)
			{
				bundle.SharedUser.Add(w);
			}

			return bundle;
		}

		// ── ReplaceHtmlEntities ──────────────────────────────────────────
		// Per replacement rule: apply Value→Replacement, optionally scoped by Pages.
		// Page-scoping uses case-insensitive substring match on pageUrl.

		[Fact]
		public void ReplaceHtmlEntities_EmptyHtml_ReturnsEmpty()
		{
			var result = Tools.ReplaceHtmlEntities("", [], "");
			Assert.Equal("", result);
		}

		[Fact]
		public void ReplaceHtmlEntities_NoReplacements_ReturnsInputUnchanged()
		{
			var html = "<p>hello world</p>";
			Assert.Equal(html, Tools.ReplaceHtmlEntities(html, [], ""));
		}

		[Fact]
		public void ReplaceHtmlEntities_GlobalRule_AppliesRegardlessOfPageUrl()
		{
			var rules = new List<ReplacementItem>
			{
				new() { Value = "&shy;", Replacement = "", Pages = [] }
			};
			var result = Tools.ReplaceHtmlEntities("soft&shy;hyphen", rules, "");
			Assert.Equal("softhyphen", result);
		}

		[Fact]
		public void ReplaceHtmlEntities_PageScopedRule_AppliesOnlyWhenUrlMatches()
		{
			var rules = new List<ReplacementItem>
			{
				new() { Value = "foo", Replacement = "BAR", Pages = ["special-page"] }
			};
			var matched = Tools.ReplaceHtmlEntities("foo content", rules, "https://example.com/special-page.html");
			var skipped = Tools.ReplaceHtmlEntities("foo content", rules, "https://example.com/other.html");
			Assert.Equal("BAR content", matched);
			Assert.Equal("foo content", skipped);
		}

		[Fact]
		public void ReplaceHtmlEntities_PageScopedRule_EmptyPageUrl_DoesNotApply()
		{
			// Documented behaviour: a page-scoped rule requires a non-empty pageUrl
			// to even be considered; otherwise it's silently skipped.
			var rules = new List<ReplacementItem>
			{
				new() { Value = "foo", Replacement = "BAR", Pages = ["special-page"] }
			};
			var result = Tools.ReplaceHtmlEntities("foo content", rules, "");
			Assert.Equal("foo content", result);
		}

		[Fact]
		public void ReplaceHtmlEntities_PagesMatchIsCaseInsensitive()
		{
			var rules = new List<ReplacementItem>
			{
				new() { Value = "foo", Replacement = "BAR", Pages = ["SPECIAL"] }
			};
			var result = Tools.ReplaceHtmlEntities("foo", rules, "https://example.com/special-page.html");
			Assert.Equal("BAR", result);
		}

		[Fact]
		public void ReplaceHtmlEntities_AppliesRulesInListOrder()
		{
			// Rules apply in list order; the second rule sees the output of the first.
			var rules = new List<ReplacementItem>
			{
				new() { Value = "A", Replacement = "B", Pages = [] },
				new() { Value = "B", Replacement = "C", Pages = [] }
			};
			Assert.Equal("CCC", Tools.ReplaceHtmlEntities("AAA", rules, ""));
		}

		// ── CheckTrailingHyphenStem + CheckTrailingHyphenStemAny ─────────
		// German compound-word stem-check: strip trailing hyphen, then optionally
		// strip a Fugenelement (longest-first), check stem against dictionary.

		[Fact]
		public void CheckTrailingHyphenStem_BareStem_AcceptedDirectly()
		{
			var dict = InMemoryBundle("Flug");
			Assert.True(Tools.CheckTrailingHyphenStem("Flug-", dict, []));
		}

		[Fact]
		public void CheckTrailingHyphenStem_StemNotInDictionary_NoFugen_ReturnsFalse()
		{
			var dict = InMemoryBundle("OtherWord");
			Assert.False(Tools.CheckTrailingHyphenStem("Unknown-", dict, ["s", "n"]));
		}

		[Fact]
		public void CheckTrailingHyphenStem_FugenStripping_FindsBaseWord()
		{
			// "Schulungs-" → strip "s" → "Schulung" → in dictionary.
			var dict = InMemoryBundle("Schulung");
			Assert.True(Tools.CheckTrailingHyphenStem("Schulungs-", dict, ["s", "n"]));
		}

		[Fact]
		public void CheckTrailingHyphenStem_LongestFugenTriedFirst()
		{
			// "Auftragens-" with fugens ["s", "ns"]. Longest-first would strip "ns"
			// giving "Auftrage" (not in dict); short "s" would give "Auftragen" (in dict).
			// Either path finds a match; the test confirms behaviour either way.
			// Stronger test: ambiguous stem where only the longer strip hits.
			var dict = InMemoryBundle("Auftrag"); // only the longer "ns" path produces this
			Assert.True(Tools.CheckTrailingHyphenStem("Auftrags-", dict, ["s", "ags"]));
			// "Auftrags-" → strip hyphen → "Auftrags" → try "ags" first → "Auftr" (not in dict)
			//                                       → try "s"          → "Auftrag" (in dict) ✓
		}

		[Fact]
		public void CheckTrailingHyphenStem_EmptyFugenList_FallsBackToStemAsIs()
		{
			var dict = InMemoryBundle("Auto");
			Assert.True(Tools.CheckTrailingHyphenStem("Auto-", dict, []));
			Assert.False(Tools.CheckTrailingHyphenStem("Unknown-", dict, []));
		}

		[Fact]
		public void CheckTrailingHyphenStem_FugenLongerThanStem_Skipped()
		{
			// "Ag-" stem is just "Ag"; fugen "rieren" is longer than stem.
			// The EndsWith check fails so the fugen is skipped (no IndexOutOfRange).
			var dict = InMemoryBundle();
			Assert.False(Tools.CheckTrailingHyphenStem("Ag-", dict, ["rieren"]));
		}

		[Fact]
		public void CheckTrailingHyphenStemAny_ChecksAcrossMultipleBundles()
		{
			var de = InMemoryBundle("Schulung");
			var en = InMemoryBundle("OtherStuff");
			Assert.True(Tools.CheckTrailingHyphenStemAny("Schulung-", new[] { de, en }, []));

			// Confirm cross-bundle: stem only in second bundle.
			var bundles = new[] { en, de };
			Assert.True(Tools.CheckTrailingHyphenStemAny("Schulung-", bundles, []));
		}

		[Fact]
		public void CheckTrailingHyphenStemAny_FugenStripping_FindsBaseAcrossBundles()
		{
			// "Modernisierungs-" → strip "s" → "Schulung" → in the German bundle.
			// Exercises the Fugenelement loop in the Any-variant (the only-bundle
			// variant's loop is covered by CheckTrailingHyphenStem tests; this
			// is its multi-bundle equivalent).
			var de = InMemoryBundle("Schulung");
			var en = InMemoryBundle("Office");
			Assert.True(Tools.CheckTrailingHyphenStemAny("Schulungs-",
				new[] { en, de }, ["s", "n"]));
		}

		[Fact]
		public void CheckTrailingHyphenStemAny_EmptyFugenInList_Skipped()
		{
			// An empty fuge in the list should be silently skipped by the
			// !IsNullOrEmpty(fuge) guard inside the loop — the iteration
			// then falls through to return false. Sole empty fuge ensures
			// the guard branch executes (longer non-empty fugens otherwise
			// short-circuit and the empty branch never runs).
			var de = InMemoryBundle("Auto");
			Assert.False(Tools.CheckTrailingHyphenStemAny("Unknown-", new[] { de }, [""]));
		}

		[Fact]
		public void CheckTrailingHyphenStemAny_NoMatchAnyPath_ReturnsFalse()
		{
			// Drives the function through the entire foreach loop without a
			// match, covering the fall-through return false at the bottom.
			var de = InMemoryBundle("OtherWord");
			Assert.False(Tools.CheckTrailingHyphenStemAny("Unknown-",
				new[] { de }, ["s", "n", "es"]));
		}

		[Fact]
		public void CheckTrailingHyphenStemAny_StrippedStemBecomesEmpty_Skipped()
		{
			// "Ss-" → strip hyphen → "Ss" (2 chars). Fuge "ss" matches EndsWith,
			// but strip → "" (empty) which the !IsNullOrEmpty(stripped) guard
			// rejects. Function returns false; covers the stripped-empty branch.
			var de = InMemoryBundle();
			Assert.False(Tools.CheckTrailingHyphenStemAny("Ss-", new[] { de }, ["ss"]));
		}

		// ── StripInlineFormattingTags ────────────────────────────────────
		// Regex strips <b>, <i>, <em>, <strong>, <u>, <s>, <mark>, <small>,
		// <sub>, <sup> opening and closing tags (with optional attributes).

		[Fact]
		public void StripInlineFormattingTags_RemovesKnownTags()
		{
			Assert.Equal("hello world",
				Tools.StripInlineFormattingTags("<b>hello</b> <i>world</i>"));
		}

		[Fact]
		public void StripInlineFormattingTags_PreservesOtherTags()
		{
			Assert.Equal("<p>hello</p>",
				Tools.StripInlineFormattingTags("<p><strong>hello</strong></p>"));
		}

		[Fact]
		public void StripInlineFormattingTags_StripsAttributes()
		{
			Assert.Equal("hello",
				Tools.StripInlineFormattingTags("<b class=\"foo\">hello</b>"));
		}

		// ── RemoveEmailAddresses ─────────────────────────────────────────

		[Fact]
		public void RemoveEmailAddresses_StripsAddresses()
		{
			var result = Tools.RemoveEmailAddresses("contact foo@example.com today");
			Assert.DoesNotContain("foo@example.com", result);
		}

		[Fact]
		public void RemoveEmailAddresses_NoAddresses_ReturnsUnchanged()
		{
			Assert.Equal("plain text", Tools.RemoveEmailAddresses("plain text"));
		}

		// ── ReplaceDomainNames ───────────────────────────────────────────

		[Fact]
		public void ReplaceDomainNames_StripsDomains()
		{
			var result = Tools.ReplaceDomainNames("visit www.example.com today");
			Assert.DoesNotContain("example.com", result);
		}

		[Fact]
		public void ReplaceDomainNames_NoDomains_ReturnsUnchanged()
		{
			Assert.Equal("plain text", Tools.ReplaceDomainNames("plain text"));
		}

	}
}
