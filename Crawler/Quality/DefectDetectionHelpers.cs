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
		/// Single source of truth for the invisible-character set shared by the
		/// editor-class detector (ControlChars, surfaced in triage) and the
		/// architect-class detector (INVISIBLE_CHAR_IN_BODY, in the CMS-defect CSV).
		/// Returns the codepoint and an operator-readable name, or null for visible
		/// content. CR/LF/TAB are anomalous in title/meta attribute values but
		/// ordinary insignificant whitespace in body prose and markup, so the caller
		/// passes includeWhitespaceControls = true only for the former; everything
		/// below is unconditional. Names match the editor detector's historical
		/// labels (its cards and tests are unaffected); the architect detector keeps
		/// its own naming and consults this only for membership.
		/// </summary>
		internal static (int Codepoint, string Name)? ClassifyInvisible(
			char ch, bool includeWhitespaceControls)
		{
			if (ch == '\r' || ch == '\n' || ch == '\t')
			{
				if (!includeWhitespaceControls)
				{
					return null;
				}

				if (ch == '\r')
				{
					return (ch, "CR (U+000D)");
				}

				if (ch == '\n')
				{
					return (ch, "LF (U+000A)");
				}

				return (ch, "TAB (U+0009)");
			}

			if (ch < 0x20)
			{
				return (ch, $"C0 control (U+{(int)ch:X4})");
			}

			if (ch == '\u007F')
			{
				return (ch, "DEL (U+007F)");
			}

			if (ch >= 0x80 && ch <= 0x9F)
			{
				return (ch, $"C1 control (U+{(int)ch:X4})");
			}

			if (ch == '\u00AD')
			{
				return (ch, "SHY (U+00AD)");
			}

			if (ch == '\u061C')
			{
				return (ch, "ALM (U+061C)");
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

			if (ch == '\u200E')
			{
				return (ch, "LRM (U+200E)");
			}

			if (ch == '\u200F')
			{
				return (ch, "RLM (U+200F)");
			}

			if (ch >= '\u202A' && ch <= '\u202E')
			{
				return (ch, $"bidi control (U+{(int)ch:X4})");
			}

			if (ch == '\u2060')
			{
				return (ch, "WJ (U+2060)");
			}

			if (ch >= '\u2061' && ch <= '\u2064')
			{
				return (ch, $"invisible math (U+{(int)ch:X4})");
			}

			if (ch >= '\u2066' && ch <= '\u2069')
			{
				return (ch, $"bidi isolate (U+{(int)ch:X4})");
			}

			if (ch == '\u2028')
			{
				return (ch, "LINE SEPARATOR (U+2028)");
			}

			if (ch == '\u2029')
			{
				return (ch, "PARAGRAPH SEPARATOR (U+2029)");
			}

			if (ch == '\uFEFF')
			{
				return (ch, "BOM/ZWNBSP (U+FEFF)");
			}

			return null;
		}

		/// <summary>
		/// Architect-class membership: invisibles that matter in non-prose body and
		/// template markup. Excludes CR/LF/TAB (ordinary HTML-source whitespace).
		/// Delegates to <see cref="ClassifyInvisible"/>.
		/// </summary>
		internal static bool IsArchitectClassInvisible(char ch)
			=> ClassifyInvisible(ch, includeWhitespaceControls: false).HasValue;
	}
}
