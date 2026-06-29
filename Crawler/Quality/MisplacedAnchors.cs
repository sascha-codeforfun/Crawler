using System.Net;
using HtmlAgilityPack;

namespace Crawler.Quality
{
	internal static class MisplacedAnchors
	{
		/// <summary>
		/// Detects structurally malformed anchor tags in raw downloaded HTML.
		/// Two defect types are reported independently:
		///
		///   MISPLACED_ANCHOR_EMPTY — an anchor whose visible text is absent.
		///     Anchors containing only whitespace or only child elements that
		///     themselves carry no text are treated as empty — InnerText of all
		///     descendants is checked, not just the immediate text node.
		///
		///   ADJACENT_ANCHOR (renamed from MISPLACED_ANCHOR_SPLIT) —
		///     two consecutive sibling anchor tags with no whitespace-text node
		///     between them, AND the rendered excerpt contains the literal
		///     "&lt;/a&gt;&lt;a" (a string-evidence post-filter on HAP's DOM
		///     verdict). Adjacency alone is a structural fact, not a verdict;
		///     OFF by default (AnchorDetection.DetectAdjacent), opted in per
		///     site. The previous name's "SPLIT" overlapped misleadingly with
		///     SPLIT_WORD_ANCHOR — the new name names what is actually checked.
		///
		/// Runs on the already-parsed simplified HTML document — navigation chrome,
		/// scripts, and framework noise are stripped before this check runs, eliminating
		/// false positives from icon anchors, hidden UI controls, and CMS scaffolding.
		/// The caller passes the already-parsed <paramref name="doc"/> to avoid a second
		/// parse of the same file.
		/// </summary>
		internal static IEnumerable<QualityIssue> Check(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			foreach (var anchor in doc.DocumentNode.Descendants("a").ToList())
			{
				// ── MISPLACED_ANCHOR_EMPTY ────────────────────────────────────────────
				// Treat as empty when InnerText (all descendant text collapsed) is blank.
				// Covers: no children, whitespace-only text, empty inline elements.
				// Anchors wrapping an <img> are intentional image links — not flagged.
				var visibleText = WebUtility.HtmlDecode(anchor.InnerText).Trim();
				if (string.IsNullOrEmpty(visibleText)
					&& !anchor.Descendants("img").Any())
				{
					var hrefAttr = anchor.GetAttributeValue("href", string.Empty);
					var detail = string.IsNullOrEmpty(hrefAttr)
						? "No href"
						: $"href: {hrefAttr}";
					yield return new QualityIssue(
						filename,
						"MISPLACED_ANCHOR_EMPTY",
						detail,
						Excerpt.Centred(
							anchor.ParentNode?.OuterHtml ?? anchor.OuterHtml,
							anchor.OuterHtml,
							config.ContentQualityMaxExcerpt));
				}

				// ── ADJACENT_ANCHOR (was MISPLACED_ANCHOR_SPLIT) ────────────────
				// Flag when the immediately following sibling is also an anchor with no
				// whitespace-text node between them. Check current anchor against its next
				// sibling only — avoids double-reporting the same adjacent pair.
				//
				// Gated on AnchorDetection.DetectAdjacent (default false) because adjacency
				// alone is a structural fact, not a defect verdict — many sites use
				// adjacent <a><a> intentionally (CSS-spaced button rows, JS-trigger
				// widgets, dense nav). Operator opts in per site.
				if (!config.AnchorDetection.DetectAdjacent)
				{
					continue;
				}

				var next = anchor.NextSibling;

				// Skip comment nodes — invisible and not a real separator.
				while (next is { NodeType: HtmlNodeType.Comment })
				{
					next = next.NextSibling;
				}

				if (next is not { NodeType: HtmlNodeType.Element } || next.Name != "a")
				{
					continue;
				}

				// Walk siblings between anchor and next to find any whitespace text node.
				var between = anchor.NextSibling;
				var hasSeparator = false;
				while (between != null && between != next)
				{
					if (between.NodeType == HtmlNodeType.Text
						&& between.InnerText.Any(char.IsWhiteSpace))
					{
						hasSeparator = true;
						break;
					}
					between = between.NextSibling;
				}

				if (!hasSeparator)
				{
					var textA = WebUtility.HtmlDecode(anchor.InnerText).Trim();
					var textB = WebUtility.HtmlDecode(next.InnerText).Trim();

					// Centre the excerpt on the </a><a split BOUNDARY, not on the first
					// anchor's OuterHtml start (which sits before a potentially long SVG
					// body, pushing the actual split out of the window). The
					// boundary is where anchor.OuterHtml ends within the source. Fall back
					// to centring on the source midpoint if the anchor markup can't be
					// located (defensive — OuterHtml should always be present in source).
					var source = anchor.ParentNode?.OuterHtml ?? $"{anchor.OuterHtml}{next.OuterHtml}";
					var anchorAt = source.IndexOf(anchor.OuterHtml, StringComparison.Ordinal);
					var boundaryAt = anchorAt >= 0 ? anchorAt + anchor.OuterHtml.Length : source.Length / 2;

					var excerpt = Excerpt.Centred(source, boundaryAt, config.ContentQualityMaxExcerpt);

					// [KEEP] String-evidence post-filter: require the literal
					// "</a><a" to appear in the rendered excerpt before firing.
					// HAP's DOM-level adjacency verdict is what flagged this pair, but
					// HAP may normalize source whitespace during parsing/serialization,
					// so a DOM "adjacent" pair occasionally lacks literal source-level
					// adjacency. Operator decision: drop those edge cases rather
					// than ship a finding the operator cannot confirm by looking at the
					// shown excerpt. Case-insensitive to match the existing anchor-tag
					// highlighter's stance. Do NOT remove this gate — it is the honest
					// bridge between HAP's verdict and the source-bytes claim.
					if (excerpt.IndexOf("</a><a", StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					yield return new QualityIssue(
						filename,
						"ADJACENT_ANCHOR",
						// Prepend [boundaryAt] — the source-byte position
						// of this collision's "</a><a" boundary in the parent's
						// OuterHtml. Persists structural position in the log
						// (4-column shape unchanged) so BuildGroups can sort
						// multiple findings within one page-cluster reliably,
						// independent of ConcurrentBag emission order (which is
						// undefined and observably LIFO under current .NET).
						// Display layer renders this as sequential [01]/[02]
						// for clusters of 2+; bracket stays raw in the log so
						// the position info is intact across crawls. Stripping
						// is a trivial regex if the raw value is unwanted in
						// downstream tools (Excel: replace `\[\d+\] ` with "").
						$"[{boundaryAt}] \u201e{textA}\u201c + \u201e{textB}\u201c",
						excerpt);
				}
			}
		}
	}
}
