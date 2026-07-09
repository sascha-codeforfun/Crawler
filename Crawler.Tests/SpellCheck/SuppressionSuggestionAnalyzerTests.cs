using System;
using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 627: the script-source suppression-suggestion log (27-spellcheck-suppression-
	/// suggestions.log). The A/B signal is a "resembles a real word" predicate supplied by the caller,
	/// but it is consulted ONLY for purely-alphabetic literals — a hyphen/slash/digit marks an
	/// identifier or path, which goes straight to SUGGEST. Covers the tiering contract, the alphabetic
	/// guard, page-spread ranking, the two ready-to-paste blocks (SUGGEST and REVIEW), and the load-
	/// bearing safety property: a real typo inside a sentence literal can never be auto-suggested.
	/// All fixtures synthetic and generic.
	/// </summary>
	public class SuppressionSuggestionAnalyzerTests
	{
		private static readonly Func<string, bool> NeverReal = _ => false;

		private static Func<string, bool> RealWords(params string[] words) => w => words.Contains(w);

		private static ScriptSuppressionInput In(string url, string word, string literal, string context = "")
			=> new(url, word, literal, string.IsNullOrEmpty(context) ? literal : context);

		// Returns the body of the "## <header...>" section up to the next "##" marker.
		private static string Section(string log, string headerStartsWith)
		{
			int start = log.IndexOf("## " + headerStartsWith, StringComparison.Ordinal);
			Assert.True(start >= 0, $"section '{headerStartsWith}' not found");
			int next = log.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
			return next < 0 ? log.Substring(start) : log.Substring(start, next - start);
		}

		private static string SuggestPaste(string log) => Section(log, "Ready to paste — SUGGEST");
		private static string ReviewPaste(string log) => Section(log, "Ready to paste — REVIEW");

		// ---- tiering ----

		[Fact]
		public void SingleToken_NotRealWord_LandsInSuggest()
		{
			var log = SuppressionSuggestionAnalyzer.Compose(new[] { In("u1", "techkey", "techkey") }, NeverReal);

			Assert.Contains("techkey", Section(log, "SUGGEST —"));
			Assert.Contains("\"techkey\"", SuggestPaste(log));
			Assert.DoesNotContain("techkey", Section(log, "REVIEW"));
		}

		[Fact]
		public void SingleToken_RealWord_LandsInReview_AndInReviewPasteOnly()
		{
			// Clean alphabetic literal whose word resembles a real word → REVIEW.
			var log = SuppressionSuggestionAnalyzer.Compose(new[] { In("u1", "aktien", "aktien") }, RealWords("aktien"));

			Assert.Contains("aktien", Section(log, "REVIEW"));
			Assert.DoesNotContain("aktien", Section(log, "SUGGEST —"));
			// Appears in the REVIEW paste block, never the SUGGEST one.
			Assert.Contains("\"aktien\"", ReviewPaste(log));
			Assert.DoesNotContain("\"aktien\"", SuggestPaste(log));
		}

		[Fact]
		public void PunctuatedLiteral_ThatResemblesRealWord_StaysInSuggest()
		{
			// The literal "submit/" has a slash, so the tokenizer's word is the clean word
			// "submit" — but the alphabetic guard must keep the LITERAL in SUGGEST regardless of the
			// predicate, because a path/identifier is not prose.
			var log = SuppressionSuggestionAnalyzer.Compose(new[] { In("u1", "submit", "submit/") }, RealWords("submit"));

			Assert.Contains("submit/", Section(log, "SUGGEST —"));
			Assert.Contains("\"submit/\"", SuggestPaste(log));
			Assert.DoesNotContain("submit/", Section(log, "REVIEW"));
		}

		[Fact]
		public void HyphenatedLiteral_StaysInSuggest_EvenIfPredicateAccepts()
		{
			var log = SuppressionSuggestionAnalyzer.Compose(new[] { In("u1", "auto-scroll", "auto-scroll") }, RealWords("auto-scroll", "Auto-scroll"));

			Assert.Contains("auto-scroll", Section(log, "SUGGEST —"));
			Assert.DoesNotContain("auto-scroll", Section(log, "REVIEW"));
		}

		// ---- the safety property ----

		[Fact]
		public void RealTypoInsideSentenceLiteral_LandsInNotSuggested_EvenIfPredicateSaysReal()
		{
			// A genuine misspelling ("Beispeil" for "Beispiel") inside a multiword prose literal. Even
			// with a predicate that WOULD call it a real word, the multiword gate must win: counted as
			// NOT SUGGESTED, never reaching SUGGEST/REVIEW or either paste block.
			var log = SuppressionSuggestionAnalyzer.Compose(
				new[] { In("u1", "Beispeil", "das ist ein Beispeil") },
				RealWords("Beispeil"));

			Assert.Contains("NOT SUGGESTED — 1 multiword", log);
			Assert.DoesNotContain("Beispeil", Section(log, "SUGGEST —"));
			Assert.DoesNotContain("Beispeil", Section(log, "REVIEW"));
			Assert.DoesNotContain("Beispeil", SuggestPaste(log));
			Assert.DoesNotContain("Beispeil", ReviewPaste(log));
		}

		// ---- spread ----

		[Fact]
		public void Suggest_RanksByDistinctUrlSpread()
		{
			var log = SuppressionSuggestionAnalyzer.Compose(new[]
			{
				In("u1", "rare", "rare"),
				In("u1", "wide", "wide"),
				In("u2", "wide", "wide"),
				In("u3", "wide", "wide"),
			}, NeverReal);

			var suggest = Section(log, "SUGGEST —");
			Assert.True(
				suggest.IndexOf("wide", StringComparison.Ordinal) < suggest.IndexOf("rare", StringComparison.Ordinal),
				"higher page-spread literal should rank first");
		}

		[Fact]
		public void Grouping_IsCaseInsensitive_SpreadCountsDistinctUrls()
		{
			var log = SuppressionSuggestionAnalyzer.Compose(new[]
			{
				In("u1", "configkey", "configkey"),
				In("u2", "configkey", "CONFIGKEY"),
			}, NeverReal);

			Assert.Contains("## SUGGEST — 1 literal(s)", log);
			Assert.Contains("2×", Section(log, "SUGGEST —"));
		}

		// ---- degenerate input ----

		[Fact]
		public void EmptyList_ProducesHeaderWithEmptySections_AndBothPasteBlocks()
		{
			var log = SuppressionSuggestionAnalyzer.Compose(Array.Empty<ScriptSuppressionInput>(), NeverReal);

			Assert.Contains("## SUGGEST — 0 literal(s)", log);
			Assert.Contains("## REVIEW (low confidence — resembles a real word) — 0 literal(s)", log);
			Assert.Contains("## NOT SUGGESTED — 0 multiword", log);
			Assert.Contains("## Ready to paste — SUGGEST", log);
			Assert.Contains("## Ready to paste — REVIEW", log);
		}
	}
}
