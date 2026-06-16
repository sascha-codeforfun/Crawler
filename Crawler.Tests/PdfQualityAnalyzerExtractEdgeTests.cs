using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Batch B for PdfQualityAnalyzer — the extractor EDGE branches the happy-path
	/// tests in PdfQualityAnalyzerTests don't reach: the XMP packet boundary guards,
	/// the container-vs-inline-vs-unclosed-child forms of ExtractXmpValue, the
	/// non-BOM hex path of ExtractInfoValue, the open-but-unclosed XMP attribute tag,
	/// and CheckOutlines' valid-tree / zero-count arms. All methods are pure string
	/// (or byte) functions, exercised directly. SYNTHETIC fixtures.
	/// </summary>
	public class PdfQualityAnalyzerExtractEdgeTests
	{
		// XMP-as-UTF8 takes the raw bytes plus their Latin-1 view (how CheckPdfBytes
		// locates the ASCII packet boundaries).
		private static (byte[] Bytes, string Latin1) L1(string s)
		{
			var bytes = Encoding.Latin1.GetBytes(s);
			return (bytes, Encoding.Latin1.GetString(bytes));
		}

		// ── ExtractXmpAsUtf8 boundary guards ────────────────────────────────

		[Fact]
		public void ExtractXmpAsUtf8_BeginWithoutEnd_ReturnsNull()
		{
			var (bytes, latin1) = L1("<?xpacket begin='x'?> some xmp but no closing marker");
			Assert.Null(PdfQualityAnalyzer.ExtractXmpAsUtf8(bytes, latin1));
		}

		[Fact]
		public void ExtractXmpAsUtf8_EndWithoutClose_ReturnsNull()
		{
			// begin and end markers present, but no "?>" follows the end marker.
			var (bytes, latin1) = L1("<?xpacket begin <?xpacket end");
			Assert.Null(PdfQualityAnalyzer.ExtractXmpAsUtf8(bytes, latin1));
		}

		// ── ExtractXmp (string) boundary guards ─────────────────────────────

		[Fact]
		public void ExtractXmp_BeginWithoutEnd_ReturnsNull()
		{
			Assert.Null(PdfQualityAnalyzer.ExtractXmp("<?xpacket begin='x'?> xmp with no end"));
		}

		[Fact]
		public void ExtractXmp_EndWithoutClose_ReturnsNull()
		{
			Assert.Null(PdfQualityAnalyzer.ExtractXmp("<?xpacket begin <?xpacket end"));
		}

		// ── ExtractXmpValue forms ───────────────────────────────────────────

		[Fact]
		public void ExtractXmpValue_ContainerWithoutChild_ReturnsNull()
		{
			// rdf:Alt container present but no rdf:li inside it.
			const string xmp = "<dc:title><rdf:Alt></rdf:Alt></dc:title>";
			Assert.Null(PdfQualityAnalyzer.ExtractXmpValue(xmp, "dc:title", "rdf:li", "rdf:Alt"));
		}

		[Fact]
		public void ExtractXmpValue_InlineWithNestedTag_ReturnsNull()
		{
			// No container; the inline value contains a tag → rejected as tag-soup.
			const string xmp = "<dc:title>foo<b></dc:title>";
			Assert.Null(PdfQualityAnalyzer.ExtractXmpValue(xmp, "dc:title", "rdf:li", "rdf:Alt"));
		}

		[Fact]
		public void ExtractXmpValue_ChildNotClosed_ReturnsNull()
		{
			// rdf:li opens but is never closed before the container ends.
			const string xmp = "<dc:title><rdf:Alt><rdf:li>val</rdf:Alt></dc:title>";
			Assert.Null(PdfQualityAnalyzer.ExtractXmpValue(xmp, "dc:title", "rdf:li", "rdf:Alt"));
		}

		// ── ExtractInfoValue non-BOM hex ────────────────────────────────────

		[Fact]
		public void ExtractInfoValue_HexWithoutBom_DecodedAsLatin1()
		{
			// <48656C6C6F> = "Hello", no FEFF BOM → Latin-1 decode path.
			Assert.Equal("Hello",
				PdfQualityAnalyzer.ExtractInfoValue("/Title <48656C6C6F>", "/Title"));
		}

		// ── ExtractXmpAttribute open-but-unclosed ───────────────────────────

		[Fact]
		public void ExtractXmpAttribute_OpenTagNotClosed_ReturnsNull()
		{
			// Element-form open tag with no matching close, and no attribute form.
			Assert.Null(PdfQualityAnalyzer.ExtractXmpAttribute("<pdfuaid:part>", "pdfuaid:part"));
		}

		// ── CheckOutlines ───────────────────────────────────────────────────

		[Fact]
		public void CheckOutlines_ValidTree_ReturnsTrue()
		{
			Assert.True(PdfQualityAnalyzer.CheckOutlines(
				"/Outlines 1 0 R\n1 0 obj << /Count 5 /First 2 0 R >>"));
		}

		[Fact]
		public void CheckOutlines_ZeroCount_ReturnsFalse()
		{
			// /Outlines present but the only /Count is 0 → not a non-empty tree.
			Assert.False(PdfQualityAnalyzer.CheckOutlines("/Outlines\n/Count 0"));
		}
	}
}
