using System.Net;
using HtmlAgilityPack;

namespace Crawler.Quality
{
	internal static class ControlChars
	{
		// Detect control characters / bidi controls / zero-widths
		// in title and meta attribute values. These often originate from
		// CMS editors copy-pasting from other sources (Word, PDFs, web pages).
		// Invisible to the author but they break downstream parsing and can,
		// in the worst case, be used as an injection vector by a malicious
		// page. Distinct from LIGATURE — that's about visible-but-wrong
		// characters; this is about invisible-but-harmful characters.

		/// <summary>
		/// First codepoint of <paramref name="s"/> that is a control / bidi /
		/// zero-width character, or null if none. Returns the codepoint and
		/// a short human-readable name. Used by Check.
		/// </summary>
		internal static (int Codepoint, string Name)? FindFirstControlChar(
			string s, bool includeWhitespaceControls = true)
		{
			if (string.IsNullOrEmpty(s))
			{
				return null;
			}

			foreach (var ch in s)
			{
				var hit = DefectDetectionHelpers.ClassifyInvisible(ch, includeWhitespaceControls);
				if (hit.HasValue)
				{
					return hit;
				}
			}

			return null;
		}

		/// <summary>
		/// Scans &lt;title&gt; text and the content attribute of &lt;meta&gt; tags
		/// for control characters / bidi controls / zero-widths. CMS-pasted
		/// content frequently contains these — invisible to the author but
		/// breaking downstream tooling. One issue per offending element
		/// (not per character) — Detail names the first bad codepoint.
		/// </summary>
		internal static IEnumerable<QualityIssue> Check(
			string filename, HtmlDocument doc, ContentQualityConfig config)
		{
			// Title text content.
			var titleNode = doc.DocumentNode.SelectSingleNode("//title");
			if (titleNode != null)
			{
				var rawTitle = WebUtility.HtmlDecode(titleNode.InnerText);
				var hit = FindFirstControlChar(rawTitle);
				if (hit.HasValue)
				{
					yield return new QualityIssue(
						filename,
						"CONTROL_CHARS_IN_CONTENT",
						$"Found {hit.Value.Name} in <title> text",
						LogExcerpt.Truncate(rawTitle, config.ContentQualityMaxExcerpt));
				}
			}

			// Meta content attributes.
			foreach (var meta in doc.DocumentNode.SelectNodes("//meta[@content]") ?? Enumerable.Empty<HtmlNode>())
			{
				var name = meta.GetAttributeValue("name", "").Trim();
				var content = meta.GetAttributeValue("content", "");
				var decoded = WebUtility.HtmlDecode(content);
				var hit = FindFirstControlChar(decoded);
				if (hit.HasValue)
				{
					yield return new QualityIssue(
						filename,
						"CONTROL_CHARS_IN_CONTENT",
						$"Found {hit.Value.Name} in meta[@name=\"{name}\"] content",
						LogExcerpt.Truncate(decoded, config.ContentQualityMaxExcerpt));
				}
			}

			// Editor-authored prose containers. The architect-class detector
			// (INVISIBLE_CHAR_IN_BODY) deliberately SKIPS these same elements as
			// "editor prose"; scanning them here is the complementary half, so an
			// invisible in author content surfaces in triage instead of falling
			// through the gap between the two detectors. Keyed on a text node's
			// DIRECT parent — the architect detector's exact check — so the two
			// partition body text with no overlap. One finding per offending
			// element, mirroring the title/meta cards above.
			var blockElements = config.ContentQualityBlockElements
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (var el in doc.DocumentNode.Descendants()
				.Where(n => n.NodeType == HtmlNodeType.Element && blockElements.Contains(n.Name)))
			{
				// Direct text children only — text inside a nested inline child
				// (<b>, <span>) has that child as its parent and belongs to the
				// architect-class detector, not here.
				var directText = string.Concat(el.ChildNodes
					.Where(n => n.NodeType == HtmlNodeType.Text)
					.Select(n => n.InnerText));
				var decoded = WebUtility.HtmlDecode(directText);
				var hit = FindFirstControlChar(decoded, includeWhitespaceControls: false);
				if (hit.HasValue)
				{
					yield return new QualityIssue(
						filename,
						"CONTROL_CHARS_IN_CONTENT",
						$"Found {hit.Value.Name} in <{el.Name}> text",
						LogExcerpt.Truncate(decoded, config.ContentQualityMaxExcerpt));
				}
			}
		}
	}
}
