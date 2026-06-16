using System.Text;
using System.Text.RegularExpressions;

namespace Crawler
{
	// ── PdfQualityAnalyzer ────────────────────────────────────────────────────
	//
	// Checks all PDF files in the download directory for metadata quality.
	// Uses pure .NET — no third-party PDF library required.
	//
	// Output: 17-pdf-quality.log — one row per PDF:
	//   PageUrl@@@Title@@@Description@@@Keywords@@@Language@@@Tags@@@PdfA
	//   @@@StructTree@@@RoleMap@@@Outlines@@@AltText@@@FormFields@@@PdfUA
	//
	//   Text fields : actual value or "n/a" if missing.
	//   Flag fields : 1 = present, -1 = absent. 0 reserved for unknown/unparseable.
	//   PdfUA is always the last column.
	//
	// Output: 18-pdf-remediation.log — one row per gap per PDF, priority-ordered:
	//   PageUrl@@@Priority@@@Effort@@@Gap@@@Action
	//   Priority 1 = fix first (low effort), 11 = fix last (PDF/UA composite target).
	//   Multiple rows per PDF — filter by Priority or Effort in Excel.
	//
	// IssueTracking: one row per PDF with at least one gap.
	//   Word    = comma-separated gap types (PDF_NO_TITLE, PDF_NO_LANGUAGE, etc.)
	//   Excerpt = document title (makes the ticket identifiable)
	//
	// Delimiter: @@@ — three @ signs used instead of pipe because PDF metadata
	// values may contain pipe characters. The @@@ sequence is vanishingly rare in
	// real PDF metadata. Pipe (|) is used by all other logs in this system.
	// ─────────────────────────────────────────────────────────────────────────

	public static class PdfQualityAnalyzer
	{
		private const string Header =
			"PageUrl@@@Title@@@Description@@@Keywords@@@Language@@@Tags@@@PdfA" +
			"@@@StructTree@@@RoleMap@@@Outlines@@@AltText@@@FormFields@@@PdfUA";

		private const string RemediationHeader =
			"PageUrl@@@Priority@@@Effort@@@Gap@@@Action";

		// ── Public entry point ────────────────────────────────────────────────

		public static List<IssueTracking.IssueRecord> Analyse(
			string downloadDirectory,
			string pdfQualityLogPath,
			string pdfRemediationLogPath)
		{
			var results = new List<PdfResult>();

			if (!Directory.Exists(downloadDirectory))
			{
				Logger.LogInfo("PdfQualityAnalyzer: download directory not found, skipping.");
				return [];
			}

			var pdfFiles = Directory.GetFiles(downloadDirectory, "*.pdf",
				SearchOption.TopDirectoryOnly);

			if (pdfFiles.Length == 0)
			{
				Logger.LogInfo("PdfQualityAnalyzer: no PDF files found.");
				WriteLog(pdfQualityLogPath, results);
				return [];
			}

			Logger.LogInfo($"PdfQualityAnalyzer: checking {pdfFiles.Length} PDF file(s).");

			foreach (var file in pdfFiles)
			{
				var filename = Path.GetFileName(file);
				var pageUrl = CrawlIndex.LookUpUrlForFile(filename);
				if (string.IsNullOrEmpty(pageUrl) || pageUrl == "error")
				{
					pageUrl = filename;
				}

				try
				{
					results.Add(CheckPdf(file, pageUrl));
				}
				catch (Exception ex)
				{
					Logger.LogWarning($"PdfQualityAnalyzer: could not read {filename}: {ex.Message}");
				}
			}

			WriteLog(pdfQualityLogPath, results);
			WriteRemediationLog(pdfRemediationLogPath, results);

			var issueCount = results.Count(r => r.HasGaps);
			Logger.LogInfo($"PdfQualityAnalyzer: {results.Count} PDF(s) checked, " +
				$"{issueCount} with quality gap(s). See {Path.GetFileName(pdfQualityLogPath)}.");

			return results
				.Where(r => r.HasGaps)
				.Select(r => new IssueTracking.IssueRecord
				{
					Type = "PDFQUALITY",
					Url = r.PageUrl,
					Word = r.GapSummary,
					SourceLabel = "PDF metadata",
					Excerpt = r.Title == "n/a" ? r.PageUrl : r.Title,
				})
				.ToList();
		}

		// ── Result model ──────────────────────────────────────────────────────

		public record PdfResult(
			string PageUrl,
			string Title,
			string Description,
			string Keywords,
			string Language,
			int Tags,
			int PdfA,
			int StructTree,
			int RoleMap,
			int Outlines,
			int AltText,
			int FormFields,
			int PdfUA)
		{
			// [KEEP] 0 = present but empty/unparseable — treated as a gap same as -1.
			// 1 = present and valid, -1 = absent, 0 = unknown/malformed.
			public bool HasGaps =>
				Title == "n/a" || Description == "n/a" || Keywords == "n/a" ||
				Language == "n/a" || Tags <= 0 || PdfA <= 0 || PdfUA <= 0 ||
				StructTree <= 0 || Outlines <= 0 || AltText <= 0;

			public string GapSummary
			{
				get
				{
					var gaps = new List<string>();
					if (Title == "n/a")
					{
						gaps.Add("PDF_NO_TITLE");
					}

					if (Description == "n/a")
					{
						gaps.Add("PDF_NO_DESCRIPTION");
					}

					if (Keywords == "n/a")
					{
						gaps.Add("PDF_NO_KEYWORDS");
					}

					if (Language == "n/a")
					{
						gaps.Add("PDF_NO_LANGUAGE");
					}

					if (Tags <= 0)
					{
						gaps.Add("PDF_NO_TAGS");
					}

					if (StructTree <= 0)
					{
						gaps.Add("PDF_NO_STRUCTTREE");
					}

					if (RoleMap <= 0)
					{
						gaps.Add("PDF_NO_ROLEMAP");
					}

					if (Outlines <= 0)
					{
						gaps.Add("PDF_NO_OUTLINES");
					}

					if (AltText <= 0)
					{
						gaps.Add("PDF_NO_ALTTEXT");
					}

					if (FormFields == 0)
					{
						gaps.Add("PDF_NO_FORMFIELD_NAMES");
					}

					if (PdfA <= 0)
					{
						gaps.Add("PDF_NO_PDFA");
					}

					if (PdfUA <= 0)
					{
						gaps.Add("PDF_NO_PDFUA");
					}

					return string.Join(", ", gaps);
				}
			}

			public string Serialize() =>
				$"{PageUrl}@@@{Title}@@@{Description}@@@{Keywords}@@@{Language}@@@{Tags}@@@{PdfA}" +
				$"@@@{StructTree}@@@{RoleMap}@@@{Outlines}@@@{AltText}@@@{FormFields}@@@{PdfUA}";
		}

		// ── PDF checker ───────────────────────────────────────────────────────

		internal static PdfResult CheckPdf(string filePath, string pageUrl)
		{
			var bytes = File.ReadAllBytes(filePath);
			return CheckPdfBytes(bytes, pageUrl);
		}

		internal static PdfResult CheckPdfBytes(byte[] bytes, string pageUrl)
		{
			// [KEEP] The full PDF is read as Latin-1 to safely locate ASCII markers
			// (<?xpacket, /MarkInfo etc.) without corrupting binary data. Latin-1 is
			// a lossless byte-to-char mapping for arbitrary binary content.
			// XMP is always UTF-8 — once we locate the XMP byte boundaries we decode
			// that segment separately as UTF-8 to correctly render non-ASCII chars
			// (e.g. German umlauts stored as multi-byte UTF-8 sequences in XMP).
			var latin1Text = Encoding.Latin1.GetString(bytes);
			var xmp = ExtractXmpAsUtf8(bytes, latin1Text);

			var title = SanitizeValue(xmp != null
				? ExtractXmpValue(xmp, "dc:title", "rdf:li", "rdf:Alt")
				: ExtractInfoValue(latin1Text, "/Title"));

			var description = SanitizeValue(xmp != null
				? ExtractXmpValue(xmp, "dc:description", "rdf:li", "rdf:Alt")
				: ExtractInfoValue(latin1Text, "/Subject"));

			// Keywords: try dc:subject (XMP), then pdf:Keywords (XMP simple element),
			// then /Keywords from Info dictionary. pdf:Keywords is widely used by
			// Adobe-based generators that omit dc:subject.
			var keywordsRaw = (xmp != null
				? ExtractXmpValue(xmp, "dc:subject", "rdf:li", "rdf:Bag")
					?? ExtractXmpAttribute(xmp, "pdf:Keywords")
				: null)
				?? ExtractInfoValue(latin1Text, "/Keywords");
			var keywords = SanitizeValue(keywordsRaw);

			// Language: try XMP dc:language first, then always fall back to /Lang
			// in the PDF document catalog (Latin-1 text). Most PDFs store language
			// in the catalog rather than XMP dc:language, so the fallback is critical.
			var languageRaw = (xmp != null
				? ExtractXmpValue(xmp, "dc:language", "rdf:li", "rdf:Bag")
				: null)
				?? ExtractInfoValue(latin1Text, "/Lang");
			var language = SanitizeValue(languageRaw);

			var pdfaConformance = xmp != null ? ExtractXmpAttribute(xmp, "pdfaid:conformance") : null;
			var pdfaPart = xmp != null ? ExtractXmpAttribute(xmp, "pdfaid:part") : null;
			var pdfA = (!string.IsNullOrWhiteSpace(pdfaConformance) ||
						  !string.IsNullOrWhiteSpace(pdfaPart)) ? 1 : -1;

			var pdfuaPart = xmp != null ? ExtractXmpAttribute(xmp, "pdfuaid:part") : null;
			var pdfUA = !string.IsNullOrWhiteSpace(pdfuaPart) ? 1 : -1;

			var tags = CheckTagged(latin1Text) ? 1 : -1;
			var structTree = CheckStructTree(latin1Text) ? 1 : -1;
			var roleMap = CheckRoleMap(latin1Text) ? 1 : -1;
			var outlines = CheckOutlines(latin1Text) ? 1 : -1;
			var altText = CheckAltText(latin1Text) ? 1 : -1;
			var formFields = CheckFormFields(latin1Text);

			return new PdfResult(
				PageUrl: pageUrl,
				Title: string.IsNullOrWhiteSpace(title) ? "n/a" : title,
				Description: string.IsNullOrWhiteSpace(description) ? "n/a" : description,
				Keywords: string.IsNullOrWhiteSpace(keywords) ? "n/a" : keywords,
				Language: string.IsNullOrWhiteSpace(language) ? "n/a" : language,
				Tags: tags,
				PdfA: pdfA,
				StructTree: structTree,
				RoleMap: roleMap,
				Outlines: outlines,
				AltText: altText,
				FormFields: formFields,
				PdfUA: pdfUA);
		}

		// ── XMP extraction ────────────────────────────────────────────────────

		/// <summary>
		/// Locates the XMP packet in the raw PDF bytes using ASCII marker strings,
		/// then decodes that byte range as UTF-8 to correctly render non-ASCII chars.
		/// [KEEP] XMP is always UTF-8 per the XMP specification. The surrounding PDF
		/// binary is read as Latin-1 only to find the ASCII packet boundaries.
		/// </summary>
		internal static string? ExtractXmpAsUtf8(byte[] pdfBytes, string latin1Text)
		{
			var start = latin1Text.IndexOf("<?xpacket begin", StringComparison.Ordinal);
			if (start < 0)
			{
				return null;
			}

			var end = latin1Text.IndexOf("<?xpacket end", start, StringComparison.Ordinal);
			if (end < 0)
			{
				return null;
			}

			var endClose = latin1Text.IndexOf("?>", end, StringComparison.Ordinal);
			if (endClose < 0)
			{
				return null;
			}
			// Decode the XMP byte range as UTF-8.
			return Encoding.UTF8.GetString(pdfBytes, start, endClose + 2 - start);
		}

		/// <summary>
		/// Latin-1 string extraction — used in unit tests where byte-level
		/// UTF-8 decoding is not required.
		/// </summary>
		internal static string? ExtractXmp(string pdfText)
		{
			var start = pdfText.IndexOf("<?xpacket begin", StringComparison.Ordinal);
			if (start < 0)
			{
				return null;
			}

			var end = pdfText.IndexOf("<?xpacket end", start, StringComparison.Ordinal);
			if (end < 0)
			{
				return null;
			}

			var endClose = pdfText.IndexOf("?>", end, StringComparison.Ordinal);
			if (endClose < 0)
			{
				return null;
			}

			return pdfText[start..(endClose + 2)];
		}

		/// <summary>
		/// Cleans an extracted metadata string: strips any residual XML tags,
		/// normalizes whitespace, and removes pipe characters (which would break
		/// the pipe-delimited log format).
		/// Rejects strings that are predominantly non-printable (binary garbage
		/// from accidental pattern matches inside PDF binary streams).
		/// </summary>
		private static string SanitizeValue(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				return string.Empty;
			}
			// Reject binary garbage: require at least 80% printable ASCII characters.
			// PDF binary streams can contain accidental matches for metadata keys.
			var printable = raw.Count(c => c >= 32 && c <= 126);
			if (raw.Length > 0 && (double)printable / raw.Length < 0.8)
			{
				return string.Empty;
			}
			// Strip any XML tags that leaked through.
			var stripped = System.Text.RegularExpressions.Regex.Replace(raw, @"<[^>]+>", " ");
			// Normalize whitespace (collapse newlines and multiple spaces).
			stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"\s+", " ").Trim();
			// Field delimiter is @@@ (consistent with 08-seo-data.log), so pipe
			// characters in values are preserved as-is.
			return stripped.Trim();
		}

		internal static string? ExtractXmpValue(string xmp, string element,
			string childElement, string containerElement)
		{
			var elemTag = $"<{element}>";
			var elemClose = $"</{element}>";
			var elemIdx = xmp.IndexOf(elemTag, StringComparison.OrdinalIgnoreCase);
			if (elemIdx < 0)
			{
				return null;
			}

			var elemEndIdx = xmp.IndexOf(elemClose, elemIdx, StringComparison.OrdinalIgnoreCase);

			// [KEEP] Container/child form: <dc:title><rdf:Alt><rdf:li>value</rdf:li></rdf:Alt></dc:title>
			// Some XMP serialisers emit the container BEFORE the element tag — search the
			// full element span in both directions to be order-independent.
			var containerTag = $"<{containerElement}>";
			var containerIdx = xmp.IndexOf(containerTag, elemIdx, StringComparison.OrdinalIgnoreCase);

			// Container must be inside the element span (or element span is unclosed).
			if (containerIdx >= 0 && (elemEndIdx < 0 || containerIdx < elemEndIdx))
			{
				var liOpenStart = xmp.IndexOf($"<{childElement}", containerIdx,
					StringComparison.OrdinalIgnoreCase);
				if (liOpenStart >= 0)
				{
					var liOpenEnd = xmp.IndexOf('>', liOpenStart);
					if (liOpenEnd >= 0)
					{
						// Self-closing tag <rdf:li .../> has no content — treat as empty.
						if (xmp[liOpenEnd - 1] == '/')
						{
							return null;
						}

						var liClose = xmp.IndexOf($"</{childElement}>", liOpenStart,
							StringComparison.OrdinalIgnoreCase);
						if (liClose > liOpenEnd)
						{
							return xmp[(liOpenEnd + 1)..liClose].Trim();
						}
					}
				}
			}
			else if (elemEndIdx > elemIdx)
			{
				// [KEEP] Simple/inline element form: <dc:title>value</dc:title>
				// Reached when the element exists but has no container (rdf:Alt/rdf:Bag).
				// Unusual in well-formed XMP but occurs in some PDF generators that
				// emit simplified XMP without the full RDF container structure.
				var inner = xmp[(elemIdx + elemTag.Length)..elemEndIdx].Trim();
				// Guard against tag-soup from out-of-order container falling through.
				// If the inner text contains XML tags, it's not a simple value.
				if (inner.Contains('<'))
				{
					return null;
				}

				return inner;
			}

			return null;
		}

		internal static string? ExtractXmpAttribute(string xmp, string attribute)
		{
			var match = Regex.Match(xmp,
				$"{attribute}=[\"']([^\"']+)[\"']",
				RegexOptions.IgnoreCase);
			if (match.Success)
			{
				return match.Groups[1].Value.Trim();
			}

			var tagStart = xmp.IndexOf($"<{attribute}>", StringComparison.OrdinalIgnoreCase);
			if (tagStart >= 0)
			{
				var tagEnd = xmp.IndexOf($"</{attribute}>", tagStart,
					StringComparison.OrdinalIgnoreCase);
				if (tagEnd > tagStart)
				{
					return xmp[(tagStart + attribute.Length + 2)..tagEnd].Trim();
				}
			}

			return null;
		}

		// ── Info dictionary ───────────────────────────────────────────────────

		internal static string? ExtractInfoValue(string pdfText, string key)
		{
			// [KEEP] PDF literal strings use \) to escape a literal ) character.
			// The pattern allows \) inside the string to avoid silent truncation.
			// Capture group replaces \) with ) after matching.
			var literalMatch = Regex.Match(pdfText,
				$@"{Regex.Escape(key)}\s*\(((?:[^\\)\r\n]|\\.)*)\)");
			if (literalMatch.Success)
			{
				return literalMatch.Groups[1].Value
					.Replace("\\)", ")").Replace("\\(", "(").Trim();
			}

			var hexMatch = Regex.Match(pdfText,
				$@"{Regex.Escape(key)}\s*<([0-9A-Fa-f]+)>");
			if (hexMatch.Success)
			{
				try
				{
					var hex = hexMatch.Groups[1].Value;
					var bytes = Enumerable.Range(0, hex.Length / 2)
						.Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
						.ToArray();
					if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
					{
						return Encoding.BigEndianUnicode
							.GetString(bytes, 2, bytes.Length - 2).Trim();
					}

					return Encoding.Latin1.GetString(bytes).Trim();
				}
				catch { }
			}

			return null;
		}

		// ── Tagged PDF check ──────────────────────────────────────────────────

		internal static bool CheckTagged(string pdfText)
		{
			var idx = pdfText.IndexOf("/MarkInfo", StringComparison.Ordinal);
			if (idx < 0)
			{
				return false;
			}

			var window = pdfText.Substring(idx, Math.Min(200, pdfText.Length - idx));
			// [KEEP] OrdinalIgnoreCase covers both "true" and "True" — the Ordinal
			// check was redundant. PDF spec uses /Marked true (lowercase).
			return window.Contains("/Marked true", StringComparison.OrdinalIgnoreCase);
		}

		// ── Structural checks ────────────────────────────────────────────────────

		/// <summary>
		/// Checks for /StructTreeRoot — indicates a logical document structure tree
		/// is present, required for proper reading order and accessibility.
		/// </summary>
		internal static bool CheckStructTree(string pdfText) =>
			pdfText.Contains("/StructTreeRoot", StringComparison.Ordinal);

		/// <summary>
		/// Checks for /RoleMap — maps custom PDF tag names to standard role names.
		/// Required when the document uses non-standard tag names.
		/// </summary>
		internal static bool CheckRoleMap(string pdfText) =>
			pdfText.Contains("/RoleMap", StringComparison.Ordinal);

		/// <summary>
		/// Checks for /Outlines with at least one child entry — PDF bookmarks that
		/// allow users to navigate long documents. /Outlines alone (empty) does not count.
		///
		/// [KEEP] The outline tree root is often an indirect reference (/Outlines N M R),
		/// with /Count and /First stored in the referenced object rather than inline.
		/// We therefore search the full document for /First paired with a positive /Count
		/// anywhere after an /Outlines key — covers both inline and indirect forms.
		/// </summary>
		internal static bool CheckOutlines(string pdfText)
		{
			// Must have /Outlines at all.
			if (!pdfText.Contains("/Outlines", StringComparison.Ordinal))
			{
				return false;
			}

			// [KEEP] Scan for /Count N (positive) with /First nearby — the signature of
			// a non-empty outline tree. Avoids full obj-block regex on large PDFs which
			// can cause catastrophic backtracking. Instead: find each /Count occurrence,
			// check its value is positive, then verify /First appears within 200 chars.
			var countMatches = Regex.Matches(pdfText, @"/Count\s+(\d+)");
			foreach (Match cm in countMatches)
			{
				if (!int.TryParse(cm.Groups[1].Value, out var count) || count <= 0)
				{
					continue;
				}

				var start = Math.Max(0, cm.Index - 100);
				var len = Math.Min(300, pdfText.Length - start);
				var vicinity = pdfText.Substring(start, len);
				if (vicinity.Contains("/First", StringComparison.Ordinal))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Checks for at least one /Alt entry — alt text on figure or formula elements.
		/// Presence indicates the document has some alt text; absence means none at all.
		/// [KEEP] Cannot verify completeness (every image has alt text) without full
		/// structure tree traversal — this is a signal, not a guarantee.
		/// </summary>
		internal static bool CheckAltText(string pdfText) =>
			pdfText.Contains("/Alt(", StringComparison.Ordinal) ||
			pdfText.Contains("/Alt <", StringComparison.Ordinal);

		/// <summary>
		/// Checks for interactive form fields (/AcroForm) and whether they have
		/// accessible names (/TU = tooltip/user-facing name).
		/// Returns:
		///   -1 = no AcroForm (no forms, no issue)
		///    0 = AcroForm present but no /TU entries (forms lack accessible names)
		///    1 = AcroForm present and at least one /TU entry found
		/// </summary>
		internal static int CheckFormFields(string pdfText)
		{
			if (!pdfText.Contains("/AcroForm", StringComparison.Ordinal))
			{
				return -1;
			}

			return pdfText.Contains("/TU(", StringComparison.Ordinal) ||
				pdfText.Contains("/TU <", StringComparison.Ordinal) ? 1 : 0;
		}

		// ── Log writer ────────────────────────────────────────────────────────

		private static void WriteLog(string logPath, List<PdfResult> results)
		{
			var lines = new List<string> { Header };
			lines.AddRange(results.OrderBy(r => r.PageUrl).Select(r => r.Serialize()));
			FileIo.WriteAllLinesWithRetry(logPath, lines, Path.GetFileName(logPath));
		}

		// ── Remediation log writer ────────────────────────────────────────────

		/// <summary>
		/// Writes 18-pdf-remediation.log — one row per gap per PDF, priority-ordered
		/// from simplest fix (metadata fields) to hardest (full PDF/UA compliance).
		/// PDF_NO_PDFUA is always the last row so the compliance target is always
		/// visible and filterable even when all other gaps are resolved.
		/// </summary>
		internal static void WriteRemediationLogPublic(List<PdfResult> results, string logPath) =>
			WriteRemediationLog(logPath, results);

		private static void WriteRemediationLog(string logPath, List<PdfResult> results)
		{
			// Priority | Effort | Gap code | Action
			var ladder = new (int Priority, string Effort, string Gap, string Action)[]
			{
				(1,  "low",    "PDF_NO_LANGUAGE",        "Set document language in PDF properties or export settings"),
				(2,  "low",    "PDF_NO_TITLE",            "Set document title in PDF properties or export settings"),
				(3,  "low",    "PDF_NO_DESCRIPTION",      "Set document description/subject in PDF properties"),
				(4,  "low",    "PDF_NO_KEYWORDS",         "Set document keywords in PDF properties"),
				(5,  "low",    "PDF_NO_OUTLINES",         "Add bookmarks/outline structure for navigation"),
				(6,  "medium", "PDF_NO_TAGS",             "Re-export from source with PDF tagging enabled"),
				(7,  "medium", "PDF_NO_PDFA",             "Re-export with PDF/A conformance enabled"),
				(8,  "high",   "PDF_NO_ALTTEXT",          "Add alt text to all images in the source document"),
				(9,  "high",   "PDF_NO_STRUCTTREE",       "Use an accessible authoring tool or PDF remediation tool to add structure tree"),
				(10, "high",   "PDF_NO_ROLEMAP",          "Verify custom tag role mapping using a PDF remediation tool"),
				(11, "high",   "PDF_NO_FORMFIELD_NAMES",  "Add accessible names (tooltips) to all interactive form fields"),
				(12, "high",   "PDF_NO_PDFUA",            "Full PDF/UA remediation — resolve all gaps above first"),
			};

			var lines = new List<string> { RemediationHeader };
			foreach (var result in results.OrderBy(r => r.PageUrl))
			{
				var gaps = result.GapSummary
					.Split(", ", StringSplitOptions.RemoveEmptyEntries)
					.ToHashSet(StringComparer.OrdinalIgnoreCase);

				if (gaps.Count == 0)
				{
					continue;
				}

				foreach (var (priority, effort, gap, action) in ladder)
				{
					// PDF_NO_PDFUA always appears as the last row when the PDF lacks
					// PDF/UA — even if it is the only gap — so it is always filterable.
					if (!gaps.Contains(gap))
					{
						continue;
					}

					lines.Add($"{result.PageUrl}@@@{priority}@@@{effort}@@@{gap}@@@{action}");
				}
			}
			FileIo.WriteAllLinesWithRetry(logPath, lines, Path.GetFileName(logPath));
		}
	}
}
