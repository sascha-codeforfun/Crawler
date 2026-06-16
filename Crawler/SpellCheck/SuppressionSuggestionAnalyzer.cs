namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using HtmlAgilityPack;

	/// <summary>
	/// One script-source spell finding reduced to the raw material the suppression analysis needs:
	/// the page it was on, the flagged word, the WHOLE decoded literal it came from (the unit that
	/// <c>SpellCheckJavaScript.TokensToFilter</c> matches — whole-literal, case-insensitive), and the
	/// raw-source context window (display only).
	/// </summary>
	public readonly record struct ScriptSuppressionInput(
		string Url,
		string Word,
		string Literal,
		string Context);

	/// <summary>
	/// Builds the suppression-suggestion log (<c>27-spellcheck-suppression-suggestions.log</c>) from a
	/// harvest's SCRIPT-source findings. It supplements the SpellCheckJavaScript feature ONLY — it
	/// never looks at text-node or attribute findings.
	///
	/// The load-bearing safety property is the single-token gate. A TokensToFilter entry suppresses an
	/// ENTIRE literal, so suggesting one is safe only when that literal is a single technical token:
	/// there is then no OTHER word in it a filter could silently hide. A MULTIWORD literal is never
	/// suggested, because a real typo can live inside one — structurally, a prose typo in script lands
	/// in a sentence literal — so those are left to normal triage and can never be auto-suppressed.
	///
	/// Among single-token literals the confidence split asks whether the word RESEMBLES A REAL WORD —
	/// supplied as a predicate (the checker surfaces no correction candidates of its own, so the caller
	/// probes the dictionary directly, typically "is the capitalized form accepted", e.g. "aktien" →
	/// "Aktien"). Resembles nothing → SUGGEST (almost certainly technical); resembles a real word →
	/// REVIEW (could be a genuine word/typo — still suggested, but flagged to verify). Either way the
	/// operator prunes before pasting; A/B is review ergonomics, not a safety boundary.
	/// </summary>
	public static class SuppressionSuggestionAnalyzer
	{
		private enum Tier { Suggest, Review, NotSuggested }

		private sealed record Candidate(string Literal, int Spread, Tier Tier, string Context);

		// A throwaway host node so a bare literal can be tokenized through the REAL tokenizer (the
		// single source of truth for what is one token). SpellTokenizer.Tokenize reads only the run's
		// text, never its node, so this placeholder is never inspected. Compose runs after the parallel
		// harvest, on one thread, so sharing one node is safe.
		private static readonly HtmlNode TokenizerHost = HtmlNode.CreateNode("<x></x>");

		/// <summary>
		/// Composes the full log body. <paramref name="resemblesRealWord"/> is the A/B signal: given a
		/// flagged word it returns true when the word resembles a real dictionary word (→ REVIEW).
		/// Empty input yields a header with empty sections. Line separator is '\n'; the writer
		/// normalises it.
		/// </summary>
		public static string Compose(IReadOnlyList<ScriptSuppressionInput> inputs, Func<string, bool> resemblesRealWord)
		{
			var resembles = resemblesRealWord ?? (_ => false);

			var candidates = (inputs ?? Array.Empty<ScriptSuppressionInput>())
				.Where(x => !string.IsNullOrWhiteSpace(x.Literal))
				.GroupBy(x => x.Literal.Trim(), StringComparer.OrdinalIgnoreCase)
				.Select(g => Summarize(g, resembles))
				.ToList();

			var suggest = candidates.Where(c => c.Tier == Tier.Suggest)
				.OrderByDescending(c => c.Spread).ThenBy(c => c.Literal, StringComparer.OrdinalIgnoreCase).ToList();
			var review = candidates.Where(c => c.Tier == Tier.Review)
				.OrderByDescending(c => c.Spread).ThenBy(c => c.Literal, StringComparer.OrdinalIgnoreCase).ToList();
			int notSuggested = candidates.Count(c => c.Tier == Tier.NotSuggested);

			var sb = new StringBuilder();
			sb.Append("# 27 — script-source suppression suggestions for SpellCheckJavaScript.TokensToFilter\n");
			sb.Append("# SUGGEST: single-token technical literals (whole-literal filter candidates), by page-spread.\n");
			sb.Append("# REVIEW : single-token, but it resembles a real word — verify it is not a real typo.\n");
			sb.Append("# Multiword literals are never suggested: real typos live there and are triaged normally.\n");
			sb.Append('\n');

			sb.Append($"## SUGGEST — {suggest.Count} literal(s)\n");
			if (suggest.Count == 0)
			{
				sb.Append("   (none)\n");
			}

			foreach (var c in suggest)
			{
				sb.Append($"   {c.Spread,4}×  {c.Literal,-30}  {OneLine(c.Context, 80)}\n");
			}

			sb.Append('\n');
			sb.Append($"## REVIEW (low confidence — resembles a real word) — {review.Count} literal(s)\n");
			if (review.Count == 0)
			{
				sb.Append("   (none)\n");
			}

			foreach (var c in review)
			{
				sb.Append($"   {c.Spread,4}×  {c.Literal,-30}  {OneLine(c.Context, 80)}\n");
			}

			sb.Append('\n');
			sb.Append($"## NOT SUGGESTED — {notSuggested} multiword literal(s) (real typos triaged normally)\n");
			sb.Append('\n');

			sb.Append("## Ready to paste — SUGGEST into TokensToFilter (high confidence; prune first):\n");
			sb.Append("\"TokensToFilter\": [\n");
			for (int k = 0; k < suggest.Count; k++)
			{
				string comma = k < suggest.Count - 1 ? "," : string.Empty;
				sb.Append($"  \"{JsonEscape(suggest[k].Literal)}\"{comma}\n");
			}

			sb.Append("]\n");
			sb.Append('\n');

			sb.Append("## Ready to paste — REVIEW into TokensToFilter (low confidence; verify first):\n");
			sb.Append("\"TokensToFilter\": [\n");
			for (int k = 0; k < review.Count; k++)
			{
				string comma = k < review.Count - 1 ? "," : string.Empty;
				sb.Append($"  \"{JsonEscape(review[k].Literal)}\"{comma}\n");
			}

			sb.Append("]\n");
			return sb.ToString();
		}

		private static Candidate Summarize(IGrouping<string, ScriptSuppressionInput> group, Func<string, bool> resemblesRealWord)
		{
			string literal = group.Key; // already trimmed by the grouping key selector
			int spread = group.Select(x => x.Url).Distinct(StringComparer.Ordinal).Count();
			string context = group.Select(x => x.Context).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? literal;
			bool singleToken = WordTokenCount(literal) == 1;

			Tier tier;
			if (!singleToken)
			{
				tier = Tier.NotSuggested;
			}
			else
			{
				// Single-token literals carry exactly one flagged word; use it for the real-word probe.
				// But only probe a PURELY ALPHABETIC literal: a hyphen/slash/digit means it is an
				// identifier or path (e.g. "auto-scroll", "submit/"), not prose a human would
				// mistype — those go straight to SUGGEST. Without this, the tokenizer strips trailing
				// punctuation before the probe, so "submit/" would be checked as the real word
				// "Submit" and mislabelled REVIEW. Umlauts are letters, so German words still qualify.
				string word = group.Select(x => x.Word).FirstOrDefault(w => !string.IsNullOrWhiteSpace(w)) ?? literal;
				bool cleanAlphabetic = literal.All(char.IsLetter);
				tier = (cleanAlphabetic && resemblesRealWord(word)) ? Tier.Review : Tier.Suggest;
			}

			return new Candidate(literal, spread, tier, context);
		}

		// LETTER-bearing token count via the real tokenizer. One → single-token literal (e.g.
		// "auto-scroll", "route/" — punctuation is not a word token). Two or more (whitespace-
		// separated words like "save draft now") → multiword.
		private static int WordTokenCount(string literal)
		{
			var run = new TextRun(TokenizerHost, RunSource.Script, string.Empty, literal);
			return SpellTokenizer.Tokenize(run).Count(t => t.Text.Any(char.IsLetter));
		}

		private static string OneLine(string s, int max)
		{
			s = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
			return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "…";
		}

		private static string JsonEscape(string s)
		{
			var sb = new StringBuilder(s.Length + 2);
			foreach (char c in s)
			{
				switch (c)
				{
					case '\\': sb.Append("\\\\"); break;
					case '"': sb.Append("\\\""); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default: sb.Append(c); break;
				}
			}

			return sb.ToString();
		}
	}
}
