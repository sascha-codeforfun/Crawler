namespace Crawler.Suppressions
{
	using System;
	using System.Collections.Generic;
	using Crawler.Quality;

	/// <summary>
	/// Cross-pass dedup. Origin discipline: content-quality (SPLIT_WORD_ANCHOR). Surfaced-in
	/// discipline: spelling. When a CMS author closes an anchor a keystroke early, a whole word
	/// is severed across the tag — "village" authored as "vill&lt;/a&gt;age". The traverser
	/// treats the &lt;/a&gt; as a run boundary, so the HEAD ("vill") lands in its own run and
	/// spelling flags it as a non-word. That spelling "miss" is not a typo; it is the same
	/// physical defect content-quality already reports as SPLIT_WORD_ANCHOR. Reporting it in both
	/// passes is duplicate triage, so the spelling twin is muted and the defect is reported once,
	/// by CQ (which shows the markup — the more actionable home).
	///
	/// This type is the MARKUP half: from each SPLIT_WORD_ANCHOR excerpt it extracts the
	/// (head, severed-tail) pair — the head is the run of letters ending immediately before
	/// &lt;/a&gt;, the tail the letters immediately after. The DICTIONARY half lives at the
	/// spelling check point (RunChecker), where the page's language bundle is live: the flagged
	/// head is muted only if head+tail rejoins to a real word in that bundle. Keeping the dict
	/// check there means it is the exact check the word would normally get.
	///
	/// Keyed by the hashed filename — the stable identity both CQ findings (QualityIssue.Filename)
	/// and the spell pass (FileInput.Filename) hold; the URL is the lossy projection and is not
	/// used as the join key.
	///
	/// === Why the tail is capped at ONE letter (the deliberately-waived risk) ===
	/// Suppression must be surgical: a missed suppression is merely the noise we have today, but
	/// an over-suppression hides a real defect we would never see. So the bar is "only mute when
	/// we are certain this finding IS the CQ defect," and the tail length is the load-bearing
	/// guard. A FALSE suppression would need ALL of:
	///   (1) the in-anchor head is itself a dictionary MISS (else there is no finding to mute);
	///   (2) the tail does not start with whitespace — CQ's own pattern guarantees this, it
	///       matches &lt;/a&gt; immediately abutting text, so a correctly authored
	///       "&lt;/a&gt; word" with a space never produces a tail at all;
	///   (3) the tail is at most ONE letter;
	///   (4) head+tail passes the page dictionary; AND
	///   (5) the SAME string also appears elsewhere on the page as a genuine standalone typo —
	///       the only way muting the word could hide a real error. The suppression is
	///       word-scoped, matching the existing WordCollisionMatcher granularity.
	/// The tail cap descends deliberately. At THREE letters a tail is often itself a real word
	/// (age, son, ant, ear, ape), so a real word can split on a real-word boundary and hide in
	/// plain sight: vill&lt;/a&gt;age, rea&lt;/a&gt;son, eleph&lt;/a&gt;ant, nucl&lt;/a&gt;ear,
	/// esc&lt;/a&gt;ape. At TWO the camouflage persists (in, us, on, be, go, ox):
	/// pengu&lt;/a&gt;in, octop&lt;/a&gt;us, ribb&lt;/a&gt;on, descri&lt;/a&gt;be,
	/// flamin&lt;/a&gt;go, parad&lt;/a&gt;ox. At ONE the tail is a single character, and the
	/// standalone single-letter words are essentially just "a"/"I" — so the "tail is itself a
	/// word" camouflage regime effectively vanishes, and the residual is only the five-way
	/// coincidence above, deemed negligible. English is the stricter test here: German
	/// inflection/compounding offers MORE short-tail glue candidates, so reasoning in English
	/// under-counts nothing. If anyone wants to re-open 1-vs-2-vs-3, this is where it was waived.
	/// </summary>
	public sealed class AnchorSplitSpellSuppression
	{
		public const string AnchorSplitType = "SPLIT_WORD_ANCHOR";

		/// <summary>Longest severed tail we will rejoin. See the type remarks for why this is 1.</summary>
		internal const int MaxTailLength = 1;

		private const string AnchorClose = "</a>";

		private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> EmptyMap =
			new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

		// filename -> head (letters before </a>) -> the severed tails seen for that head
		private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _byFile;

		internal AnchorSplitSpellSuppression(IEnumerable<QualityIssue>? issues)
		{
			_byFile = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
			if (issues == null)
			{
				return;
			}

			foreach (var issue in issues)
			{
				if (issue == null
					|| !string.Equals(issue.IssueType, AnchorSplitType, StringComparison.Ordinal)
					|| string.IsNullOrEmpty(issue.Filename)
					|| string.IsNullOrEmpty(issue.Context))
				{
					continue;
				}

				foreach (var (head, tail) in HeadTails(issue.Context))
				{
					if (!_byFile.TryGetValue(issue.Filename, out var heads))
					{
						heads = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
						_byFile[issue.Filename] = heads;
					}

					if (!heads.TryGetValue(head, out var tails))
					{
						tails = new HashSet<string>(StringComparer.Ordinal);
						heads[head] = tails;
					}

					tails.Add(tail);
				}
			}
		}

		public bool IsEmpty => _byFile.Count == 0;

		/// <summary>
		/// The severed (head → tails) map reported for a file (empty when none / unknown file).
		/// A head may carry more than one tail when the same fragment is severed differently on
		/// the page; the spell side suppresses if ANY rejoin is a valid word.
		/// </summary>
		public IReadOnlyDictionary<string, IReadOnlySet<string>> ForFile(string filename)
		{
			if (filename == null || !_byFile.TryGetValue(filename, out var heads) || heads.Count == 0)
			{
				return EmptyMap;
			}

			var copy = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
			foreach (var (head, tails) in heads)
			{
				copy[head] = tails;
			}

			return copy;
		}

		/// <summary>
		/// Extracts (head, tail) pairs from a SPLIT_WORD_ANCHOR excerpt. For each "&lt;/a&gt;" in
		/// the context: the TAIL is the maximal run of letters immediately after it, accepted only
		/// when that run is exactly <see cref="MaxTailLength"/> letter(s) long (a longer or
		/// zero-length run is outside the gate and skipped — zero meaning whitespace/punctuation
		/// follows, i.e. nothing severed). The HEAD is the maximal run of letters ending
		/// immediately before the "&lt;/a&gt;". A pair is yielded only when both are non-empty.
		/// Letters are tested with char.IsLetter, so German umlauts are included and digits
		/// excluded (a digit tail cannot rejoin to a word).
		/// </summary>
		internal static IEnumerable<(string Head, string Tail)> HeadTails(string context)
		{
			if (string.IsNullOrEmpty(context))
			{
				yield break;
			}

			int search = 0;
			while (true)
			{
				int at = context.IndexOf(AnchorClose, search, StringComparison.Ordinal);
				if (at < 0)
				{
					yield break;
				}

				int afterClose = at + AnchorClose.Length;
				search = afterClose;

				// Tail: letters right after </a>, accepted only at exactly MaxTailLength.
				int tailEnd = afterClose;
				while (tailEnd < context.Length && char.IsLetter(context[tailEnd]))
				{
					tailEnd++;
				}

				if (tailEnd - afterClose != MaxTailLength)
				{
					continue;
				}

				// Head: letters ending right before </a>.
				int headStart = at;
				while (headStart > 0 && char.IsLetter(context[headStart - 1]))
				{
					headStart--;
				}

				if (headStart < at)
				{
					yield return (context[headStart..at], context[afterClose..tailEnd]);
				}
			}
		}
	}
}
