using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for the PDF-side file-type identification helpers in FileTypeClassifier:
	/// IsPdfContentType (header signal), LooksLikePdf (byte sniff), and ClassifyPdf
	/// (the pure policy decision). All are pure functions. Parallel to
	/// FileTypeClassifierHtmlTests (the HTML side).
	///
	/// This covers the UNWIRED pure logic only — the settle-phase wiring (renaming,
	/// routing to the PDF pipeline, finding emission) is validated separately.
	/// </summary>
	public class FileTypeClassifierPdfTests
	{
		// ── IsPdfContentType ─────────────────────────────────────────────
		// Counterpart to IsHtmlContentType, used by the settle phase (#338) as the
		// header signal for PDF classification. Only "application/pdf" qualifies.

		[Fact]
		public void IsPdfContentType_ApplicationPdf_ReturnsTrue()
		{
			Assert.True(FileTypeClassifier.IsPdfContentType("application/pdf"));
		}

		[Fact]
		public void IsPdfContentType_CaseInsensitive()
		{
			Assert.True(FileTypeClassifier.IsPdfContentType("APPLICATION/PDF"));
			Assert.True(FileTypeClassifier.IsPdfContentType("Application/Pdf"));
		}

		[Fact]
		public void IsPdfContentType_NullOrEmpty_ReturnsFalse()
		{
			// An undeclared type is not assumed to be a PDF.
			Assert.False(FileTypeClassifier.IsPdfContentType(null));
			Assert.False(FileTypeClassifier.IsPdfContentType(""));
		}

		[Fact]
		public void IsPdfContentType_KnownNonPdf_ReturnsFalse()
		{
			Assert.False(FileTypeClassifier.IsPdfContentType("text/html"));
			Assert.False(FileTypeClassifier.IsPdfContentType("image/png"));
			Assert.False(FileTypeClassifier.IsPdfContentType("application/zip"));
			Assert.False(FileTypeClassifier.IsPdfContentType("application/octet-stream"));
		}

		[Fact]
		public void IsPdfContentType_MediaTypePropertyAlreadyStripsParameters()
		{
			// Mirrors the HTML case: callers pass the bare MediaType, so a value
			// carrying parameters is correctly rejected by the equality check.
			Assert.False(FileTypeClassifier.IsPdfContentType("application/pdf; qs=0.9"));
		}

		// ── LooksLikePdf ────────────────────────────────────────────────────

		[Fact]
		public void LooksLikePdf_StandardMagic_ReturnsTrue()
		{
			var bytes = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\n...rest...");
			Assert.True(FileTypeClassifier.LooksLikePdf(bytes));
		}

		[Fact]
		public void LooksLikePdf_MagicWithLeadingBom_ReturnsTrue()
		{
			var bom = new byte[] { 0xEF, 0xBB, 0xBF };
			var magic = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4");
			var bytes = bom.Concat(magic).ToArray();
			Assert.True(FileTypeClassifier.LooksLikePdf(bytes));
		}

		[Theory]
		[InlineData("%PDF1.7")]              // missing the dash
		[InlineData(" %PDF-1.7")]            // leading space (malformed, not tolerated)
		[InlineData("<!DOCTYPE html>")]      // HTML
		[InlineData("PK\x03\x04")]           // zip
		[InlineData("plain text")]
		[InlineData("%PD")]                  // too short / partial
		public void LooksLikePdf_NonPdf_ReturnsFalse(string content)
		{
			var bytes = System.Text.Encoding.ASCII.GetBytes(content);
			Assert.False(FileTypeClassifier.LooksLikePdf(bytes));
		}

		[Fact]
		public void LooksLikePdf_Null_ReturnsFalse()
		{
			Assert.False(FileTypeClassifier.LooksLikePdf(null!));
		}

		[Fact]
		public void LooksLikePdf_Empty_ReturnsFalse()
		{
			Assert.False(FileTypeClassifier.LooksLikePdf(System.Array.Empty<byte>()));
		}

		[Fact]
		public void LooksLikePdf_PngMagic_ReturnsFalse()
		{
			var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
			Assert.False(FileTypeClassifier.LooksLikePdf(png));
		}

		// ── ClassifyPdf: agreement cases (no policy, no mismatch) ───────────

		[Theory]
		[InlineData(UnverifiedPdfPolicy.TrustByteSniff)]
		[InlineData(UnverifiedPdfPolicy.TrustContentType)]
		[InlineData(UnverifiedPdfPolicy.Quarantine)]
		[InlineData(UnverifiedPdfPolicy.AnalyseBlindly)]
		public void ClassifyPdf_AllSignalsPdf_TreatAsPdfNoMismatch_RegardlessOfPolicy(
			UnverifiedPdfPolicy policy)
		{
			var v = FileTypeClassifier.ClassifyPdf(policy, requestedExtIsPdf: true,
				headerIsPdf: true, sniffIsPdf: true);
			Assert.True(v.TreatAsPdf);
			Assert.False(v.IsMismatch);
		}

		[Theory]
		[InlineData(UnverifiedPdfPolicy.TrustByteSniff)]
		[InlineData(UnverifiedPdfPolicy.TrustContentType)]
		[InlineData(UnverifiedPdfPolicy.Quarantine)]
		[InlineData(UnverifiedPdfPolicy.AnalyseBlindly)]
		public void ClassifyPdf_NoSignalsPdf_NotPdfNoMismatch_RegardlessOfPolicy(
			UnverifiedPdfPolicy policy)
		{
			var v = FileTypeClassifier.ClassifyPdf(policy, requestedExtIsPdf: false,
				headerIsPdf: false, sniffIsPdf: false);
			Assert.False(v.TreatAsPdf);
			Assert.False(v.IsMismatch);
		}

		// ── ClassifyPdf: disagreement → policy decides, always a mismatch ───

		[Fact]
		public void ClassifyPdf_IsPdfButUndeclared_TrustByteSniff_TreatsAndFlags()
		{
			// Bytes are a PDF, but neither URL ext nor header said so (the real
			// extensionless / page-path-linked PDF case). TrustByteSniff → treat as
			// PDF, and it is a mismatch (finding-worthy).
			var v = FileTypeClassifier.ClassifyPdf(UnverifiedPdfPolicy.TrustByteSniff,
				requestedExtIsPdf: false, headerIsPdf: false, sniffIsPdf: true);
			Assert.True(v.TreatAsPdf);
			Assert.True(v.IsMismatch);
		}

		[Fact]
		public void ClassifyPdf_DeclaredPdfButNot_TrustByteSniff_RejectsAndFlags()
		{
			// Server says PDF (ext and/or header) but the bytes are not — symmetric
			// mismatch. TrustByteSniff → do NOT treat as PDF, and flag it.
			var v = FileTypeClassifier.ClassifyPdf(UnverifiedPdfPolicy.TrustByteSniff,
				requestedExtIsPdf: true, headerIsPdf: true, sniffIsPdf: false);
			Assert.False(v.TreatAsPdf);
			Assert.True(v.IsMismatch);
		}

		[Fact]
		public void ClassifyPdf_TrustContentType_FollowsHeader()
		{
			var v = FileTypeClassifier.ClassifyPdf(UnverifiedPdfPolicy.TrustContentType,
				requestedExtIsPdf: false, headerIsPdf: true, sniffIsPdf: false);
			Assert.True(v.TreatAsPdf);
			Assert.True(v.IsMismatch);
		}

		[Fact]
		public void ClassifyPdf_Quarantine_NeverTreatsAMismatchAsPdf()
		{
			var v = FileTypeClassifier.ClassifyPdf(UnverifiedPdfPolicy.Quarantine,
				requestedExtIsPdf: true, headerIsPdf: true, sniffIsPdf: false);
			Assert.False(v.TreatAsPdf);
			Assert.True(v.IsMismatch);
		}

		[Fact]
		public void ClassifyPdf_AnalyseBlindly_AlwaysTreatsAMismatchAsPdf()
		{
			// Genuine mismatch: ext says PDF, header and sniff do not. AnalyseBlindly
			// treats it as a PDF anyway, and flags it.
			var v = FileTypeClassifier.ClassifyPdf(UnverifiedPdfPolicy.AnalyseBlindly,
				requestedExtIsPdf: true, headerIsPdf: false, sniffIsPdf: false);
			Assert.True(v.TreatAsPdf);
			Assert.True(v.IsMismatch);
		}
	}
}
