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
		internal static (int Codepoint, string Name)? FindFirstControlChar(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return null;
			}

			foreach (var ch in s)
			{
				if (ch == '\r')
				{
					return (ch, "CR (U+000D)");
				}

				if (ch == '\n')
				{
					return (ch, "LF (U+000A)");
				}

				if (ch == '\t')
				{
					return (ch, "TAB (U+0009)");
				}

				if (ch < 0x20)
				{
					return (ch, $"C0 control (U+{(int)ch:X4})");
				}

				if (ch >= 0x80 && ch <= 0x9F)
				{
					return (ch, $"C1 control (U+{(int)ch:X4})");
				}

				if (ch == '\u200B')
				{
					return (ch, "ZWSP (U+200B)");
				}

				if (ch == '\u200C')
				{
					return (ch, "ZWNJ (U+200C)");
				}

				if (ch == '\u200D')
				{
					return (ch, "ZWJ (U+200D)");
				}

				if (ch == '\uFEFF')
				{
					return (ch, "BOM/ZWNBSP (U+FEFF)");
				}

				if (ch >= '\u202A' && ch <= '\u202E')
				{
					return (ch, $"bidi control (U+{(int)ch:X4})");
				}

				if (ch >= '\u2066' && ch <= '\u2069')
				{
					return (ch, $"bidi isolate (U+{(int)ch:X4})");
				}
				// Unicode line-break characters that .NET ReadLine doesn't split
				// on but text editors and other tooling may render as breaks.
				if (ch == '\u2028')
				{
					return (ch, "LINE SEPARATOR (U+2028)");
				}

				if (ch == '\u2029')
				{
					return (ch, "PARAGRAPH SEPARATOR (U+2029)");
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
			string filename, HtmlDocument doc)
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
						LogExcerpt.Truncate(rawTitle));
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
						LogExcerpt.Truncate(decoded));
				}
			}
		}
	}
}
