using System.Net;
using HtmlAgilityPack;

namespace Crawler.Quality
{
	internal static class DefectBareText
	{
		/// <summary>
		/// Flags text nodes that are direct children of container elements
		/// (div, section, article etc.) without being wrapped in a block element.
		/// This is an HTML authoring defect — bare text in containers causes
		/// rendering inconsistencies and accessibility issues.
		/// </summary>
		internal static IEnumerable<QualityIssue> CheckBareText(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			var containers = config.ContentQualityContainerElements
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			foreach (var node in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Element
					&& containers.Contains(n.Name)))
			{
				foreach (var child in node.ChildNodes)
				{
					if (child.NodeType != HtmlNodeType.Text)
					{
						continue;
					}

					var text = WebUtility.HtmlDecode(child.InnerText).Trim();
					if (string.IsNullOrEmpty(text))
					{
						continue;
					}

					// Suppress BARE_TEXT when the post-trim text contains
					// no visible content — i.e. every remaining character is
					// either whitespace or an architect-class invisible (ZWSPs,
					// ZWNJs, BOMs, C0/C1 controls, bidi marks, etc.). Trim()
					// removes leading/trailing whitespace but not interior
					// whitespace between invisibles (e.g. "   \u200B  \u200C"
					// trims to "\u200B  \u200C" with interior spaces intact),
					// and Trim() never removes the invisibles themselves. So a
					// text node containing only invisibles plus filler
					// whitespace previously fired both BARE_TEXT_IN_CONTAINER
					// and INVISIBLE_CHAR_IN_BODY on the same content.
					// INVISIBLE_CHAR names the actual problem (the embedded
					// codepoint) and is the more useful of the two findings;
					// BARE_TEXT here adds noise without new information. Pure
					// whitespace was already excluded by the IsNullOrEmpty
					// check above (Trim of all-whitespace = empty).
					bool noVisibleContent = true;
					for (int i = 0; i < text.Length; i++)
					{
						var ch = text[i];
						if (char.IsWhiteSpace(ch) || DefectDetectionHelpers.IsArchitectClassInvisible(ch))
						{
							continue;
						}

						noVisibleContent = false;
						break;
					}
					if (noVisibleContent)
					{
						continue;
					}

					var textExcerpt = text.Length > config.ContentQualityExcerptRadius
						? text[..config.ContentQualityExcerptRadius] + "…"
						: text;

					// Prepend the container's start tag so the operator can identify
					// which XPath strip to write without having to open the source HTML
					// and grep for the text. Format: [<div class="...">] excerpt-text
					// Without this context, repeating chrome (e.g. caps-lock warnings,
					// related-link headers) that fires on hundreds of pages requires
					// per-text manual lookup.
					var excerpt = $"[{DefectDetectionHelpers.FormatContainerStartTag(node)}] {textExcerpt}";

					yield return new QualityIssue(
						filename,
						"BARE_TEXT_IN_CONTAINER",
						$"Text directly inside <{node.Name}> without block wrapper",
						excerpt);
				}
			}
		}
	}
}
