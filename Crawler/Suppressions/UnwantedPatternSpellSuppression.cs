namespace Crawler.Suppressions
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using Crawler.Quality;
	using Crawler.SpellCheck;

	/// <summary>
	/// Cross-pass dedup. Origin discipline: content-quality (UNWANTED_PATTERN). Surfaced-in
	/// discipline: spelling. A configured unwanted pattern that is a structural DELIMITER — a
	/// leaked CMS placeholder envelope such as "{{order.142.line_total}}" — drags its inner
	/// identifiers (order, line_total, …) into the rendered text, where spelling flags them as
	/// non-words.
	/// That spelling "miss" is not a typo; it is the same physical defect content-quality already
	/// reports as UNWANTED_PATTERN. Reporting it in both passes is duplicate triage, so the
	/// spelling twin is muted and the defect is reported once, by CQ.
	///
	/// This type is the MARKUP half: from each UNWANTED_PATTERN excerpt it isolates the
	/// delimiter's unbroken run and collects the word tokens inside it. The spelling guard
	/// (RunChecker) then mutes any finding whose token is in this file's set. There is NO
	/// dictionary check — a token sitting inside a configured delimiter run is junk by
	/// construction, never prose, so it is suppressed directly.
	///
	/// Keyed by the hashed filename — the stable identity both CQ findings (QualityIssue.Filename)
	/// and the spell pass (FileInput.Filename) hold.
	///
	/// Built from the page's CQ findings (the gate: only pages CQ flagged are considered) and the
	/// configured pattern strings (the anchors). Word-scoped per file, matching how triage
	/// presents findings (one card per page+word) and consistent with the other suppression
	/// tenants.
	/// </summary>
	public sealed class UnwantedPatternSpellSuppression
	{
		public const string UnwantedPatternType = "UNWANTED_PATTERN";

		// [KEEP] The two character sets below are the load-bearing invariant of this tenant, and
		// they look arbitrary until you know WHY they differ — do not collapse them into one list.
		// This was an expensive design; the reasoning is not reconstructable from the code alone.
		//
		// There are TWO knobs, deliberately overlapping but NOT identical:
		//   • QUALIFIER set — what counts as a "special char" when deciding whether a configured
		//     pattern may ANCHOR a run at all. A pattern must contain >= 2 special chars to qualify.
		//     This restricts anchoring to delimiter-shaped patterns that are self-evidently
		//     non-prose. Common CMS / editor envelope forms all qualify, e.g.:
		//       ((param))   @(param)@   @{param}@   [[param]]   [%param%]
		//       [#param#]   [@param@]   {{param}}   {%param%}   {#param#}
		//       {|param|}   #[param]#   %(param)%   %{param}%   ~{param}~
		//     (A form whose opener itself contains a run-ender — e.g. <%param%> — qualifies but
		//     self-terminates on its own '<' and captures nothing; that is why it is not listed.)
		//     A bare word ("legacyword") has 0 specials and a single-delimiter pattern has 1, so
		//     NEITHER anchors — and that is intentional: those are left for spelling to surface on
		//     its own (the backstop). Either the word is in the dictionary (no finding, nothing to
		//     suppress) or it is not (and then the spell finding is the useful signal we must not
		//     throw away).
		//   • RUN-ENDER set — what STOPS the run that an anchor starts. The run is the anchor plus
		//     everything after it until the first run-ender.
		//
		// Why this is safe: a real word cannot sit INSIDE the run, because separating it from the
		// delimiter needs whitespace, and whitespace ends the run. So the span can only ever contain
		// the contiguous junk run, never neighbouring prose. The run-ender set is belt-and-suspenders
		// on top of that: '<' and '>' stop the run from swallowing a monolith of HTML when a closer
		// is missing.
		//
		// Why '-' and ':' are special-cased — they are excluded from QUALIFIER and added to
		// RUN-ENDER:
		//   • '-' appears in legitimate prose (German hyphenated compounds:
		//     "Hinterachsen-Differential-Sperre", names, ranges). Counting it as "special" would let
		//     a hyphen-built pattern anchor and run across a real multi-word compound; letting it
		//     continue a run would let the span
		//     swallow that compound. Both directions are over-suppression, so '-' both fails to
		//     qualify AND terminates a run.
		//   • ':' appears in legitimate structured tokens ("std::vector", CSS "::before",
		//     namespaced keys). Same reasoning: excluded from qualifying, and ends a run.
		//
		// The safe direction for BOTH lists is "add when in doubt": every addition to the qualifier
		// exclusions makes fewer patterns anchor, and every addition to the run-enders makes runs
		// shorter — both can only ever make suppression NARROWER, never wider. Adding the next
		// '::'-like character is therefore a one-line change plus a test, not a redesign.
		private const int MinSpecialChars = 2;

		// A "special char" for qualifying an anchor: NOT a letter, NOT a digit, and NOT '-'/':'
		// (see the [KEEP] note above for why '-'/':' are excluded).
		private static bool IsSpecial(char c) =>
			!char.IsLetterOrDigit(c) && c != '-' && c != ':';

		// A run-ender: whitespace, the HTML tag edges '<'/'>', and '-'/':' (see [KEEP]).
		private static bool IsRunEnder(char c) =>
			char.IsWhiteSpace(c) || c == '<' || c == '>' || c == '-' || c == ':';

		private static readonly IReadOnlySet<string> EmptySet =
			new HashSet<string>(StringComparer.Ordinal);

		// filename -> the word tokens that sit inside a qualified delimiter run on that page
		private readonly Dictionary<string, HashSet<string>> _byFile;

		internal UnwantedPatternSpellSuppression(
			IEnumerable<QualityIssue>? issues,
			IEnumerable<string>? configuredPatterns)
		{
			_byFile = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

			// Anchors = configured patterns that qualify as delimiters (>= 2 special chars).
			var anchors = (configuredPatterns ?? Enumerable.Empty<string>())
				.Where(p => !string.IsNullOrEmpty(p) && p.Count(IsSpecial) >= MinSpecialChars)
				.Distinct(StringComparer.Ordinal)
				.ToList();

			if (anchors.Count == 0 || issues == null)
			{
				return;
			}

			foreach (var issue in issues)
			{
				if (issue == null
					|| !string.Equals(issue.IssueType, UnwantedPatternType, StringComparison.Ordinal)
					|| string.IsNullOrEmpty(issue.Filename)
					|| string.IsNullOrEmpty(issue.Context))
				{
					continue;
				}

				foreach (var word in WordsInDelimiterRuns(issue.Context, anchors))
				{
					if (!_byFile.TryGetValue(issue.Filename, out var set))
					{
						set = new HashSet<string>(StringComparer.Ordinal);
						_byFile[issue.Filename] = set;
					}

					set.Add(word);
				}
			}
		}

		public bool IsEmpty => _byFile.Count == 0;

		/// <summary>The words inside delimiter runs for a file (empty set for none / unknown file).</summary>
		public IReadOnlySet<string> WordsForFile(string filename)
			=> filename != null && _byFile.TryGetValue(filename, out var set) ? set : EmptySet;

		/// <summary>
		/// For each occurrence of any anchor in <paramref name="excerpt"/>, takes the run from the
		/// anchor position extending while the char is not a run-ender, tokenises that run with the
		/// same <see cref="SpellTokenizer"/> the spell pass uses, and yields each letter-bearing
		/// token. These are the tokens a flagged finding could match.
		/// </summary>
		internal static IEnumerable<string> WordsInDelimiterRuns(string excerpt, IReadOnlyList<string> anchors)
		{
			foreach (var anchor in anchors)
			{
				int from = 0;
				while (true)
				{
					int at = excerpt.IndexOf(anchor, from, StringComparison.Ordinal);
					if (at < 0)
					{
						break;
					}

					int end = at;
					while (end < excerpt.Length && !IsRunEnder(excerpt[end]))
					{
						end++;
					}

					foreach (var token in RunTokens(excerpt[at..end]))
					{
						yield return token;
					}

					from = at + anchor.Length;
				}
			}
		}

		private static IEnumerable<string> RunTokens(string run) =>
			SpellTokenizer
				.Tokenize(new TextRun(null!, RunSource.TextNode, "unwanted", run))
				.Select(t => t.Text)
				.Where(w => w.Any(char.IsLetter));
	}
}
