using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Integration tests for PdfQualityAnalyzer.CheckPdfBytes using minimal
	/// synthetic PDF fixtures — no real content, no internal data.
	///
	/// Each fixture is purpose-built to exercise exactly one detection path.
	/// Fixture catalogue:
	///   LangInCatalog  — /Lang(de-DE) in catalog, XMP with pdfuaid:part=1,
	///                    pdf:Keywords, tagged, StructTree, RoleMap, Outlines.
	///   XmpNoLang      — XMP present, no language, pdfaid+pdfuaid, AltText.
	///   NoXmpNoLang    — No XMP, no /Lang, binary /Keywords garbage, AcroForm
	///                    without /TU, no structural features.
	///   CatalogLangNoXmp — No XMP at all, /Lang in catalog only, tagged,
	///                      StructTree, RoleMap.
	///   DcSubject      — XMP with dc:subject and pdf:Keywords, no language.
	/// </summary>
	public class PdfQualityLanguageTests
	{
		private static byte[] LangInCatalogPdf() =>
			Convert.FromBase64String(
				"JVBERi0xLjcKPD94cGFja2V0IGJlZ2luPSIiIGlkPSJXNU0wTXBDZWhpSHpyZVN6TlRjemtjOWQiPz4KPHg6eG1wbWV0YSB4bWxuczp4PSJhZG9iZTpuczpt" +
				"ZXRhLyI+CiA8cmRmOlJERiB4bWxuczpyZGY9Imh0dHA6Ly93d3cudzMub3JnLzE5OTkvMDIvMjItcmRmLXN5bnRheC1ucyMiPgogIDxyZGY6RGVzY3JpcHRp" +
				"b24gcmRmOmFib3V0PSIiIHhtbG5zOmRjPSJodHRwOi8vcHVybC5vcmcvZGMvZWxlbWVudHMvMS4xLyIgIHhtbG5zOnBkZnVhaWQ9Imh0dHA6Ly93d3cuYWlp" +
				"bS5vcmcvcGRmdWEvbnMvaWQvIiB4bWxuczpwZGY9Imh0dHA6Ly9ucy5hZG9iZS5jb20vcGRmLzEuMy8iPgogICA8cGRmdWFpZDpwYXJ0PjE8L3BkZnVhaWQ6" +
				"cGFydD48cGRmOktleXdvcmRzPkRhdGVuc2NodXR6LCBEU0dWTzwvcGRmOktleXdvcmRzPgogIDwvcmRmOkRlc2NyaXB0aW9uPgogPC9yZGY6UkRGPgo8L3g6" +
				"eG1wbWV0YT4KPD94cGFja2V0IGVuZD0idyI/PgovTGFuZyhkZS1ERSkKL01hcmtJbmZvPDwvTWFya2VkIHRydWU+PgovU3RydWN0VHJlZVJvb3QgOTkgMCBS" +
				"Ci9Sb2xlTWFwPDwvU2VjdCAvRGl2Pj4KL091dGxpbmVzIDUwIDAgUgo1MCAwIG9iago8PC9GaXJzdCA1MSAwIFIvTGFzdCA1MSAwIFIvQ291bnQgMz4+CmVu" +
				"ZG9iagolJUVPRg==");

		private static byte[] XmpNoLangPdf() =>
			Convert.FromBase64String(
				"JVBERi0xLjcKPD94cGFja2V0IGJlZ2luPSIiIGlkPSJXNU0wTXBDZWhpSHpyZVN6TlRjemtjOWQiPz4KPHg6eG1wbWV0YSB4bWxuczp4PSJhZG9iZTpuczpt" +
				"ZXRhLyI+CiA8cmRmOlJERiB4bWxuczpyZGY9Imh0dHA6Ly93d3cudzMub3JnLzE5OTkvMDIvMjItcmRmLXN5bnRheC1ucyMiPgogIDxyZGY6RGVzY3JpcHRp" +
				"b24gcmRmOmFib3V0PSIiIHhtbG5zOmRjPSJodHRwOi8vcHVybC5vcmcvZGMvZWxlbWVudHMvMS4xLyIgeG1sbnM6cGRmYWlkPSJodHRwOi8vd3d3LmFpaW0u" +
				"b3JnL3BkZmEvbnMvaWQvIiB4bWxuczpwZGZ1YWlkPSJodHRwOi8vd3d3LmFpaW0ub3JnL3BkZnVhL25zL2lkLyIgPgogICA8cGRmYWlkOnBhcnQ+MTwvcGRm" +
				"YWlkOnBhcnQ+PHBkZmFpZDpjb25mb3JtYW5jZT5BPC9wZGZhaWQ6Y29uZm9ybWFuY2U+PHBkZnVhaWQ6cGFydD4xPC9wZGZ1YWlkOnBhcnQ+CiAgPC9yZGY6" +
				"RGVzY3JpcHRpb24+CiA8L3JkZjpSREY+CjwveDp4bXBtZXRhPgo8P3hwYWNrZXQgZW5kPSJ3Ij8+Ci9NYXJrSW5mbzw8L01hcmtlZCB0cnVlPj4KL1N0cnVj" +
				"dFRyZWVSb290IDk5IDAgUgovUm9sZU1hcDw8L1NlY3QgL0Rpdj4+Ci9BbHQoVGVzdCBhbHQgdGV4dCBmb3IgaW1hZ2UpCiUlRU9G");

		private static byte[] NoXmpNoLangPdf() =>
			Convert.FromBase64String(
				"JVBERi0xLjcKL0tleXdvcmRzKICQoLDA0ODwAQIDBAUGBwgpCi9BY3JvRm9ybTw8L0ZpZWxkcyBbXT4+CiUlRU9G");

		private static byte[] CatalogLangNoXmpPdf() =>
			Convert.FromBase64String(
				"JVBERi0xLjcKL0xhbmcoZGUtREUpCi9NYXJrSW5mbzw8L01hcmtlZCB0cnVlPj4KL1N0cnVjdFRyZWVSb290IDk5IDAgUgovUm9sZU1hcDw8L1NlY3QgL0Rp" +
				"dj4+CiUlRU9G");

		private static byte[] DcSubjectPdf() =>
			Convert.FromBase64String(
				"JVBERi0xLjcKPD94cGFja2V0IGJlZ2luPSIiIGlkPSJXNU0wTXBDZWhpSHpyZVN6TlRjemtjOWQiPz4KPHg6eG1wbWV0YSB4bWxuczp4PSJhZG9iZTpuczpt" +
				"ZXRhLyI+CiA8cmRmOlJERiB4bWxuczpyZGY9Imh0dHA6Ly93d3cudzMub3JnLzE5OTkvMDIvMjItcmRmLXN5bnRheC1ucyMiPgogIDxyZGY6RGVzY3JpcHRp" +
				"b24gcmRmOmFib3V0PSIiIHhtbG5zOmRjPSJodHRwOi8vcHVybC5vcmcvZGMvZWxlbWVudHMvMS4xLyIgICB4bWxuczpwZGY9Imh0dHA6Ly9ucy5hZG9iZS5j" +
				"b20vcGRmLzEuMy8iPgogICA8ZGM6c3ViamVjdD48cmRmOkJhZz48cmRmOmxpPk9TUGx1cyBETVM8L3JkZjpsaT48L3JkZjpCYWc+PC9kYzpzdWJqZWN0Pjxw" +
				"ZGY6S2V5d29yZHM+T1NQbHVzIERNUzwvcGRmOktleXdvcmRzPgogIDwvcmRmOkRlc2NyaXB0aW9uPgogPC9yZGY6UkRGPgo8L3g6eG1wbWV0YT4KPD94cGFj" +
				"a2V0IGVuZD0idyI/PgolJUVPRg==");

		private static PdfQualityAnalyzer.PdfResult Check(byte[] bytes) =>
			PdfQualityAnalyzer.CheckPdfBytes(bytes, "test-url");

		[Fact]
		public void CheckPdfBytes_Language_InCatalogNoXmpDcLanguage_Detected()
		{
			// /Lang(de-DE) in catalog; XMP present but no dc:language.
			// Regression guard: was n/a before catalog fallback was added.
			var result = Check(LangInCatalogPdf());
			Assert.Equal("de-DE", result.Language);
		}

		[Fact]
		public void CheckPdfBytes_Language_XmpPresentNoLangAnywhere_ReturnsNa()
		{
			// XMP present, no language metadata anywhere.
			var result = Check(XmpNoLangPdf());
			Assert.Equal("n/a", result.Language);
		}

		[Fact]
		public void CheckPdfBytes_Language_NoXmpNoCatalogLang_ReturnsNa()
		{
			// No XMP, no /Lang in catalog.
			var result = Check(NoXmpNoLangPdf());
			Assert.Equal("n/a", result.Language);
		}

		[Fact]
		public void CheckPdfBytes_Language_NoXmpCatalogOnly_Detected()
		{
			// No XMP at all; language only in PDF catalog.
			var result = Check(CatalogLangNoXmpPdf());
			Assert.Equal("de-DE", result.Language);
		}

		[Fact]
		public void CheckPdfBytes_Keywords_InXmpPdfNamespace_Detected()
		{
			// Keywords in pdf:Keywords XMP element — not in dc:subject.
			var result = Check(LangInCatalogPdf());
			Assert.NotEqual("n/a", result.Keywords);
			Assert.Contains("Datenschutz", result.Keywords);
		}

		[Fact]
		public void CheckPdfBytes_Keywords_InDcSubjectAndPdfKeywords_Detected()
		{
			// dc:subject and pdf:Keywords both present; dc:subject wins.
			var result = Check(DcSubjectPdf());
			Assert.NotEqual("n/a", result.Keywords);
			Assert.Contains("OSPlus", result.Keywords);
		}

		[Fact]
		public void CheckPdfBytes_Keywords_BinaryGarbageMatch_ReturnsNa()
		{
			// Binary garbage in /Keywords must be rejected by printable-ratio guard.
			var result = Check(NoXmpNoLangPdf());
			Assert.Equal("n/a", result.Keywords);
		}

		[Fact]
		public void CheckPdfBytes_PdfUA_Detected()
		{
			// pdfuaid:part=1 in XMP.
			var result = Check(LangInCatalogPdf());
			Assert.Equal(1, result.PdfUA);
		}

		[Fact]
		public void CheckPdfBytes_PdfUA_Absent_ReturnsMinusOne()
		{
			var result = Check(NoXmpNoLangPdf());
			Assert.Equal(-1, result.PdfUA);
		}

		[Fact]
		public void CheckPdfBytes_PdfA_Present_Detected()
		{
			// pdfaid:part=1 and pdfaid:conformance=A present.
			var result = Check(XmpNoLangPdf());
			Assert.Equal(1, result.PdfA);
		}

		[Fact]
		public void CheckPdfBytes_PdfA_Absent_ReturnsMinusOne()
		{
			var result = Check(NoXmpNoLangPdf());
			Assert.Equal(-1, result.PdfA);
		}

		[Fact]
		public void CheckPdfBytes_Tags_TaggedPdf_ReturnsOne()
		{
			// /MarkInfo<</Marked true>> present.
			var result = Check(LangInCatalogPdf());
			Assert.Equal(1, result.Tags);
		}

		[Fact]
		public void CheckPdfBytes_Tags_UntaggedPdf_ReturnsMinusOne()
		{
			var result = Check(NoXmpNoLangPdf());
			Assert.Equal(-1, result.Tags);
		}

		[Fact]
		public void CheckPdfBytes_StructTree_Present_ReturnsOne()
		{
			var result = Check(LangInCatalogPdf());
			Assert.Equal(1, result.StructTree);
		}

		[Fact]
		public void CheckPdfBytes_StructTree_Absent_ReturnsMinusOne()
		{
			var result = Check(NoXmpNoLangPdf());
			Assert.Equal(-1, result.StructTree);
		}

		[Fact]
		public void CheckPdfBytes_RoleMap_Present_ReturnsOne()
		{
			var result = Check(LangInCatalogPdf());
			Assert.Equal(1, result.RoleMap);
		}

		[Fact]
		public void CheckPdfBytes_RoleMap_Absent_ReturnsMinusOne()
		{
			var result = Check(NoXmpNoLangPdf());
			Assert.Equal(-1, result.RoleMap);
		}

		[Fact]
		public void CheckPdfBytes_Outlines_Present_ReturnsOne()
		{
			// /Outlines with indirect ref object containing /Count 3 /First.
			var result = Check(LangInCatalogPdf());
			Assert.Equal(1, result.Outlines);
		}

		[Fact]
		public void CheckPdfBytes_Outlines_Absent_ReturnsMinusOne()
		{
			var result = Check(NoXmpNoLangPdf());
			Assert.Equal(-1, result.Outlines);
		}

		[Fact]
		public void CheckPdfBytes_AltText_Present_ReturnsOne()
		{
			var result = Check(XmpNoLangPdf());
			Assert.Equal(1, result.AltText);
		}

		[Fact]
		public void CheckPdfBytes_AltText_Absent_ReturnsMinusOne()
		{
			var result = Check(NoXmpNoLangPdf());
			Assert.Equal(-1, result.AltText);
		}

		[Fact]
		public void CheckPdfBytes_FormFields_AcroFormWithoutTu_ReturnsZero()
		{
			// AcroForm present but no /TU — forms lack accessible names.
			var result = Check(NoXmpNoLangPdf());
			Assert.Equal(0, result.FormFields);
		}

		[Fact]
		public void CheckPdfBytes_FormFields_NoAcroForm_ReturnsMinusOne()
		{
			var result = Check(LangInCatalogPdf());
			Assert.Equal(-1, result.FormFields);
		}

		[Fact]
		public void CheckPdfBytes_CatalogLangNoXmp_StructuralValues()
		{
			// No XMP; all structural data from raw PDF markers.
			var result = Check(CatalogLangNoXmpPdf());
			Assert.Equal("de-DE", result.Language);
			Assert.Equal(1, result.Tags);
			Assert.Equal(1, result.StructTree);
			Assert.Equal(1, result.RoleMap);
			Assert.Equal(-1, result.PdfUA);
			Assert.Equal(-1, result.PdfA);
		}

		[Fact]
		public void CheckPdfBytes_XmpNoLang_StructuralValues()
		{
			// XMP present but no language; full compliance markers set.
			var result = Check(XmpNoLangPdf());
			Assert.Equal("n/a", result.Language);
			Assert.Equal(1, result.PdfUA);
			Assert.Equal(1, result.PdfA);
			Assert.Equal(1, result.Tags);
			Assert.Equal(1, result.StructTree);
			Assert.Equal(1, result.AltText);
		}

	}
}