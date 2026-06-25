using System.Text;

namespace Crawler.Quality
{
	internal static class ContentBeforeDoctype
	{
		/// <summary>
		/// Tier-1 MALFORMED_HTML check (<c>CONTENT_BEFORE_DOCTYPE</c>): flags
		/// non-whitespace content before the document's opening
		/// &lt;!doctype&gt;/&lt;html&gt;/&lt;?xml&gt; token (after an optional leading
		/// UTF-8 BOM), on raw bytes. Composite Word
		/// <c>MALFORMED_HTML:CONTENT_BEFORE_DOCTYPE</c>, log 10, auto-promoted.
		///
		/// Why raw bytes, not the parsed DOM: HtmlAgilityPack is lenient and folds
		/// leading stray content into the tree, so <c>HtmlDocument.ParseErrors</c>
		/// does not surface it. A direct check on the leading bytes is the only
		/// reliable detector. When this fires it is a backend templating /
		/// error-injection bug — always a server-side fix, never editorial, which
		/// is why it is not interactively triaged.
		///
		/// One finding per file: a whole-document property, not per-occurrence.
		/// </summary>
		internal static IEnumerable<QualityIssue> Check(
			string filename, byte[] rawBytes)
		{
			if (rawBytes is null || rawBytes.Length == 0)
			{
				yield break;
			}

			// Skip a single legitimate leading UTF-8 BOM (EF BB BF at offset 0).
			int i = 0;
			if (rawBytes.Length >= 3
				&& rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF)
			{
				i = 3;
			}

			// Skip ASCII whitespace (space, tab, CR, LF) — leading whitespace
			// before the doctype is benign and common.
			while (i < rawBytes.Length)
			{
				byte b = rawBytes[i];
				if (b == 0x20 || b == 0x09 || b == 0x0D || b == 0x0A) { i++; continue; }
				break;
			}

			// All-whitespace (or empty after BOM) file: nothing to flag here.
			// An empty/near-empty body is a different defect class (download
			// robustness — see gotchas) and is not this check's concern.
			if (i >= rawBytes.Length)
			{
				yield break;
			}

			// Decode a bounded window from the first non-whitespace byte so the
			// prefix test sees text, not bytes. The longest token we test for is
			// "<!doctype" (9 chars); a 64-byte window is ample and keeps the
			// excerpt cheap even on a multi-megabyte page.
			const int probeBytes = 64;
			int probeLen = Math.Min(probeBytes, rawBytes.Length - i);

			// NB: a yield-return method may not contain `yield` inside a
			// try-with-catch (CS1626), so decode into a nullable local here and
			// branch after the try rather than yielding from the catch.
			string? lead = null;
			try { lead = Encoding.UTF8.GetString(rawBytes, i, probeLen); }
			catch { /* undecodable lead — not our defect to classify */ }
			if (lead is null)
			{
				yield break;
			}

			// The byte-level skip above already consumed leading ASCII
			// whitespace, so `lead` begins at the first significant byte. Do NOT
			// TrimStart here: a non-ASCII "whitespace" lookalike (e.g. NBSP
			// U+00A0, U+2028) before the doctype is genuine pre-doctype content
			// and must stay flaggable, and trimming would also desync the
			// reported byte offset from the evidence.
			bool startsWell =
				lead.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
				|| lead.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
				|| lead.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);

			if (startsWell)
			{
				yield break;
			}

			// Offending lead: render with operator-friendly invisible markers and
			// cap at the 250-char excerpt limit used for invisible-marker output.
			var evidence = LogExcerpt.Truncate(lead);
			const int maxExcerpt = 250;
			if (evidence.Length > maxExcerpt)
			{
				evidence = evidence[..(maxExcerpt - 1)] + "…";
			}

			yield return new QualityIssue(
				filename,
				"MALFORMED_HTML",
				"CONTENT_BEFORE_DOCTYPE",
				$"offset {i}: {evidence}");
		}
	}
}
