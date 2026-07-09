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
		/// Separator between the human-readable display prose and the stable
		/// identity payload that <see cref="Check"/> packs into a finding's Detail
		/// field. Triage splits on it: the left side is shown to the operator, the
		/// right side (a location token + the full marker-encoded element text)
		/// builds the tracking key. Chosen to be a string that does not occur in
		/// the display prose ("Found &lt;name&gt; in &lt;location&gt;") and that is
		/// safe in the pipe-delimited ledger.
		///
		/// WHY the identity travels in Detail rather than a new field: the finding
		/// record and its on-disk log format are positional (Filename|IssueType|
		/// Detail|Context) and read by several consumers; threading a stable id
		/// through the existing Detail avoids a format change while giving triage
		/// what it needs. The prose remains first so any consumer that does not
		/// split still shows something sensible.
		/// </summary>
		internal const string IdentitySeparator = " \u00A6\u00A6ID\u00A6\u00A6 ";

		/// <summary>
		/// Builds a finding Detail that carries BOTH the human display prose and a
		/// stable identity payload. The identity is:
		///   &lt;locationToken&gt; :: &lt;full marker-encoded element text&gt;
		/// where the element text is encoded UNTRUNCATED and losslessly (see
		/// <see cref="LogExcerpt.EncodeForIdentity"/>), so every distinct element
		/// on a page gets a distinct key and the key is stable across identical
		/// re-crawls. The location token is the element descriptor (e.g. "&lt;li&gt;",
		/// "&lt;title&gt;", "meta[name=description]") — stable and cheap, and for
		/// singular locations (title / a named meta) already unique on the page.
		/// </summary>
		private static string ComposeDetail(string prose, string locationToken, string elementText)
			=> prose + IdentitySeparator + locationToken + " :: " + LogExcerpt.EncodeForIdentity(elementText);

		/// <summary>
		/// Scans &lt;title&gt; text and the content attribute of &lt;meta&gt; tags
		/// for control characters / bidi controls / zero-widths. CMS-pasted
		/// content frequently contains these — invisible to the author but
		/// breaking downstream tooling. One issue per offending element
		/// (not per character). Detail names the first bad codepoint (display)
		/// AND carries a stable identity payload (see <see cref="ComposeDetail"/>)
		/// so each distinct element round-trips against its own tracking key.
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
						ComposeDetail(
							$"Found {hit.Value.Name} in <title> text",
							"<title>",
							rawTitle),
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
					// A named meta is singular per page, so the location token
					// alone (meta[name=<name>]) is already a unique, stable id
					// component — but we still carry the encoded text so the key
					// changes if the meta's invisible content changes.
					yield return new QualityIssue(
						filename,
						"CONTROL_CHARS_IN_CONTENT",
						ComposeDetail(
							$"Found {hit.Value.Name} in meta[@name=\"{name}\"] content",
							$"meta[name={name}]",
							decoded),
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
					// Repeatable block element: location token alone is NOT unique
					// (a page has many <li>/<p>), so the full marker-encoded text
					// is what distinguishes one finding from its siblings. Two
					// list items with different text get different keys; two truly
					// identical elements share a key (nothing stable separates
					// them — surfaced once, occurrence-aware in triage).
					yield return new QualityIssue(
						filename,
						"CONTROL_CHARS_IN_CONTENT",
						ComposeDetail(
							$"Found {hit.Value.Name} in <{el.Name}> text",
							$"<{el.Name}>",
							decoded),
						LogExcerpt.Truncate(decoded, config.ContentQualityMaxExcerpt));
				}
			}
		}
	}
}
