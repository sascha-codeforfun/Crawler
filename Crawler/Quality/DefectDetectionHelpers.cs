using System.Text;
using HtmlAgilityPack;

namespace Crawler.Quality
{
	internal static class DefectDetectionHelpers
	{
		/// <summary>
		/// Builds an inspection-friendly representation of an element's start tag.
		/// Includes the tag name and a curated subset of attributes most useful for
		/// writing an XPath strip — class, id, role, data-component — in that order
		/// of preference. Empty attributes are skipped. Output is capped at
		/// <see cref="ContainerTagMaxLength"/> characters to keep log lines readable
		/// even when class lists are pathological.
		///
		/// Examples:
		///   <c>&lt;div class="caps-lock-warning"&gt;</c>
		///   <c>&lt;section id="related"&gt;</c>
		///   <c>&lt;div&gt;</c>  (no informative attributes)
		/// </summary>
		internal static string FormatContainerStartTag(HtmlNode node)
		{
			var sb = new StringBuilder();
			sb.Append('<').Append(node.Name);

			foreach (var attrName in ContainerTagAttributesOfInterest)
			{
				var value = node.GetAttributeValue(attrName, string.Empty);
				if (string.IsNullOrEmpty(value))
				{
					continue;
				}

				sb.Append(' ').Append(attrName).Append("=\"").Append(value).Append('"');
			}

			sb.Append('>');

			var result = sb.ToString();
			if (result.Length > ContainerTagMaxLength)
			{
				result = result[..(ContainerTagMaxLength - 2)] + "…>";
			}

			return result;
		}

		private static readonly string[] ContainerTagAttributesOfInterest =
			["class", "id", "role", "data-component"];

		private const int ContainerTagMaxLength = 200;

		/// <summary>
		/// True for character codepoints classed as architect-emitted invisibles:
		/// zero-widths, bidi controls, line/paragraph separators, C0/C1 controls,
		/// and BOM/ZWNBSP when it appears outside its leading-byte role. Excludes
		/// CR/LF/TAB which are normal whitespace in HTML source.
		/// </summary>
		internal static bool IsArchitectClassInvisible(char ch)
		{
			if (ch == '\r' || ch == '\n' || ch == '\t')
			{
				return false;
			}

			if (ch < 0x20)
			{
				return true;                              // C0 controls
			}

			if (ch >= 0x80 && ch <= 0x9F)
			{
				return true;               // C1 controls
			}

			if (ch == '\u200B' || ch == '\u200C' || ch == '\u200D')
			{
				return true; // ZWSP/ZWNJ/ZWJ
			}

			if (ch == '\u2060')
			{
				return true;                         // WJ
			}

			if (ch == '\uFEFF')
			{
				return true;                         // ZWNBSP / embedded BOM
			}

			if (ch >= '\u202A' && ch <= '\u202E')
			{
				return true;       // bidi controls
			}

			if (ch >= '\u2066' && ch <= '\u2069')
			{
				return true;       // bidi isolates
			}

			if (ch == '\u2028' || ch == '\u2029')
			{
				return true;       // line/paragraph separators
			}

			return false;
		}
	}
}
