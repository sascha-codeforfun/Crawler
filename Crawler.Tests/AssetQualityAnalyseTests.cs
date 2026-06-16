using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Batch A — AssetQuality.Analyse orchestration plus CheckSize, CheckDimensions,
	/// ReadImageDimensions (PNG/GIF/JPEG), the sidecar readers and ReadHead. Driven
	/// with crafted image-header bytes (valid magic + fixed-position dimension
	/// fields) and ".header" sidecar files. CheckMetadata (EXIF leak detection) is
	/// Batch B — it needs a real metadata-bearing image fixture — so metadata
	/// checking is disabled here to keep findings limited to SIZE / DIMENSIONS.
	///
	/// The Classify/Sniff/ReadDimensionsFromHead leaf helpers are covered by the
	/// existing AssetQualityTests; this file covers the scan pipeline. SYNTHETIC
	/// fixtures throughout.
	///
	/// UrlCache is process-wide static with no reset, so URL tests use GUID-unique
	/// filenames. In the Logger collection: Analyse / CrawlIndex log via the static
	/// Logger.
	/// </summary>
	[Collection("Logger")]
	public class AssetQualityAnalyseTests : IDisposable
	{
		private readonly string _dir;
		private readonly string _log;

		public AssetQualityAnalyseTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"AssetQ_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
			_log = Path.Combine(_dir, "asset_quality.log");
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── byte builders ───────────────────────────────────────────────────

		private static byte[] Be16(int v) => new[] { (byte)(v >> 8), (byte)v };
		private static byte[] Be32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
		private static byte[] Le16(int v) => new[] { (byte)v, (byte)(v >> 8) };

		// Minimal PNG header: 8-byte signature, IHDR chunk with width/height at the
		// fixed offsets the analyzer reads (16..23, big-endian).
		private static byte[] Png(int w, int h)
		{
			var b = new List<byte>
			{
				0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // signature
				0x00, 0x00, 0x00, 0x0D,                         // IHDR length
				0x49, 0x48, 0x44, 0x52,                         // "IHDR"
			};
			b.AddRange(Be32(w));
			b.AddRange(Be32(h));
			b.AddRange(new byte[] { 0x08, 0x06, 0x00, 0x00, 0x00 }); // bit depth etc.
			return b.ToArray();
		}

		// Minimal GIF header (10 bytes): "GIF89a" + logical-screen w/h little-endian.
		private static byte[] Gif(int w, int h)
		{
			var b = new List<byte> { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' };
			b.AddRange(Le16(w));
			b.AddRange(Le16(h));
			return b.ToArray(); // exactly 10 bytes → exercises ReadHead's short-read trim
		}

		// Minimal JPEG: SOI then an SOF0 segment carrying height/width (big-endian).
		private static byte[] Jpeg(int w, int h)
		{
			var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08 };
			b.AddRange(Be16(h));
			b.AddRange(Be16(w));
			b.AddRange(new byte[20]); // padding
			return b.ToArray();
		}

		// ── fixture helpers ─────────────────────────────────────────────────

		private static string NewPng() => $"img_{Guid.NewGuid():N}.png";
		private static string NewGif() => $"img_{Guid.NewGuid():N}.gif";
		private static string NewJpg() => $"img_{Guid.NewGuid():N}.jpg";

		private string Write(string filename, byte[] bytes)
		{
			File.WriteAllBytes(Path.Combine(_dir, filename), bytes);
			return Path.Combine(_dir, filename);
		}

		private void WriteSidecar(string filename, string? contentType = null, string? contentLength = null)
		{
			var sidecar = Path.ChangeExtension(Path.Combine(_dir, filename), "header");
			var lines = new List<string> { "=== RESPONSE ===" };
			if (contentType != null) lines.Add($"Content-Type: {contentType}");
			if (contentLength != null) lines.Add($"Content-Length: {contentLength}");
			File.WriteAllLines(sidecar, lines, Encoding.UTF8);
		}

		private void RegisterUrl(string filename, string url, string source = "discovery")
		{
			var path = Path.Combine(_dir, $"lookup_{Guid.NewGuid():N}.lku");
			File.WriteAllLines(path, new[] { $"{filename}|{url}|{source}" }, Encoding.UTF8);
			UrlCache.LoadCache(path);
		}

		private static AssetQualityConfig Cfg(
			bool metadata = false, bool size = true, bool dims = true,
			long maxBytes = 1_048_576, int maxDim = 5000, params string[] exclusions)
			=> new()
			{
				CheckMetadataLeakage = metadata,
				CheckSize = size,
				CheckDimensions = dims,
				MaxImageBytes = maxBytes,
				MaxImageDimensionPixels = maxDim,
				SizeAndDimensionExclusions = exclusions.ToList(),
			};

		private List<IssueTracking.IssueRecord> Analyse(AssetQualityConfig config) =>
			AssetQuality.Analyse(_dir, _log, config);

		// ── orchestration: skips ────────────────────────────────────────────

		[Fact]
		public void Analyse_AllChecksDisabled_ReturnsEmpty()
		{
			Write(NewPng(), Png(100, 100));
			Assert.Empty(Analyse(Cfg(metadata: false, size: false, dims: false)));
		}

		[Fact]
		public void Analyse_DirectoryMissing_ReturnsEmpty()
		{
			Assert.Empty(AssetQuality.Analyse(Path.Combine(_dir, "nope"), _log, Cfg()));
		}

		[Fact]
		public void Analyse_NonImageFile_Skipped()
		{
			File.WriteAllText(Path.Combine(_dir, $"note_{Guid.NewGuid():N}.txt"),
				"this is plain text, not an image", Encoding.UTF8);
			Assert.Empty(Analyse(Cfg()));
		}

		// ── orchestration: findings ─────────────────────────────────────────

		[Fact]
		public void Analyse_OversizeBytes_FlagsAssetSize()
		{
			Write(NewPng(), Png(100, 100));
			var r = Assert.Single(Analyse(Cfg(maxBytes: 10)));
			Assert.Equal("ASSET", r.Type);
			Assert.Equal("ASSET_SIZE", r.Word);
			Assert.Equal("OVERSIZE", r.SourceLabel);
		}

		[Fact]
		public void Analyse_SidecarContentLengthMismatch_FlagsLengthMismatch()
		{
			var name = NewPng();
			Write(name, Png(100, 100));
			WriteSidecar(name, contentType: "image/png", contentLength: "999999");

			var r = Assert.Single(Analyse(Cfg(maxBytes: 10_000_000)));
			Assert.Equal("ASSET_SIZE", r.Word);
			Assert.Equal("LENGTH_MISMATCH", r.SourceLabel);
		}

		[Fact]
		public void Analyse_DegenerateDimensions_FlagsDimensions()
		{
			Write(NewPng(), Png(1, 1));
			var r = Assert.Single(Analyse(Cfg(maxBytes: 10_000_000)));
			Assert.Equal("ASSET_DIMENSIONS", r.Word);
			Assert.Equal("DEGENERATE", r.SourceLabel);
		}

		[Fact]
		public void Analyse_OversizeDimensions_FlagsDimensions()
		{
			Write(NewPng(), Png(6000, 6000));
			var r = Assert.Single(Analyse(Cfg(maxBytes: 10_000_000, maxDim: 5000)));
			Assert.Equal("ASSET_DIMENSIONS", r.Word);
			Assert.Equal("OVERSIZE", r.SourceLabel);
		}

		[Fact]
		public void Analyse_ExcludedAsset_SkipsSizeAndDimensionChecks()
		{
			// Filename matches an exclusion substring → both size and dimension
			// checks are skipped even though the image is oversize on both axes.
			Write("press_kit.png", Png(6000, 6000));
			Assert.Empty(Analyse(Cfg(maxBytes: 10, maxDim: 5000, exclusions: new[] { "press" })));
		}

		[Fact]
		public void Analyse_UnregisteredFile_UrlFallsBackToFilename()
		{
			var name = NewPng();
			Write(name, Png(100, 100));
			var r = Assert.Single(Analyse(Cfg(maxBytes: 10)));
			Assert.Equal(name, r.Url);
		}

		[Fact]
		public void Analyse_RegisteredFile_UsesCacheUrlAndSource()
		{
			var name = NewPng();
			Write(name, Png(100, 100));
			RegisterUrl(name, "https://site.test/img.png", source: "discovery");

			var r = Assert.Single(Analyse(Cfg(maxBytes: 10)));
			Assert.Equal("https://site.test/img.png", r.Url);
			Assert.Equal("discovery", r.CrawlSource);
		}

		[Fact]
		public void Analyse_GifShortFile_FlagsDimensionsAndReadsHeader()
		{
			// 10-byte GIF: exercises ReadHead's short-read trim and the GIF
			// dimension path; 1x1 is degenerate.
			Write(NewGif(), Gif(1, 1));
			var r = Assert.Single(Analyse(Cfg(maxBytes: 10_000_000)));
			Assert.Equal("ASSET_DIMENSIONS", r.Word);
			Assert.Equal("DEGENERATE", r.SourceLabel);
		}

		// ── CheckSize / CheckDimensions directly ────────────────────────────

		[Fact]
		public void CheckSize_MismatchAndOversize_BothReported()
		{
			var name = NewPng();
			var file = Write(name, Png(100, 100));
			WriteSidecar(name, contentLength: "999999");

			var findings = AssetQuality
				.CheckSize(file, new FileInfo(file).Length, Cfg(maxBytes: 10))
				.ToList();

			Assert.Contains(findings, f => f.Detail == "LENGTH_MISMATCH");
			Assert.Contains(findings, f => f.Detail == "OVERSIZE");
		}

		[Theory]
		[InlineData(100, 100, null)]          // normal — no finding
		[InlineData(1, 1, "DEGENERATE")]
		[InlineData(6000, 6000, "OVERSIZE")]
		public void CheckDimensions_ClassifiesByDimension(int w, int h, string? expected)
		{
			var name = NewPng();
			var bytes = Png(w, h);
			var file = Write(name, bytes);
			var head = bytes.Take(24).ToArray();

			var findings = AssetQuality.CheckDimensions(file, head, Cfg(maxDim: 5000)).ToList();

			if (expected is null)
			{
				Assert.Empty(findings);
			}
			else
			{
				var f = Assert.Single(findings);
				Assert.Equal("DIMENSIONS", f.Word);
				Assert.Equal(expected, f.Detail);
			}
		}

		// ── ReadImageDimensions / sidecar readers ───────────────────────────

		[Fact]
		public void ReadImageDimensions_Jpeg_ReadsSofDimensions()
		{
			var name = NewJpg();
			var bytes = Jpeg(200, 100);
			var file = Write(name, bytes);

			var dims = AssetQuality.ReadImageDimensions(file, bytes.Take(24).ToArray());
			Assert.Equal((200, 100), dims);
		}

		[Fact]
		public void ReadImageDimensions_NonImageHead_ReturnsNull()
		{
			var head = Encoding.ASCII.GetBytes("not an image at all, just text!").Take(24).ToArray();
			Assert.Null(AssetQuality.ReadImageDimensions("nonexistent.bin", head));
		}

		[Fact]
		public void ReadSidecarContentLength_ParsedAbsentAndUnparseable()
		{
			var name = NewPng();
			var file = Write(name, Png(1, 1));

			Assert.Null(AssetQuality.ReadSidecarContentLength(file)); // no sidecar yet

			WriteSidecar(name, contentLength: "12345");
			Assert.Equal(12345L, AssetQuality.ReadSidecarContentLength(file));

			WriteSidecar(name, contentLength: "not-a-number");
			Assert.Null(AssetQuality.ReadSidecarContentLength(file));
		}

		[Fact]
		public void ReadSidecarContentType_StripsParametersAndHandlesAbsent()
		{
			var name = NewPng();
			var file = Write(name, Png(1, 1));

			Assert.Null(AssetQuality.ReadSidecarContentType(file)); // no sidecar

			WriteSidecar(name, contentType: "image/png; charset=utf-8");
			Assert.Equal("image/png", AssetQuality.ReadSidecarContentType(file));
		}

		// ── Batch B: ReadJpegDimensions marker scanning ─────────────────────
		// SOF carries height=100 (0x0064), width=200 (0x00C8).

		[Fact]
		public void ReadImageDimensions_Jpeg_SkipsNonSofSegment()
		{
			// APP0 segment (len 4 → 2 payload bytes) is skipped via Seek before SOF.
			var bytes = new byte[]
			{
				0xFF, 0xD8,                               // SOI
				0xFF, 0xE0, 0x00, 0x04, 0xAA, 0xBB,       // APP0, skipped
				0xFF, 0xC0, 0x00, 0x11, 0x08,             // SOF0
				0x00, 0x64, 0x00, 0xC8,                   // h=100, w=200
				0, 0, 0, 0,
			};
			var file = Write(NewJpg(), bytes);
			Assert.Equal((200, 100), AssetQuality.ReadImageDimensions(file, bytes.Take(24).ToArray()));
		}

		[Fact]
		public void ReadImageDimensions_Jpeg_SkipsStandaloneMarker()
		{
			// FF D0 (RST0) is a standalone marker (no length) → continue, then SOF.
			var bytes = new byte[]
			{
				0xFF, 0xD8,
				0xFF, 0xD0,                               // standalone RST0
				0xFF, 0xC0, 0x00, 0x11, 0x08,
				0x00, 0x64, 0x00, 0xC8,
				0, 0, 0, 0,
			};
			var file = Write(NewJpg(), bytes);
			Assert.Equal((200, 100), AssetQuality.ReadImageDimensions(file, bytes.Take(24).ToArray()));
		}

		[Fact]
		public void ReadImageDimensions_Jpeg_SkipsFillBytes()
		{
			// FF FF before the SOF code exercises the fill-byte skip loop.
			var bytes = new byte[]
			{
				0xFF, 0xD8,
				0xFF, 0xFF, 0xC0, 0x00, 0x11, 0x08,       // fill byte then SOF0
				0x00, 0x64, 0x00, 0xC8,
				0, 0, 0, 0,
			};
			var file = Write(NewJpg(), bytes);
			Assert.Equal((200, 100), AssetQuality.ReadImageDimensions(file, bytes.Take(24).ToArray()));
		}

		[Fact]
		public void ReadImageDimensions_Jpeg_SegmentLengthTooSmall_ReturnsNull()
		{
			var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x01 }; // len < 2
			var file = Write(NewJpg(), bytes);
			Assert.Null(AssetQuality.ReadImageDimensions(file, bytes.Take(24).ToArray()));
		}

		[Fact]
		public void ReadImageDimensions_Jpeg_EofAfterSoi_ReturnsNull()
		{
			var bytes = new byte[] { 0xFF, 0xD8, 0xFF }; // marker byte then EOF
			var file = Write(NewJpg(), bytes);
			Assert.Null(AssetQuality.ReadImageDimensions(file, bytes.Take(24).ToArray()));
		}

		// ── Batch B: CheckMetadata (EXIF leak detection) ────────────────────

		// Minimal JPEG carrying an APP1/EXIF block with IFD0 Make="X", Artist="Y"
		// (little-endian TIFF; both values inline ASCII). Hand-built; if
		// MetadataExtractor's parsing differs this is the fixture to revisit.
		private static byte[] ExifJpeg()
		{
			return new byte[]
			{
				0xFF, 0xD8,                               // SOI
				0xFF, 0xE1, 0x00, 0x2E,                   // APP1, length 46
				0x45, 0x78, 0x69, 0x66, 0x00, 0x00,       // "Exif\0\0"
				// TIFF header (little-endian)
				0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
				0x02, 0x00,                               // IFD0: 2 entries
				// Make (0x010F), ASCII, count 2, value "X\0" inline
				0x0F, 0x01, 0x02, 0x00, 0x02, 0x00, 0x00, 0x00, 0x58, 0x00, 0x00, 0x00,
				// Artist (0x013B), ASCII, count 2, value "Y\0" inline
				0x3B, 0x01, 0x02, 0x00, 0x02, 0x00, 0x00, 0x00, 0x59, 0x00, 0x00, 0x00,
				0x00, 0x00, 0x00, 0x00,                   // next IFD = 0
				0xFF, 0xD9,                               // EOI
			};
		}

		[Fact]
		public void Analyse_ExifMetadataLeak_FlagsAssetMetadata()
		{
			Write(NewJpg(), ExifJpeg());

			// Metadata only — size/dimension checks off so the lone finding is the leak.
			var r = Assert.Single(Analyse(Cfg(metadata: true, size: false, dims: false)));
			Assert.Equal("ASSET_METADATA", r.Word);
			Assert.Contains("Make", r.SourceLabel);
			Assert.Contains("Artist", r.SourceLabel);

			// Excerpt is now a presence summary, not the raw values: Make -> "camera",
			// Artist -> "author". The values themselves moved to the asset log.
			Assert.Contains("camera", r.Excerpt);
			Assert.Contains("author", r.Excerpt);

			// The leaked values surface in the asset log's Exif column (Make=X; Artist=Y).
			var logLine = Assert.Single(File.ReadAllLines(_log), l => l.Contains("ASSET_METADATA"));
			Assert.Contains("Make=X", logLine);
			Assert.Contains("Artist=Y", logLine);
		}

		[Fact]
		public void Analyse_MetadataNoLeak_NoFinding()
		{
			// Plain JPEG with no EXIF leak fields → CheckMetadata yields nothing.
			Write(NewJpg(), Jpeg(200, 100));
			Assert.Empty(Analyse(Cfg(metadata: true, size: false, dims: false)));
		}
	}
}
