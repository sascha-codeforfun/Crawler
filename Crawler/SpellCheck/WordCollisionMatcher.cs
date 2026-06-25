namespace Crawler.SpellCheck
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text.RegularExpressions;
	using Crawler.Quality;

	/// <summary>
	/// Cross-pass dedup. A WORD_COLLISION reported by content-quality (an inline element abutting
	/// bare text with no separator, merging two words) is the SAME physical defect that spell
	/// independently flags as a non-word — spell's tokenizer glues the two halves across the inline
	/// seam exactly as the rendered page does. Reporting it in both passes is duplicate triage, so
	/// the spell twin is muted and the defect is reported once, by CQ (which shows the markup and
	/// highlights the seam — the more actionable home).
	///
	/// A collision's "seam token" is the merged word the seam produces — the token that exists only
	/// once the inline tag between the halves is removed. It is derived from the CQ excerpt as the
	/// tokens of the DE-TAGGED excerpt that are NOT tokens of the tagged excerpt. That isolates
	/// exactly the merge: a genuine word sharing the excerpt appears in BOTH forms, so it is never a
	/// seam token and a real typo in the same sentence is never muted. Tags are stripped with
	/// NOTHING (not a space) so the two halves glue, reproducing spell's own glued token via the
	/// same <see cref="SpellTokenizer"/>.
	///
	/// Keyed by the hashed filename — the stable identity both CQ findings (QualityIssue.Filename)
	/// and the spell pass (FileInput.Filename) hold; the URL is the lossy projection and is not used
	/// as the join key. Built from the POST-suppression CQ findings, so a collision an operator rule
	/// hid is absent here and its spell twin correctly stays visible.
	/// </summary>
	public sealed partial class WordCollisionMatcher
	{
		public const string WordCollisionType = "WORD_COLLISION";

		private static readonly IReadOnlySet<string> EmptySet =
			new HashSet<string>(StringComparer.Ordinal);

		// filename -> the merged seam tokens reported for that file
		private readonly Dictionary<string, HashSet<string>> _byFile;

		internal WordCollisionMatcher(IEnumerable<QualityIssue>? collisions)
		{
			_byFile = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
			if (collisions == null)
			{
				return;
			}

			foreach (var issue in collisions)
			{
				if (issue == null
					|| !string.Equals(issue.IssueType, WordCollisionType, StringComparison.Ordinal)
					|| string.IsNullOrEmpty(issue.Filename))
				{
					continue;
				}

				var tokens = SeamTokens(issue.Context);
				if (tokens.Count == 0)
				{
					continue;
				}

				if (!_byFile.TryGetValue(issue.Filename, out var set))
				{
					set = new HashSet<string>(StringComparer.Ordinal);
					_byFile[issue.Filename] = set;
				}

				foreach (var t in tokens)
				{
					set.Add(t);
				}
			}
		}

		public bool IsEmpty => _byFile.Count == 0;

		/// <summary>The seam tokens reported for a file (empty set when none / unknown file).</summary>
		public IReadOnlySet<string> WordsForFile(string filename)
			=> filename != null && _byFile.TryGetValue(filename, out var set) ? set : EmptySet;

		/// <summary>
		/// The merged seam token(s) of a collision excerpt: tokens present after DE-TAGGING the
		/// excerpt but absent with tags intact. Tags are removed with the empty string (not a space)
		/// so the two halves glue into the merged word the seam renders.
		/// </summary>
		internal static IReadOnlyCollection<string> SeamTokens(string? excerpt)
		{
			if (string.IsNullOrEmpty(excerpt))
			{
				return Array.Empty<string>();
			}

			var tagged = Words(excerpt);
			var deTagged = Words(TagPattern().Replace(excerpt, string.Empty));
			deTagged.ExceptWith(tagged);
			// A genuine merge is a wordish token; drop stray punctuation / number-only diffs.
			deTagged.RemoveWhere(w => w.Length < 2 || !w.Any(char.IsLetter));
			return deTagged;
		}

		private static HashSet<string> Words(string text)
			=> SpellTokenizer
				.Tokenize(new TextRun(null!, RunSource.TextNode, "collision", text))
				.Select(t => t.Text)
				.ToHashSet(StringComparer.Ordinal);

		[GeneratedRegex("<[^>]*>")]
		private static partial Regex TagPattern();
	}
}
