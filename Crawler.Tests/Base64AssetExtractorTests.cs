using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for Base64AssetExtractor.Extract — scans downloaded asset files for
	/// embedded Base64 data URIs, decodes them to disk, writes a @@@-delimited
	/// log, and promotes oversized assets to IssueTracking records.
	///
	/// All private logic (the data-URI regex, padding repair, WASM magic-byte
	/// detection, media-type → extension mapping, MinEncodedLength filtering) is
	/// exercised through the public Extract entry point using temp directories.
	/// Placed in the Logger collection because Extract logs progress through the
	/// static Logger.
	/// </summary>
	[Collection("Logger")]
	public class Base64AssetExtractorTests : IDisposable
	{
		private readonly string _root;
		private readonly string _downloadDir;
		private readonly string _assetsDir;
		private readonly string _logPath;

		public Base64AssetExtractorTests()
		{
			_root = Path.Combine(Path.GetTempPath(), $"b64-extract-{Guid.NewGuid():N}");
			_downloadDir = Path.Combine(_root, "download");
			_assetsDir = Path.Combine(_root, "base64assets");
			_logPath = Path.Combine(_root, "19-base64assets.log");
			Directory.CreateDirectory(_downloadDir);
		}

		public void Dispose()
		{
			try { Directory.Delete(_root, recursive: true); } catch { }
		}

		private void WriteAsset(string name, string content)
			=> File.WriteAllText(Path.Combine(_downloadDir, name), content, Encoding.Latin1);

		private List<string> ReadLog()
			=> File.Exists(_logPath)
				? new List<string>(File.ReadAllLines(_logPath, Encoding.UTF8))
				: new List<string>();

		// A 1x1 transparent PNG, base64-encoded (>64 chars so it clears MinEncodedLength).
		private const string PngB64 =
			"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

		// ── Missing / empty inputs ────────────────────────────────────────────

		[Fact]
		public void MissingDownloadDirectory_ReturnsEmpty_NoThrow()
		{
			var issues = Base64AssetExtractor.Extract(
				Path.Combine(_root, "does-not-exist"), _assetsDir, _logPath, new[] { ".css" });
			Assert.Empty(issues);
		}

		[Fact]
		public void NoConfiguredExtensions_ReturnsEmpty()
		{
			WriteAsset("a.css", $"a{{background:url(data:image/png;base64,{PngB64})}}");
			var issues = Base64AssetExtractor.Extract(
				_downloadDir, _assetsDir, _logPath, Array.Empty<string>());
			Assert.Empty(issues);
		}

		[Fact]
		public void NoMatchingFiles_WritesHeaderOnlyLog()
		{
			WriteAsset("a.txt", "no assets here"); // .txt not in the extension set
			Base64AssetExtractor.Extract(_downloadDir, _assetsDir, _logPath, new[] { ".css" });

			var log = ReadLog();
			Assert.Single(log); // header row only
			Assert.StartsWith("SourceFileUrl@@@", log[0]);
		}

		// ── Happy-path extraction ─────────────────────────────────────────────

		[Fact]
		public void EmbeddedPng_IsDecodedAndSavedWithPngExtension()
		{
			WriteAsset("style.css", $"a{{background:url(data:image/png;base64,{PngB64})}}");
			Base64AssetExtractor.Extract(_downloadDir, _assetsDir, _logPath, new[] { ".css" });

			var saved = Directory.GetFiles(_assetsDir, "*.png");
			Assert.Single(saved);
			// File starts with the PNG magic number.
			var bytes = File.ReadAllBytes(saved[0]);
			Assert.True(bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50);
		}

		[Fact]
		public void ExtractedAsset_IsRecordedInLog()
		{
			WriteAsset("style.css", $"a{{background:url(data:image/png;base64,{PngB64})}}");
			Base64AssetExtractor.Extract(_downloadDir, _assetsDir, _logPath, new[] { ".css" });

			var log = ReadLog();
			Assert.Equal(2, log.Count); // header + 1 row
			Assert.Contains("image/png", log[1]);
			Assert.Contains("@@@", log[1]);
		}

		[Fact]
		public void StrippedSource_IsWrittenWithPlaceholder()
		{
			WriteAsset("style.css", $"a{{background:url(data:image/png;base64,{PngB64})}}");
			Base64AssetExtractor.Extract(_downloadDir, _assetsDir, _logPath, new[] { ".css" });

			var strippedPath = Path.Combine(_assetsDir, "sourcesstripped", "style.css");
			Assert.True(File.Exists(strippedPath));
			var stripped = File.ReadAllText(strippedPath, Encoding.Latin1);
			Assert.Contains("BASE64_STRIPPED:", stripped);
			Assert.DoesNotContain(PngB64, stripped);
		}

		// ── Filtering rules ───────────────────────────────────────────────────

		[Fact]
		public void NonBase64DataUri_IsSkipped()
		{
			// data URI without ;base64 — should not be extracted.
			WriteAsset("style.css", "a{content:url(data:image/svg+xml,<svg></svg>)}");
			Base64AssetExtractor.Extract(_downloadDir, _assetsDir, _logPath, new[] { ".css" });

			var log = ReadLog();
			Assert.Single(log); // header only, nothing extracted
		}

		[Fact]
		public void ShortPayloadBelowMinLength_IsSkipped()
		{
			// "QUJD" decodes to "ABC" — well under the 64-char MinEncodedLength.
			WriteAsset("style.css", "a{background:url(data:image/png;base64,QUJD)}");
			Base64AssetExtractor.Extract(_downloadDir, _assetsDir, _logPath, new[] { ".css" });

			Assert.Single(ReadLog());
		}

		[Fact]
		public void ExtensionMatching_IsCaseInsensitive_AndDotOptional()
		{
			WriteAsset("style.CSS", $"a{{background:url(data:image/png;base64,{PngB64})}}");
			// Pass extension without a leading dot and in lower case.
			Base64AssetExtractor.Extract(_downloadDir, _assetsDir, _logPath, new[] { "css" });

			Assert.Equal(2, ReadLog().Count);
		}

		// ── Large-asset promotion ─────────────────────────────────────────────

		[Fact]
		public void AssetAboveThreshold_IsPromotedToIssue()
		{
			// Build a ~3KB payload and set a 1KB threshold.
			var bigBytes = new byte[3000];
			new Random(1).NextBytes(bigBytes);
			var bigB64 = Convert.ToBase64String(bigBytes);
			WriteAsset("style.css", $"a{{background:url(data:application/octet-stream;base64,{bigB64})}}");

			var issues = Base64AssetExtractor.Extract(
				_downloadDir, _assetsDir, _logPath, new[] { ".css" },
				largeAssetThresholdBytes: 1024);

			Assert.Single(issues);
			Assert.Equal("BASE64_LARGE_ASSET", issues[0].Word);
			Assert.Equal("QUALITY", issues[0].Type);
		}

		[Fact]
		public void AssetBelowThreshold_IsNotPromoted()
		{
			WriteAsset("style.css", $"a{{background:url(data:image/png;base64,{PngB64})}}");
			var issues = Base64AssetExtractor.Extract(
				_downloadDir, _assetsDir, _logPath, new[] { ".css" },
				largeAssetThresholdBytes: 1_000_000);

			Assert.Empty(issues);
		}

		// ── WASM magic-byte detection ─────────────────────────────────────────

		[Fact]
		public void WasmMagicBytes_OverrideDeclaredMediaType()
		{
			// \0asm + version + padding so it clears MinEncodedLength.
			var wasm = new byte[64];
			wasm[0] = 0x00; wasm[1] = 0x61; wasm[2] = 0x73; wasm[3] = 0x6D;
			var wasmB64 = Convert.ToBase64String(wasm);
			// Declared as octet-stream, but magic bytes should force .wasm.
			WriteAsset("app.js", $"const w='data:application/octet-stream;base64,{wasmB64}';");

			Base64AssetExtractor.Extract(_downloadDir, _assetsDir, _logPath, new[] { ".js" });

			Assert.Single(Directory.GetFiles(_assetsDir, "*.wasm"));
		}
	}
}
