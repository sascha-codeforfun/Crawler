namespace Crawler.Suppressions
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.RegularExpressions;
	using Crawler.Quality;

	/// <summary>
	/// Cross-pass dedup. Origin discipline: content-quality (ADJACENT_ANCHOR). Surfaced-in
	/// discipline: spelling. When a CMS editor emits consecutive anchors that fracture ONE word —
	/// "&lt;a&gt;And&lt;/a&gt;&lt;a&gt;&lt;/a&gt;&lt;a&gt;roid&lt;/a&gt;" (an empty anchor may sit
	/// between) — the traverser ends a text segment at every &lt;a&gt;, so the fragments ("And",
	/// "roid") land in separate runs and surface as stray spelling misses. That is the SAME physical
	/// defect content-quality already reports as ADJACENT_ANCHOR, so the spelling twin is muted and
	/// the defect is reported once, by CQ (which shows the markup — the more actionable home).
	///
	/// This type is the MARKUP half: from each ADJACENT_ANCHOR finding's DETAIL — "[boundaryAt]
	/// „textA“ + „textB“" — it lifts the two adjacent anchor texts and the source-byte boundary
	/// position. Per file it orders the findings by boundary and forms each fracture's two non-empty
	/// fragments into a (left, right) pair: a single finding carrying both texts, or a (textA, "")
	/// finding immediately followed by a ("", textB) finding — the empty-anchor bridge. For both
	/// fragments it records the verbatim source-order join textA+textB. The DICTIONARY half lives at
	/// the spelling check point (RunChecker), where the page's bundle is live: a flagged fragment is
	/// muted only if its recorded join is a real word in that bundle. A bad+bad join is never a word,
	/// so a mispaired fracture cannot mute.
	///
	/// Scope is deliberately TWO non-empty fragments per fracture (optionally with one empty anchor
	/// between) — the observed CMS shape. A deeper split (3+ fragments) does not pair, so its
	/// fragments stay flagged and the ADJACENT_ANCHOR report still fires: the escalation stays
	/// visible rather than being silently absorbed. Keyed by filename — the stable identity both CQ
	/// findings (QualityIssue.Filename) and the spell pass (FileInput.Filename) hold.
	/// </summary>
	public sealed class AdjacentAnchorSpellSuppression
	{
		public const string AdjacentAnchorType = "ADJACENT_ANCHOR";

		// Detail shape from Crawler.Quality.MisplacedAnchors: "[<boundaryAt>] „<textA>“ + „<textB>“"
		// (German low/high quotes U+201E / U+201C). textA/textB are already HTML-decoded and trimmed
		// at the source, so they need no further cleaning here.
		private static readonly Regex DetailPattern = new(
			"^\\[(?<pos>\\d+)\\]\\s*\u201e(?<a>.*?)\u201c\\s*\\+\\s*\u201e(?<b>.*?)\u201c\\s*$",
			RegexOptions.Compiled | RegexOptions.CultureInvariant);

		private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> EmptyMap =
			new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

		// filename -> fragment token -> the verbatim joins (textA+textB) that fragment participates in
		private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _byFile;

		internal AdjacentAnchorSpellSuppression(IEnumerable<QualityIssue>? issues)
		{
			_byFile = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
			if (issues == null)
			{
				return;
			}

			// Gather the (boundary, textA, textB) triples per file FIRST, then pair within each file:
			// a fracture's two fragments can arrive in two separate findings (the empty-anchor bridge),
			// so pairing needs the whole page's findings together in boundary order.
			var perFile = new Dictionary<string, List<(int Pos, string A, string B)>>(StringComparer.Ordinal);
			foreach (var issue in issues)
			{
				if (issue == null
					|| !string.Equals(issue.IssueType, AdjacentAnchorType, StringComparison.Ordinal)
					|| string.IsNullOrEmpty(issue.Filename)
					|| string.IsNullOrEmpty(issue.Detail))
				{
					continue;
				}

				var m = DetailPattern.Match(issue.Detail);
				if (!m.Success || !int.TryParse(m.Groups["pos"].Value, out var pos))
				{
					continue;
				}

				if (!perFile.TryGetValue(issue.Filename, out var list))
				{
					list = new List<(int, string, string)>();
					perFile[issue.Filename] = list;
				}

				list.Add((pos, m.Groups["a"].Value, m.Groups["b"].Value));
			}

			foreach (var (filename, findings) in perFile)
			{
				foreach (var (left, right) in Pairs(findings))
				{
					Record(filename, left, right);
				}
			}
		}

		public bool IsEmpty => _byFile.Count == 0;

		/// <summary>
		/// The fragment → candidate-join map for a file (empty when none / unknown file). A flagged
		/// token is muted by RunChecker when any of its candidate joins is accepted by the live
		/// dictionary; a fragment may carry more than one join when it appears in several fractures.
		/// </summary>
		public IReadOnlyDictionary<string, IReadOnlySet<string>> ForFile(string filename)
		{
			if (filename == null || !_byFile.TryGetValue(filename, out var fragments) || fragments.Count == 0)
			{
				return EmptyMap;
			}

			var copy = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
			foreach (var (fragment, joins) in fragments)
			{
				copy[fragment] = joins;
			}

			return copy;
		}

		/// <summary>
		/// Orders a file's ADJACENT_ANCHOR findings by source boundary and forms each fracture's two
		/// non-empty fragments into a (left, right) pair: a single finding with both texts present, or
		/// a (textA, "") finding immediately followed by a ("", textB) finding — the empty-anchor
		/// bridge. Leading/trailing empties and 3+ fragment chains yield no pair (left to surface).
		/// </summary>
		internal static IEnumerable<(string Left, string Right)> Pairs(List<(int Pos, string A, string B)> findings)
		{
			var ordered = findings.OrderBy(f => f.Pos).ToList();
			int i = 0;
			while (i < ordered.Count)
			{
				var f = ordered[i];
				bool aFull = !string.IsNullOrEmpty(f.A);
				bool bFull = !string.IsNullOrEmpty(f.B);

				if (aFull && bFull)
				{
					yield return (f.A, f.B);
					i++;
					continue;
				}

				if (aFull && !bFull
					&& i + 1 < ordered.Count
					&& string.IsNullOrEmpty(ordered[i + 1].A)
					&& !string.IsNullOrEmpty(ordered[i + 1].B))
				{
					yield return (f.A, ordered[i + 1].B);
					i += 2;
					continue;
				}

				i++;
			}
		}

		private void Record(string filename, string left, string right)
		{
			if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
			{
				return;
			}

			var joined = left + right;
			if (!_byFile.TryGetValue(filename, out var fragments))
			{
				fragments = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
				_byFile[filename] = fragments;
			}

			Add(fragments, left, joined);
			Add(fragments, right, joined);
		}

		private static void Add(Dictionary<string, HashSet<string>> fragments, string key, string joined)
		{
			if (!fragments.TryGetValue(key, out var joins))
			{
				joins = new HashSet<string>(StringComparer.Ordinal);
				fragments[key] = joins;
			}

			joins.Add(joined);
		}
	}
}
