using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using HtmlAgilityPack;

namespace Crawler.Quality
{
	// Detects text stored in a decomposed Unicode form (NFD) in editor-authored
	// body prose — a base letter plus a separate combining mark (e.g. "ä" as
	// a + U+0308) rather than the precomposed character (U+00E4). It renders
	// identically but is byte-fragile: any consumer that compares bytes without
	// normalising to NFC (site/app search, autosuggest, AI tokenisers, partner
	// feeds) silently fails to match it. See ContentQualityConfig.CheckDecomposition.
	//
	// Two findings, governed by the lever:
	//   DECOMPOSED_TEXT_IN_CONTENT   (FlagAll)            — any composable NFD present.
	//   MIXED_NORMALIZATION_IN_CONTENT (FlagMixed, FlagAll) — the same word appears in
	//       two encodings on one page (provably breaks in-page search and dedup; a
	//       defect regardless of any opinion on decomposed text in general).
	//
	// Scans the same ContentQualityBlockElements as ControlChars, keyed on a text
	// node's DIRECT parent. The composability test (NFC actually shortens the
	// base+mark) self-exempts scripts whose marks have no precomposed form
	// (Arabic, Hebrew, most Indic) — they are decomposed by nature, not by defect.
	internal static class Decomposition
	{
		internal static IEnumerable<QualityIssue> Check(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			var mode = config.ResolvedCheckDecomposition;
			if (mode == DecompositionMode.Off)
			{
				yield break;
			}

			var blockElements = config.ContentQualityBlockElements
				.ToHashSet(System.StringComparer.OrdinalIgnoreCase);

			// NFC-key -> distinct raw word forms seen across the WHOLE page. A key
			// holding more than one raw form is a word that appears in two encodings
			// on this page — the mixed-normalisation case.
			var formsByKey = new Dictionary<string, HashSet<string>>(System.StringComparer.Ordinal);

			foreach (var el in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Element && blockElements.Contains(n.Name)))
			{
				// Direct text children only — text inside a nested inline child has
				// that child as its parent, mirroring the ControlChars partition.
				var directText = string.Concat(el.ChildNodes
					.Where(n => n.NodeType == HtmlNodeType.Text)
					.Select(n => n.InnerText));
				var decoded = WebUtility.HtmlDecode(directText);
				if (decoded.Length == 0)
				{
					continue;
				}

				if (mode == DecompositionMode.FlagAll)
				{
					var hits = FindAllDecompositions(decoded);
					if (hits.Count > 0)
					{
						var named = string.Join(", ", hits.Select(h => $"'{h.Composed}' ({h.BaseChar} + U+{h.Mark:X4})"));
						yield return new QualityIssue(
							filename,
							"DECOMPOSED_TEXT_IN_CONTENT",
							$"Found decomposed {named} in <{el.Name}> text",
							LogExcerpt.Truncate(decoded, config.ContentQualityMaxExcerpt));
					}
				}

				foreach (var word in WordTokens(decoded))
				{
					var key = word.Normalize(NormalizationForm.FormC);
					if (!formsByKey.TryGetValue(key, out var set))
					{
						set = new HashSet<string>(System.StringComparer.Ordinal);
						formsByKey[key] = set;
					}

					set.Add(word);
				}
			}

			// Mixed: any word whose page-wide raw-form set holds more than one entry.
			// They share an NFC key but differ byte-for-byte, so at least one is
			// decomposed — a genuine same-word encoding split on one page.
			foreach (var kv in formsByKey.Where(kv => kv.Value.Count > 1))
			{
				var forms = string.Join("  |  ", kv.Value.Select(AnnotateForm));
				yield return new QualityIssue(
					filename,
					"MIXED_NORMALIZATION_IN_CONTENT",
					$"Word '{kv.Key}' appears in {kv.Value.Count} encodings on one page (same word, different byte forms)",
					forms);
			}
		}

		// First base+combining-mark pair that NFC would compose (i.e. a precomposed
		// form exists). Returns null when there is no composable decomposition —
		// already-composed text, or a script whose marks have no precomposed form
		// (where base+mark does not shorten under NFC, so it is exempt by design).
		internal static (string Composed, char BaseChar, int Mark)? FindFirstDecomposition(string text)
		{
			for (int i = 1; i < text.Length; i++)
			{
				if (!IsCombiningMark(text[i]))
				{
					continue;
				}

				var pair = text.Substring(i - 1, 2); // base + mark
				var composed = pair.Normalize(NormalizationForm.FormC);
				if (composed.Length < pair.Length)
				{
					return (composed, text[i - 1], text[i]);
				}
			}

			return null;
		}

		// Every DISTINCT composable decomposition in the text, in first-seen order.
		// Same composability test as FindFirstDecomposition (NFC shortens base+mark),
		// so non-composable scripts stay exempt. Deduped by composed grapheme so an
		// element repeating 'ü' lists it once — the presence finding names all of
		// them, not just the first (which would mask, e.g., 'ö' sitting behind 'ü').
		internal static IReadOnlyList<(string Composed, char BaseChar, int Mark)> FindAllDecompositions(string text)
		{
			var result = new List<(string Composed, char BaseChar, int Mark)>();
			var seen = new HashSet<string>(System.StringComparer.Ordinal);
			for (int i = 1; i < text.Length; i++)
			{
				// Skip when the base is itself a mark (stacked marks) so spans never overlap.
				if (!IsCombiningMark(text[i]) || IsCombiningMark(text[i - 1]))
				{
					continue;
				}

				var pair = text.Substring(i - 1, 2);
				var composed = pair.Normalize(NormalizationForm.FormC);
				if (composed.Length < pair.Length && seen.Add(composed))
				{
					result.Add((composed, text[i - 1], text[i]));
				}
			}

			return result;
		}

		// Word tokens for mixed-encoding detection: maximal runs of letters and
		// combining marks. Deliberately simple (no language tokenizer) — we only
		// need to compare a word's encodings, never to spell-check it.
		private static IEnumerable<string> WordTokens(string text)
		{
			int start = -1;
			for (int i = 0; i < text.Length; i++)
			{
				if (IsWordChar(text[i]))
				{
					if (start < 0)
					{
						start = i;
					}
				}
				else if (start >= 0)
				{
					yield return text.Substring(start, i - start);
					start = -1;
				}
			}

			if (start >= 0)
			{
				yield return text.Substring(start);
			}
		}

		// Renders a word with combining marks exposed as [U+XXXX] so an operator can
		// see the byte difference between two encodings that render identically.
		private static string AnnotateForm(string word)
		{
			var sb = new StringBuilder(word.Length + 8);
			foreach (var c in word)
			{
				if (IsCombiningMark(c))
				{
					sb.Append("[U+").Append(((int)c).ToString("X4")).Append(']');
				}
				else
				{
					sb.Append(c);
				}
			}

			return sb.ToString();
		}

		private static bool IsCombiningMark(char c)
		{
			var cat = CharUnicodeInfo.GetUnicodeCategory(c);
			return cat == UnicodeCategory.NonSpacingMark
				|| cat == UnicodeCategory.SpacingCombiningMark
				|| cat == UnicodeCategory.EnclosingMark;
		}

		private static bool IsWordChar(char c)
		{
			return char.IsLetter(c) || IsCombiningMark(c);
		}
	}
}
