using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for AssetQuality's dependency-free logic: image classification under
	/// policy, header-byte dimension parsing (PNG/GIF), size checks, and header
	/// sidecar parsing. The MetadataExtractor path (CheckMetadata) needs real
	/// image fixtures with embedded EXIF and is exercised by the live run rather
	/// than here.
	/// </summary>
	[Collection("Logger")]
	public class AssetQualityTests
	{
		public AssetQualityTests()
		{
			var tempLog = Path.Combine(Path.GetTempPath(), $"asset-test-logger-{Guid.NewGuid()}.log");
			Logger.Initialize(tempLog, silent: true);
		}

		private static AssetQualityConfig Cfg() => new();

		// ── ClassifyImage (policy) ────────────────────────────────────────

		[Fact]
		public void Classify_AllAgreeImage_TreatNoMismatch()
		{
			var c = FileTypeClassifier.ClassifyImage(UnverifiedImagePolicy.TrustByteSniff, true, true, true);
			Assert.True(c.TreatAsImage);
			Assert.False(c.IsMismatch);
		}

		[Fact]
		public void Classify_NoneImage_NotTreatedNoMismatch()
		{
			var c = FileTypeClassifier.ClassifyImage(UnverifiedImagePolicy.TrustByteSniff, false, false, false);
			Assert.False(c.TreatAsImage);
			Assert.False(c.IsMismatch);
		}

		[Fact]
		public void Classify_Disagree_TrustByteSniff_FollowsSniff()
		{
			// ext says no, header says no, sniff says yes → byte-sniff policy trusts sniff.
			var c = FileTypeClassifier.ClassifyImage(UnverifiedImagePolicy.TrustByteSniff, false, false, true);
			Assert.True(c.TreatAsImage);
			Assert.True(c.IsMismatch);
		}

		[Fact]
		public void Classify_Disagree_Quarantine_NeverImage()
		{
			var c = FileTypeClassifier.ClassifyImage(UnverifiedImagePolicy.Quarantine, true, true, false);
			Assert.False(c.TreatAsImage);
			Assert.True(c.IsMismatch);
		}

		[Fact]
		public void Classify_Disagree_AnalyseBlindly_AlwaysImage()
		{
			// Genuine disagreement: ext yes, header no, sniff no.
			var c = FileTypeClassifier.ClassifyImage(UnverifiedImagePolicy.AnalyseBlindly, true, false, false);
			Assert.True(c.TreatAsImage);
			Assert.True(c.IsMismatch);
		}

		// ── LooksLikeImage (magic sniff) ──────────────────────────────────

		[Fact]
		public void Sniff_Jpeg_True()
		{
			Assert.True(FileTypeClassifier.LooksLikeImage([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]));
		}

		[Fact]
		public void Sniff_Png_True()
		{
			Assert.True(FileTypeClassifier.LooksLikeImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
		}

		[Fact]
		public void Sniff_Gif_True()
		{
			Assert.True(FileTypeClassifier.LooksLikeImage(Encoding.ASCII.GetBytes("GIF89a__")));
		}

		[Fact]
		public void Sniff_Webp_True()
		{
			var b = new byte[12];
			Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
			Encoding.ASCII.GetBytes("WEBP").CopyTo(b, 8);
			Assert.True(FileTypeClassifier.LooksLikeImage(b));
		}

		[Fact]
		public void Sniff_Html_False()
		{
			Assert.False(FileTypeClassifier.LooksLikeImage(Encoding.ASCII.GetBytes("<!DOCTYPE html>")));
		}

		// ── SniffedImageExtension (re-settle gate) ────────────────────────

		[Fact]
		public void SniffedExt_Jpeg()
		{
			Assert.Equal("jpg", FileTypeClassifier.SniffedImageExtension([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]));
		}

		[Fact]
		public void SniffedExt_Png()
		{
			Assert.Equal("png", FileTypeClassifier.SniffedImageExtension([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
		}

		[Fact]
		public void SniffedExt_Gif()
		{
			Assert.Equal("gif", FileTypeClassifier.SniffedImageExtension(Encoding.ASCII.GetBytes("GIF89a__")));
		}

		[Fact]
		public void SniffedExt_Webp()
		{
			var b = new byte[12];
			Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
			Encoding.ASCII.GetBytes("WEBP").CopyTo(b, 8);
			Assert.Equal("webp", FileTypeClassifier.SniffedImageExtension(b));
		}

		[Fact]
		public void SniffedExt_NonImage_Null()
		{
			Assert.Null(FileTypeClassifier.SniffedImageExtension(Encoding.ASCII.GetBytes("%PDF-1.4")));
		}

		// ── ReadDimensionsFromHead (PNG / GIF byte layouts) ───────────────

		[Fact]
		public void Dimensions_Png_ReadsBigEndianIhdr()
		{
			// PNG signature + IHDR: width=800 (0x0320), height=600 (0x0258), big-endian.
			var b = new byte[24];
			byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
			sig.CopyTo(b, 0);
			// width at 16..19 = 0x00000320
			b[16] = 0x00; b[17] = 0x00; b[18] = 0x03; b[19] = 0x20;
			// height at 20..23 = 0x00000258
			b[20] = 0x00; b[21] = 0x00; b[22] = 0x02; b[23] = 0x58;

			var dims = AssetQuality.ReadDimensionsFromHead(b);
			Assert.Equal((800, 600), dims);
		}

		[Fact]
		public void Dimensions_Gif_ReadsLittleEndian()
		{
			// GIF89a + width=100 (0x64,0x00), height=50 (0x32,0x00), little-endian.
			var b = new byte[10];
			Encoding.ASCII.GetBytes("GIF89a").CopyTo(b, 0);
			b[6] = 0x64; b[7] = 0x00; // 100
			b[8] = 0x32; b[9] = 0x00; // 50
			var dims = AssetQuality.ReadDimensionsFromHead(b);
			Assert.Equal((100, 50), dims);
		}

		[Fact]
		public void Dimensions_NonPngGif_ReturnsNull()
		{
			// JPEG magic — ReadDimensionsFromHead does not handle it (needs streaming).
			Assert.Null(AssetQuality.ReadDimensionsFromHead([0xFF, 0xD8, 0xFF, 0xE0]));
		}

		[Fact]
		public void Dimensions_ShortBuffer_ReturnsNull()
		{
			Assert.Null(AssetQuality.ReadDimensionsFromHead([0x89, 0x50]));
		}

		// ── CheckDimensions findings ──────────────────────────────────────

		private static byte[] PngHead(int w, int h)
		{
			var b = new byte[24];
			byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
			sig.CopyTo(b, 0);
			b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
			b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
			return b;
		}

		[Fact]
		public void CheckDimensions_NormalImage_NoFinding()
		{
			var findings = AssetQuality.CheckDimensions("x.png", PngHead(800, 600), Cfg()).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void CheckDimensions_TrackingPixel_FlagsDegenerate()
		{
			var findings = AssetQuality.CheckDimensions("x.png", PngHead(1, 1), Cfg()).ToList();
			var f = Assert.Single(findings);
			Assert.Equal("DIMENSIONS", f.Word);
			Assert.Equal("DEGENERATE", f.Detail);
		}

		[Fact]
		public void CheckDimensions_Huge_FlagsOversize()
		{
			var findings = AssetQuality.CheckDimensions("x.png", PngHead(9000, 600), Cfg()).ToList();
			var f = Assert.Single(findings);
			Assert.Equal("OVERSIZE", f.Detail);
		}

		[Fact]
		public void CheckDimensions_RespectsConfigThreshold()
		{
			var cfg = new AssetQualityConfig { MaxImageDimensionPixels = 10000 };
			var findings = AssetQuality.CheckDimensions("x.png", PngHead(9000, 600), cfg).ToList();
			Assert.Empty(findings); // 9000 < 10000 now
		}

		// ── CheckSize findings ────────────────────────────────────────────

		[Fact]
		public void CheckSize_UnderThreshold_NoFinding()
		{
			// No sidecar (so no length-mismatch), small size.
			var findings = AssetQuality.CheckSize("nonexistent.png", 5000, Cfg()).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void CheckSize_OverThreshold_FlagsOversize()
		{
			var findings = AssetQuality.CheckSize("nonexistent.png", 2_000_000, Cfg()).ToList();
			var f = Assert.Single(findings);
			Assert.Equal("SIZE", f.Word);
			Assert.Equal("OVERSIZE", f.Detail);
		}

		[Fact]
		public void CheckSize_RespectsConfigThreshold()
		{
			var cfg = new AssetQualityConfig { MaxImageBytes = 5_000_000 };
			var findings = AssetQuality.CheckSize("nonexistent.png", 2_000_000, cfg).ToList();
			Assert.Empty(findings);
		}
	}
}
