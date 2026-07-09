using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for Base64AssetExtractor's ResolveExtension fallback arms and the
	/// ProcessFile padding / decode-failure / absent-media-type branches that the
	/// main Base64AssetExtractorTests don't reach (it uses only image/png and
	/// application/octet-stream, both direct dictionary hits, with 4-aligned
	/// payloads).
	///
	/// Payloads are valid Base64 built from repeated "QUJD", truncated to a target
	/// length so the length-mod-4 remainder exercises the padding/decode paths.
	/// SYNTHETIC fixtures. In the Logger collection: Extract / CrawlIndex / FileIo
	/// write via the static Logger. The three I/O catch blocks in ProcessFile
	/// (unreadable / unwritable files) are environment-dependent and left uncovered.
	/// </summary>
	[Collection("Logger")]
	public class Base64AssetExtractorResolveTests : IDisposable
	{
		private readonly string _root;
		private readonly string _downloadDir;
		private readonly string _assetsDir;
		private readonly string _logPath;

		public Base64AssetExtractorResolveTests()
		{
			_root = Path.Combine(Path.GetTempPath(), $"b64ext-{Guid.NewGuid():N}");
			_downloadDir = Path.Combine(_root, "download");
			_assetsDir = Path.Combine(_root, "base64assets");
			Directory.CreateDirectory(_downloadDir);
			_logPath = Path.Combine(_root, "19-base64assets.log");
			Logger.Initialize(Path.Combine(_root, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_root, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── helpers ─────────────────────────────────────────────────────────

		private void WriteAsset(string name, string content) =>
			File.WriteAllText(Path.Combine(_downloadDir, name), content, Encoding.Latin1);

		// Valid Base64 of exactly `len` characters (repeated "QUJD", truncated).
		private static string B64(int len)
		{
			var sb = new StringBuilder();
			while (sb.Length < len)
			{
				sb.Append("QUJD");
			}

			return sb.ToString().Substring(0, len);
		}

		private void Extract() =>
			Base64AssetExtractor.Extract(_downloadDir, _assetsDir, _logPath, new[] { ".css" });

		private string[] Assets(string searchPattern) =>
			Directory.GetFiles(_assetsDir, searchPattern, SearchOption.TopDirectoryOnly);

		// ── tests ───────────────────────────────────────────────────────────

		[Fact]
		public void ResolveExtension_FallbackMediaTypes_MapToExpectedExtensions()
		{
			var p = B64(64);
			WriteAsset("fallback.css", string.Join(" ",
				$"url(data:application/test+xml;base64,{p})", // +xml  → .xml
				$"url(data:text/test;base64,{p})",            // text/ → .txt
				$"url(data:image/test;base64,{p})",           // image/→ .bin
				$"url(data:font/test;base64,{p})",            // font/ → .bin
				$"url(data:application/test;base64,{p})",     // default → .bin
				$"url(data:;base64,{p})"));                   // no media type → .bin

			Extract();

			Assert.NotEmpty(Assets("*.xml"));
			Assert.NotEmpty(Assets("*.txt"));
			Assert.NotEmpty(Assets("*.bin")); // the image/font/default/empty arms
		}

		[Fact]
		public void ProcessFile_PaddingRemainderTwo_DecodesAndSaves()
		{
			WriteAsset("rem2.css", $"url(data:image/png;base64,{B64(66)})"); // 66 % 4 == 2

			Extract();

			Assert.NotEmpty(Assets("*.png"));
		}

		[Fact]
		public void ProcessFile_PaddingRemainderThree_DecodesAndSaves()
		{
			WriteAsset("rem3.css", $"url(data:image/png;base64,{B64(67)})"); // 67 % 4 == 3

			Extract();

			Assert.NotEmpty(Assets("*.png"));
		}

		[Fact]
		public void ProcessFile_InvalidBase64Length_IsSkipped()
		{
			WriteAsset("bad.css", $"url(data:image/png;base64,{B64(65)})"); // 65 % 4 == 1 → no pad → throws

			Extract();

			Assert.Empty(Assets("*.png"));
		}

		[Fact]
		public void ProcessFile_AbsentMediaType_SavedAsBin_AndRowRecordsUnknown()
		{
			WriteAsset("nomt.css", $"url(data:;base64,{B64(64)})");

			Extract();

			Assert.NotEmpty(Assets("*.bin"));
			var log = File.ReadAllText(_logPath);
			Assert.Contains("unknown", log); // mediaType ?? "unknown" in the row
		}
	}
}
