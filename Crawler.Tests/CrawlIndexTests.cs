using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for CrawlIndex.CreateLookupFile — verifies that both "saved" (HTML page)
	/// and "savedAsset" (CSS, image, PDF) log entries are included in the index
	/// with their source column ("discovery" or "list") correctly propagated.
	///
	/// The source column on asset entries was absent before this fix — assets were
	/// always written without source regardless of which crawl pass triggered them.
	/// </summary>
	[Collection("Logger")]
	public class CrawlIndexTests : IDisposable
	{
		private readonly string _tempDir;

		public CrawlIndexTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"LookupTests_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		private string WriteLog(string filename, params string[] lines)
		{
			var path = Path.Combine(_tempDir, filename);
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		private string LookupPath() =>
			Path.Combine(_tempDir, $"lookup_{Guid.NewGuid():N}.log");

		// ── HTML pages (saved) ────────────────────────────────────────────────────

		[Fact]
		public void CreateLookupFile_SavedDiscovery_IncludedWithSource()
		{
			var log = WriteLog("crawler.log",
				"2026-01-01 12:00:00 | https://example.com/page.html | saved | abc123page.html | discovery");
			var lookup = LookupPath();

			CrawlIndex.CreateLookupFile(log, lookup);

			var lines = File.ReadAllLines(lookup);
			Assert.Contains(lines, l => l.Contains("abc123page.html") && l.Contains("discovery"));
		}

		[Fact]
		public void CreateLookupFile_SavedList_IncludedWithSource()
		{
			var log = WriteLog("crawler.log",
				"2026-01-01 12:00:00 | https://example.com/page.html | saved | abc123page.html | list");
			var lookup = LookupPath();

			CrawlIndex.CreateLookupFile(log, lookup);

			var lines = File.ReadAllLines(lookup);
			Assert.Contains(lines, l => l.Contains("abc123page.html") && l.Contains("list"));
		}

		[Fact]
		public void CreateLookupFile_SavedNoSource_IncludedWithoutSourceColumn()
		{
			// Old-format log lines without source column still load cleanly.
			var log = WriteLog("crawler.log",
				"2026-01-01 12:00:00 | https://example.com/page.html | saved | abc123page.html");
			var lookup = LookupPath();

			CrawlIndex.CreateLookupFile(log, lookup);

			var lines = File.ReadAllLines(lookup);
			Assert.Contains(lines, l => l.Contains("abc123page.html"));
		}

		// ── Assets (savedAsset) ───────────────────────────────────────────────────

		[Fact]
		public void CreateLookupFile_SavedAssetDiscovery_IncludedWithSource()
		{
			var log = WriteLog("crawler.log",
				"2026-01-01 12:00:00 | https://example.com/style.css | savedAsset | abc123style.css | discovery");
			var lookup = LookupPath();

			CrawlIndex.CreateLookupFile(log, lookup);

			var lines = File.ReadAllLines(lookup);
			Assert.Contains(lines, l => l.Contains("abc123style.css") && l.Contains("discovery"));
		}

		[Fact]
		public void CreateLookupFile_SavedAssetList_IncludedWithSource()
		{
			// Asset downloaded during the post-crawl list pass — source must be "list".
			var log = WriteLog("crawler.log",
				"2026-01-01 12:00:00 | https://example.com/image.png | savedAsset | abc123image.png | list");
			var lookup = LookupPath();

			CrawlIndex.CreateLookupFile(log, lookup);

			var lines = File.ReadAllLines(lookup);
			Assert.Contains(lines, l => l.Contains("abc123image.png") && l.Contains("list"));
		}

		[Fact]
		public void CreateLookupFile_SavedAssetNoSource_IncludedWithoutSourceColumn()
		{
			// Old-format asset log lines without source still load cleanly.
			var log = WriteLog("crawler.log",
				"2026-01-01 12:00:00 | https://example.com/style.css | savedAsset | abc123style.css");
			var lookup = LookupPath();

			CrawlIndex.CreateLookupFile(log, lookup);

			var lines = File.ReadAllLines(lookup);
			Assert.Contains(lines, l => l.Contains("abc123style.css"));
		}

		// ── Mixed log ─────────────────────────────────────────────────────────────

		[Fact]
		public void CreateLookupFile_MixedLog_AllEntriesIncluded()
		{
			var log = WriteLog("crawler.log",
				"2026-01-01 12:00:00 | https://example.com/page.html | saved | abc123page.html | discovery",
				"2026-01-01 12:00:01 | https://example.com/style.css | savedAsset | abc123style.css | discovery",
				"2026-01-01 12:00:02 | https://example.com/list.html | saved | abc123list.html | list",
				"2026-01-01 12:00:03 | https://example.com/list.css  | savedAsset | abc123list.css | list",
				"2026-01-01 12:00:04 | https://example.com/page.html | crawled | info");
			var lookup = LookupPath();

			CrawlIndex.CreateLookupFile(log, lookup);

			var lines = File.ReadAllLines(lookup);
			Assert.Contains(lines, l => l.Contains("abc123page.html") && l.Contains("discovery"));
			Assert.Contains(lines, l => l.Contains("abc123style.css") && l.Contains("discovery"));
			Assert.Contains(lines, l => l.Contains("abc123list.html") && l.Contains("list"));
			Assert.Contains(lines, l => l.Contains("abc123list.css") && l.Contains("list"));
			// Non-saved lines must not appear
			Assert.DoesNotContain(lines, l => l.Contains("crawled"));
		}

	}
}
