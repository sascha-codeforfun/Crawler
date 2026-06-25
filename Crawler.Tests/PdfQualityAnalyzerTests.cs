using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for PdfQualityAnalyzer internal methods and CheckPdfBytes.
	/// All tests work on in-memory byte arrays — no file I/O, no Logger dependency.
	/// </summary>
	public class PdfQualityAnalyzerTests
	{
		private const string PageUrl = "https://example.com/doc.pdf";

		// ── Helpers ───────────────────────────────────────────────────────────

		private static string XmpPacket(string inner) =>
			$"<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
			$"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
			$"<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
			$"{inner}" +
			$"</rdf:RDF></x:xmpmeta><?xpacket end=\"r\"?>";

		private static byte[] PdfWith(string body) =>
			Encoding.Latin1.GetBytes($"%PDF-1.4\n{body}\n%%EOF");

		// ── ExtractXmp ────────────────────────────────────────────────────────

		[Fact]
		public void ExtractXmpAsUtf8_GermanUmlauts_CorrectlyDecoded()
		{
			// XMP stores text as UTF-8 — "gemäß" must not become "gemÃ¤Ã"
			var xmpUtf8 = System.Text.Encoding.UTF8.GetBytes(
				"<?xpacket begin=\"\" id=\"W5M0\"?><x:xmpmeta><dc:title>gemäß DSGVO</dc:title></x:xmpmeta><?xpacket end=\"r\"?>");
			var latin1Text = System.Text.Encoding.Latin1.GetString(xmpUtf8);
			var result = PdfQualityAnalyzer.ExtractXmpAsUtf8(xmpUtf8, latin1Text);
			Assert.NotNull(result);
			Assert.Contains("gemäß", result);
			Assert.DoesNotContain("gemÃ", result);
		}

		[Fact]
		public void ExtractXmp_NoXmp_ReturnsNull()
		{
			Assert.Null(PdfQualityAnalyzer.ExtractXmp("%PDF-1.4\n%%EOF"));
		}

		[Fact]
		public void ExtractXmp_WithPacket_ReturnsContent()
		{
			var xmp = XmpPacket("<dc:title>Test</dc:title>");
			Assert.NotNull(PdfQualityAnalyzer.ExtractXmp(xmp));
		}

		// ── ExtractXmpValue ───────────────────────────────────────────────────

		[Fact]
		public void ExtractXmpValue_AltListForm_ExtractsTitle()
		{
			var xmp = XmpPacket(
				"<dc:title><rdf:Alt><rdf:li xml:lang=\"de\">Mein Dokument</rdf:li></rdf:Alt></dc:title>");
			Assert.Equal("Mein Dokument",
				PdfQualityAnalyzer.ExtractXmpValue(xmp, "dc:title", "rdf:li", "rdf:Alt"));
		}

		[Fact]
		public void ExtractXmpValue_SimpleForm_ExtractsValue()
		{
			var xmp = XmpPacket("<dc:title>Simple Title</dc:title>");
			Assert.Equal("Simple Title",
				PdfQualityAnalyzer.ExtractXmpValue(xmp, "dc:title", "rdf:li", "rdf:Alt"));
		}

		[Fact]
		public void ExtractXmpValue_BagForm_ExtractsLanguage()
		{
			var xmp = XmpPacket(
				"<dc:language><rdf:Bag><rdf:li>de</rdf:li></rdf:Bag></dc:language>");
			Assert.Equal("de",
				PdfQualityAnalyzer.ExtractXmpValue(xmp, "dc:language", "rdf:li", "rdf:Bag"));
		}

		[Fact]
		public void ExtractXmpValue_SelfClosingLi_ReturnsNull()
		{
			// <rdf:li xml:lang="x-default"/> with no content — should be treated as empty
			var xmp = XmpPacket(
				"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\"/></rdf:Alt></dc:description>");
			Assert.Null(PdfQualityAnalyzer.ExtractXmpValue(
				xmp, "dc:description", "rdf:li", "rdf:Alt"));
		}

		[Fact]
		public void ExtractXmpValue_ElementAbsent_ReturnsNull()
		{
			var xmp = XmpPacket("<dc:creator>Author</dc:creator>");
			Assert.Null(PdfQualityAnalyzer.ExtractXmpValue(xmp, "dc:title", "rdf:li", "rdf:Alt"));
		}

		// ── ExtractXmpAttribute ───────────────────────────────────────────────

		[Fact]
		public void ExtractXmpAttribute_AttributeForm_ExtractsPdfA()
		{
			var xmp = XmpPacket(
				"<rdf:Description pdfaid:part=\"2\" pdfaid:conformance=\"B\"/>");
			Assert.Equal("2", PdfQualityAnalyzer.ExtractXmpAttribute(xmp, "pdfaid:part"));
			Assert.Equal("B", PdfQualityAnalyzer.ExtractXmpAttribute(xmp, "pdfaid:conformance"));
		}

		[Fact]
		public void ExtractXmpAttribute_ElementForm_ExtractsPdfUA()
		{
			var xmp = XmpPacket("<pdfuaid:part>1</pdfuaid:part>");
			Assert.Equal("1", PdfQualityAnalyzer.ExtractXmpAttribute(xmp, "pdfuaid:part"));
		}

		[Fact]
		public void ExtractXmpAttribute_Absent_ReturnsNull()
		{
			var xmp = XmpPacket("<rdf:Description rdf:about=\"\"/>");
			Assert.Null(PdfQualityAnalyzer.ExtractXmpAttribute(xmp, "pdfaid:part"));
		}

		// ── ExtractInfoValue ──────────────────────────────────────────────────

		[Fact]
		public void ExtractInfoValue_LiteralString_ExtractsTitle()
		{
			Assert.Equal("My Document",
				PdfQualityAnalyzer.ExtractInfoValue("%PDF-1.4\n/Title (My Document)\n%%EOF", "/Title"));
		}

		[Fact]
		public void ExtractInfoValue_HexUtf16_ExtractsTitle()
		{
			// "Hi" as UTF-16 BE with BOM: FEFF 0048 0069
			Assert.Equal("Hi",
				PdfQualityAnalyzer.ExtractInfoValue("%PDF-1.4\n/Title <FEFF00480069>\n%%EOF", "/Title"));
		}

		[Fact]
		public void ExtractInfoValue_EscapedParenthesis_NotTruncated()
		{
			// PDF literal strings use \) to escape — should not truncate at the backslash
			Assert.Equal("Title with ) paren",
				PdfQualityAnalyzer.ExtractInfoValue(
					"%PDF-1.4\n/Title (Title with \\) paren)\n%%EOF", "/Title"));
		}

		[Fact]
		public void ExtractInfoValue_KeyAbsent_ReturnsNull()
		{
			Assert.Null(
				PdfQualityAnalyzer.ExtractInfoValue("%PDF-1.4\n/Author (Someone)\n%%EOF", "/Title"));
		}

		// ── CheckTagged ───────────────────────────────────────────────────────

		[Fact]
		public void CheckTagged_MarkedTrue_ReturnsTrue()
		{
			Assert.True(PdfQualityAnalyzer.CheckTagged(
				Encoding.Latin1.GetString(PdfWith("/MarkInfo << /Marked true >>"))));
		}

		[Fact]
		public void CheckTagged_MarkedFalse_ReturnsFalse()
		{
			Assert.False(PdfQualityAnalyzer.CheckTagged(
				Encoding.Latin1.GetString(PdfWith("/MarkInfo << /Marked false >>"))));
		}

		[Fact]
		public void CheckTagged_Absent_ReturnsFalse()
		{
			Assert.False(PdfQualityAnalyzer.CheckTagged(
				Encoding.Latin1.GetString(PdfWith("/Type /Catalog"))));
		}

		// ── CheckPdfBytes — integration ───────────────────────────────────────

		[Fact]
		public void CheckPdfBytes_EmptyPdf_AllFieldsNegative()
		{
			var result = PdfQualityAnalyzer.CheckPdfBytes(PdfWith(string.Empty), PageUrl);
			Assert.Equal("n/a", result.Title);
			Assert.Equal("n/a", result.Description);
			Assert.Equal("n/a", result.Keywords);
			Assert.Equal("n/a", result.Language);
			Assert.Equal(-1, result.Tags);
			Assert.Equal(-1, result.PdfA);
			Assert.Equal(-1, result.PdfUA);
		}

		[Fact]
		public void PdfResult_ZeroFlagTreatedAsGap()
		{
			// 0 = present but unparseable — should count as a gap same as -1
			var result = new PdfQualityAnalyzer.PdfResult(
				"https://example.com/doc.pdf", "Title", "Desc", "KW", "de",
				Tags: 0, PdfA: 1,
				StructTree: 1, RoleMap: 1, Outlines: 1, AltText: 1, FormFields: -1,
				PdfUA: 1);
			Assert.True(result.HasGaps);
			Assert.Contains("PDF_NO_TAGS", result.GapSummary);
		}

		[Fact]
		public void CheckPdfBytes_FullyCompliantPdf_NoGaps()
		{
			var xmpContent =
				"<rdf:Description rdf:about=\"\" " +
				"pdfaid:part=\"2\" pdfaid:conformance=\"B\" pdfuaid:part=\"1\">" +
				"<dc:title><rdf:Alt><rdf:li xml:lang=\"de\">Dokument</rdf:li></rdf:Alt></dc:title>" +
				"<dc:description><rdf:Alt><rdf:li>Beschreibung</rdf:li></rdf:Alt></dc:description>" +
				"<dc:subject><rdf:Bag><rdf:li>keyword1</rdf:li></rdf:Bag></dc:subject>" +
				"<dc:language><rdf:Bag><rdf:li>de</rdf:li></rdf:Bag></dc:language>" +
				"</rdf:Description>";
			var body = XmpPacket(xmpContent) +
				"\n/MarkInfo << /Marked true >>" +
				"\n/StructTreeRoot 1 0 R" +
				"\n/Outlines << /First 2 0 R /Count 3 >>" +
				"\n/Alt(Sample alt text for accessibility)";
			var result = PdfQualityAnalyzer.CheckPdfBytes(PdfWith(body), PageUrl);
			Assert.False(result.HasGaps);
			Assert.Equal("Dokument", result.Title);
			Assert.Equal("Beschreibung", result.Description);
			Assert.Equal("keyword1", result.Keywords);
			Assert.Equal("de", result.Language);
			Assert.Equal(1, result.Tags);
			Assert.Equal(1, result.PdfA);
			Assert.Equal(1, result.PdfUA);
		}

		[Fact]
		public void CheckPdfBytes_HasGaps_GapSummaryContainsTypes()
		{
			var result = PdfQualityAnalyzer.CheckPdfBytes(PdfWith(string.Empty), PageUrl);
			Assert.Contains("PDF_NO_TITLE", result.GapSummary);
			Assert.Contains("PDF_NO_DESCRIPTION", result.GapSummary);
			Assert.Contains("PDF_NO_KEYWORDS", result.GapSummary);
			Assert.Contains("PDF_NO_LANGUAGE", result.GapSummary);
			Assert.Contains("PDF_NO_TAGS", result.GapSummary);
			Assert.Contains("PDF_NO_PDFA", result.GapSummary);
			Assert.Contains("PDF_NO_PDFUA", result.GapSummary);
		}

		[Fact]
		public void CheckPdfBytes_HasTitle_TitleNotInGapSummary()
		{
			var xmpContent =
				"<rdf:Description rdf:about=\"\">" +
				"<dc:title><rdf:Alt><rdf:li>Doc</rdf:li></rdf:Alt></dc:title>" +
				"</rdf:Description>";
			var result = PdfQualityAnalyzer.CheckPdfBytes(
				PdfWith(XmpPacket(xmpContent)), PageUrl);
			Assert.DoesNotContain("PDF_NO_TITLE", result.GapSummary);
		}

		[Fact]
		public void CheckPdfBytes_TitleWithNewlines_Sanitized()
		{
			var xmp = XmpPacket(
				"<dc:title><rdf:Alt><rdf:li xml:lang=\"de\">Line one\nLine two</rdf:li></rdf:Alt></dc:title>");
			var result = PdfQualityAnalyzer.CheckPdfBytes(
				Encoding.Latin1.GetBytes($"%PDF-1.4\n{xmp}\n%%EOF"), PageUrl);
			Assert.DoesNotContain("\n", result.Title);
		}

		[Fact]
		public void CheckPdfBytes_TitleWithPipe_PipePreserved()
		{
			var xmp = XmpPacket(
				"<dc:title><rdf:Alt><rdf:li>Title|With|Pipes</rdf:li></rdf:Alt></dc:title>");
			var result = PdfQualityAnalyzer.CheckPdfBytes(
				Encoding.Latin1.GetBytes($"%PDF-1.4\n{xmp}\n%%EOF"), PageUrl);
			// Pipe characters in values are preserved — RFC-4180 quoting in
			// WriteCsvPair protects any delimiter inside a field.
			Assert.Contains("|", result.Title);
		}

		[Fact]
		public void CheckPdfBytes_ToFields_ReturnsExactlyThirteenColumns()
		{
			// Even with tricky content, the field row must have exactly 13 columns
			var result = PdfQualityAnalyzer.CheckPdfBytes(PdfWith(string.Empty), PageUrl);
			var parts = result.ToFields();
			Assert.Equal(13, parts.Length);
		}

		[Fact]
		public void CheckPdfBytes_AllFieldsHaveCorrectPageUrl()
		{
			var result = PdfQualityAnalyzer.CheckPdfBytes(PdfWith(string.Empty), PageUrl);
			Assert.Equal(PageUrl, result.PageUrl);
		}

		// ── CheckStructTree ─────────────────────────────────────────────────────

		[Fact]
		public void CheckStructTree_Present_ReturnsTrue()
		{
			Assert.True(PdfQualityAnalyzer.CheckStructTree(
				"%PDF-1.7\n/StructTreeRoot 1 0 R\n%%EOF"));
		}

		[Fact]
		public void CheckStructTree_Absent_ReturnsFalse()
		{
			Assert.False(PdfQualityAnalyzer.CheckStructTree("%PDF-1.7\n%%EOF"));
		}

		// ── CheckRoleMap ──────────────────────────────────────────────────────────

		[Fact]
		public void CheckRoleMap_Present_ReturnsTrue()
		{
			Assert.True(PdfQualityAnalyzer.CheckRoleMap(
				"%PDF-1.7\n/RoleMap << /Sect /Div >>\n%%EOF"));
		}

		[Fact]
		public void CheckRoleMap_Absent_ReturnsFalse()
		{
			Assert.False(PdfQualityAnalyzer.CheckRoleMap("%PDF-1.7\n%%EOF"));
		}

		// ── CheckOutlines ─────────────────────────────────────────────────────────

		[Fact]
		public void CheckOutlines_WithFirstChild_ReturnsTrue()
		{
			// Inline form: /Count and /First in the same dictionary.
			Assert.True(PdfQualityAnalyzer.CheckOutlines(
				"%PDF-1.7\n/Outlines << /First 2 0 R /Last 2 0 R /Count 1 >>\n%%EOF"));
		}

		[Fact]
		public void CheckOutlines_IndirectReference_ReturnsTrue()
		{
			// Indirect form: /Outlines references an object; /Count and /First
			// are in the referenced object body — the common real-world pattern.
			Assert.True(PdfQualityAnalyzer.CheckOutlines(
				"%PDF-1.7\n/Outlines 74 0 R\n74 0 obj\n<</Count 5/First 75 0 R>>\nendobj\n%%EOF"));
		}

		[Fact]
		public void CheckOutlines_WithNonZeroCount_ReturnsTrue()
		{
			// /Count must be paired with /First to distinguish outline from page tree.
			Assert.True(PdfQualityAnalyzer.CheckOutlines(
				"%PDF-1.7\n/Outlines << /Count 5 /First 2 0 R >>\n%%EOF"));
		}

		[Fact]
		public void CheckOutlines_WithCountZero_ReturnsFalse()
		{
			// Empty outline tree — /Count 0 means no bookmarks.
			Assert.False(PdfQualityAnalyzer.CheckOutlines(
				"%PDF-1.7\n/Outlines << /Count 0 >>\n%%EOF"));
		}

		[Fact]
		public void CheckOutlines_Absent_ReturnsFalse()
		{
			Assert.False(PdfQualityAnalyzer.CheckOutlines("%PDF-1.7\n%%EOF"));
		}

		// ── CheckAltText ──────────────────────────────────────────────────────────

		[Fact]
		public void CheckAltText_LiteralForm_ReturnsTrue()
		{
			Assert.True(PdfQualityAnalyzer.CheckAltText(
				"%PDF-1.7\n/Alt(Description of image)\n%%EOF"));
		}

		[Fact]
		public void CheckAltText_HexForm_ReturnsTrue()
		{
			Assert.True(PdfQualityAnalyzer.CheckAltText(
				"%PDF-1.7\n/Alt <FEFF004200696C64>\n%%EOF"));
		}

		[Fact]
		public void CheckAltText_Absent_ReturnsFalse()
		{
			Assert.False(PdfQualityAnalyzer.CheckAltText("%PDF-1.7\n%%EOF"));
		}

		// ── CheckFormFields ───────────────────────────────────────────────────────

		[Fact]
		public void CheckFormFields_NoAcroForm_ReturnsMinusOne()
		{
			// No forms at all — not a gap.
			Assert.Equal(-1, PdfQualityAnalyzer.CheckFormFields("%PDF-1.7\n%%EOF"));
		}

		[Fact]
		public void CheckFormFields_AcroFormWithTu_ReturnsOne()
		{
			// Forms present with accessible names — compliant.
			Assert.Equal(1, PdfQualityAnalyzer.CheckFormFields(
				"%PDF-1.7\n/AcroForm << /Fields [...] >>\n/TU(Name des Feldes)\n%%EOF"));
		}

		[Fact]
		public void CheckFormFields_AcroFormWithoutTu_ReturnsZero()
		{
			// Forms present but no accessible names — gap.
			Assert.Equal(0, PdfQualityAnalyzer.CheckFormFields(
				"%PDF-1.7\n/AcroForm << /Fields [...] >>\n%%EOF"));
		}

		// ── WriteRemediationLog ───────────────────────────────────────────────────

		[Fact]
		public void WriteRemediationLog_PdfWithGaps_PdfUaAlwaysLastRow()
		{
			var results = new List<PdfQualityAnalyzer.PdfResult>
			{
				new("https://example.com/doc.pdf", "n/a", "n/a", "n/a", "n/a",
					Tags: -1, PdfA: -1,
					StructTree: -1, RoleMap: -1, Outlines: -1, AltText: -1, FormFields: -1,
					PdfUA: -1)
			};
			var csvBase = Path.Combine(Path.GetTempPath(), $"remediation_{Guid.NewGuid():N}");
			try
			{
				PdfQualityAnalyzer.WriteRemediationLogPublic(results, csvBase);
				var lines = File.ReadAllLines(csvBase + IssueLogWriter.CsvSemicolonSuffix)
					.Where(l => l.Length > 0 && !l.StartsWith("PageUrl"))
					.ToList();
				Assert.NotEmpty(lines);
				Assert.Contains("PDF_NO_PDFUA", lines.Last());
			}
			finally
			{
				File.Delete(csvBase + IssueLogWriter.CsvSemicolonSuffix);
				File.Delete(csvBase + IssueLogWriter.CsvCommaSuffix);
			}
		}

		[Fact]
		public void WriteRemediationLog_CleanPdf_NoRows()
		{
			var results = new List<PdfQualityAnalyzer.PdfResult>
			{
				new("https://example.com/doc.pdf", "Title", "Desc", "KW", "de",
					Tags: 1, PdfA: 1,
					StructTree: 1, RoleMap: 1, Outlines: 1, AltText: 1, FormFields: -1,
					PdfUA: 1)
			};
			var csvBase = Path.Combine(Path.GetTempPath(), $"remediation_{Guid.NewGuid():N}");
			try
			{
				PdfQualityAnalyzer.WriteRemediationLogPublic(results, csvBase);
				var lines = File.ReadAllLines(csvBase + IssueLogWriter.CsvSemicolonSuffix)
					.Where(l => l.Length > 0 && !l.StartsWith("PageUrl"))
					.ToList();
				Assert.Empty(lines);
			}
			finally
			{
				File.Delete(csvBase + IssueLogWriter.CsvSemicolonSuffix);
				File.Delete(csvBase + IssueLogWriter.CsvCommaSuffix);
			}
		}

		[Fact]
		public void WriteRemediationLog_PriorityOrder_LowerPriorityFirst()
		{
			// Language (priority 1) and Tags (priority 6) both missing.
			var results = new List<PdfQualityAnalyzer.PdfResult>
			{
				new("https://example.com/doc.pdf", "Title", "Desc", "KW", "n/a",
					Tags: -1, PdfA: 1,
					StructTree: 1, RoleMap: 1, Outlines: 1, AltText: 1, FormFields: -1,
					PdfUA: 1)
			};
			var csvBase = Path.Combine(Path.GetTempPath(), $"remediation_{Guid.NewGuid():N}");
			try
			{
				PdfQualityAnalyzer.WriteRemediationLogPublic(results, csvBase);
				var lines = File.ReadAllLines(csvBase + IssueLogWriter.CsvSemicolonSuffix)
					.Where(l => l.Length > 0 && !l.StartsWith("PageUrl"))
					.ToList();
				Assert.Equal(2, lines.Count);
				Assert.Contains("PDF_NO_LANGUAGE", lines[0]);
				Assert.Contains("PDF_NO_TAGS", lines[1]);
			}
			finally
			{
				File.Delete(csvBase + IssueLogWriter.CsvSemicolonSuffix);
				File.Delete(csvBase + IssueLogWriter.CsvCommaSuffix);
			}
		}

		[Fact]
		public void Analyse_WritesQualityCsvPair_WithThirteenColumnHeader()
		{
			// Empty (but existing) download dir → no PDFs → the quality CSV pair is
			// still written header-only; exercises the WriteCsvPair path end to end.
			var dir = Path.Combine(Path.GetTempPath(), $"pdfq_{Guid.NewGuid():N}");
			Directory.CreateDirectory(dir);
			Logger.Initialize(Path.Combine(dir, "test.log"), silent: true);
			var qualityBase = Path.Combine(Path.GetTempPath(), $"17-q_{Guid.NewGuid():N}");
			var remediationBase = Path.Combine(Path.GetTempPath(), $"18-r_{Guid.NewGuid():N}");
			try
			{
				PdfQualityAnalyzer.Analyse(dir, qualityBase, remediationBase);

				var semicolon = qualityBase + IssueLogWriter.CsvSemicolonSuffix;
				Assert.True(File.Exists(semicolon), "quality semicolon CSV not written");
				Assert.True(File.Exists(qualityBase + IssueLogWriter.CsvCommaSuffix), "quality comma CSV not written");

				var header = IssueLogWriter.ParseCsvLine(File.ReadAllLines(semicolon)[0], ';');
				Assert.Equal(13, header.Length);
				Assert.Equal("PageUrl", header[0]);
				Assert.Equal("PdfUA", header[12]);
			}
			finally
			{
				Directory.Delete(dir, recursive: true);
				File.Delete(qualityBase + IssueLogWriter.CsvSemicolonSuffix);
				File.Delete(qualityBase + IssueLogWriter.CsvCommaSuffix);
			}
		}

	}
}