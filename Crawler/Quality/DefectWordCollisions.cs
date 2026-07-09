using System.Net;
using HtmlAgilityPack;

namespace Crawler.Quality
{
	internal static class DefectWordCollisions
	{
		/// <summary>
		/// Inline phrasing elements that add no implicit whitespace at their boundary —
		/// the same set the spell harvester glues across (see DomTraverser.InlinePhrasingGlue).
		/// Text on either side of such a boundary touches with no separator, exactly as the
		/// browser renders it.
		/// </summary>
		private static readonly HashSet<string> InlinePhrasingElements =
			new(StringComparer.OrdinalIgnoreCase)
			{
				"b", "i", "em", "strong", "mark", "small", "u", "s", "span", "wbr",
			};

		/// <summary>
		/// Flags word collisions where an inline phrasing element (e.g. a CMS editor's
		/// <c>&lt;span class="h2"&gt;</c> used to fake a heading size) abuts bare sibling
		/// text with no whitespace at the seam, so two words merge into one
		/// (e.g. <c>&lt;span&gt;Basismodul&lt;/span&gt;Inhalte</c> → "BasismodulInhalte").
		/// CSS (display:block) often hides the visual mash, so this is invisible to visual
		/// QA — but the DOM text, the accessibility tree, and the spell harvester all see
		/// the merged token. The high-precision signal is a lowercase→Uppercase transition
		/// straddling the seam: natural words carry no internal capital, whereas a true
		/// mid-word emphasis (<c>&lt;b&gt;bezah&lt;/b&gt;len</c> → "bezahlen") is
		/// lowercase→lowercase and is correctly left alone.
		/// </summary>
		internal static IEnumerable<QualityIssue> CheckWordCollisions(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			// Document-order rank of the inline-phrasing node, prepended to each
			// finding's Detail as a "[N]" prefix. The log freezes findings in
			// ConcurrentBag/LIFO order and WriteLog sorts only by (Filename,
			// IssueType), so equal-key findings land in arbitrary (observed-LIFO)
			// order; this rank lets BuildGroups recover page order so the operator
			// reads 1-2-3, not 3-2-1. Deterministic across runs because the
			// simplified file is frozen and Descendants() is document-ordered.
			// Mirrors the ADJACENT_ANCHOR "[boundaryAt]" prefix pattern.
			// Run-local ordinal only - NOT a raw-HTML coordinate.
			int rank = -1;
			foreach (var node in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Element
					&& InlinePhrasingElements.Contains(n.Name)))
			{
				rank++;
				var inner = WebUtility.HtmlDecode(node.InnerText);
				if (string.IsNullOrEmpty(inner))
				{
					continue;
				}

				// Trailing seam: <span>…Basismodul</span>Inhalte…
				// left char = inner's last char; right char = next text sibling's first char.
				if (node.NextSibling is { NodeType: HtmlNodeType.Text } nextText)
				{
					var right = WebUtility.HtmlDecode(nextText.InnerText);
					if (right.Length > 0
						&& IsLowerUpperSeam(inner[^1], right[0]))
					{
						// Context is the RAW html around the seam (e.g.
						// "<span class=\"h2\">Basismodul</span>Inhalte des Moduls…"),
						// so triage can show actual markup and highlight WORD1/</tag>/WORD2.
						yield return new QualityIssue(
							filename,
							"WORD_COLLISION",
							$"[{rank}] Inline <{node.Name}> abuts bare text without separator — words merge",
							node.OuterHtml + CapExcerpt(nextText.InnerText, config));
						continue;   // one finding per element; don't also test the leading seam
					}
				}

				// Leading seam: …Inhalte<span>Basismodul</span>
				// left char = previous text sibling's last char; right char = inner's first char.
				if (node.PreviousSibling is { NodeType: HtmlNodeType.Text } prevText)
				{
					var left = WebUtility.HtmlDecode(prevText.InnerText);
					if (left.Length > 0
						&& IsLowerUpperSeam(left[^1], inner[0]))
					{
						yield return new QualityIssue(
							filename,
							"WORD_COLLISION",
							$"[{rank}] Bare text abuts inline <{node.Name}> without separator — words merge",
							CapExcerptEnd(prevText.InnerText, config) + node.OuterHtml);
					}
				}
			}
		}

		/// <summary>
		/// True when the seam straddles a lowercase letter immediately followed by an
		/// uppercase letter — the high-precision word-collision signal. Whitespace on
		/// either side breaks the seam (the characters compared are the literal adjacent
		/// ones, so a leading/trailing space yields a non-letter and returns false).
		/// </summary>
		private static bool IsLowerUpperSeam(char left, char right)
			=> char.IsLower(left) && char.IsUpper(right);

		/// <summary>Caps to the leading <c>ContentQualityExcerptRadius</c> chars (head kept).</summary>
		private static string CapExcerpt(string text, ContentQualityConfig config)
			=> text.Length > config.ContentQualityExcerptRadius
				? text[..config.ContentQualityExcerptRadius] + "…"
				: text;

		/// <summary>Caps to the trailing <c>ContentQualityExcerptRadius</c> chars (tail kept) — for the leading-seam fragment.</summary>
		private static string CapExcerptEnd(string text, ContentQualityConfig config)
			=> text.Length > config.ContentQualityExcerptRadius
				? "…" + text[^config.ContentQualityExcerptRadius..]
				: text;
	}
}
