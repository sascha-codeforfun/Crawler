using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for Content.CompareCrawlAndContent — particularly that the index
	/// column count does not affect whether crawled pages are correctly excluded
	/// from log 05. A strict parts.Length == N check would silently empty the
	/// crawl set whenever a new column is added to the index format, causing
	/// every CMS page to appear in log 05 as "not directly crawlable".
	/// </summary>
	[Collection("Logger")]
	public class ContentTests : IDisposable
	{
		private readonly string _tempDir;

		public ContentTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"ContentTests_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── Helpers ──────────────────────────────────────────────────────────────

		private string WriteIndex(string filename, params string[] lines)
		{
			var path = Path.Combine(_tempDir, filename);
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		private string WriteContentList(string filename, params string[] paths)
		{
			var path = Path.Combine(_tempDir, filename);
			File.WriteAllLines(path, paths, Encoding.UTF8);
			return path;
		}

		private string OutputPath() =>
			Path.Combine(_tempDir, $"out_{Guid.NewGuid():N}.log");

		// ── 2-column index (old format) ───────────────────────────────────────────

		[Fact]
		public void CompareCrawlAndContent_TwoColumnIndex_CrawledPageNotInOutput()
		{
			var index = WriteIndex("index2.log",
				"abc.html|https://example.com/page-a.html");
			var content = WriteContentList("content.log", "/page-a.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.DoesNotContain("/page-a.html", result);
		}

		// ── 3-column index (current format — filename|url|source) ─────────────────

		[Fact]
		public void CompareCrawlAndContent_ThreeColumnIndex_CrawledPageNotInOutput()
		{
			var index = WriteIndex("index3.log",
				"abc.html|https://example.com/page-a.html|discovery");
			var content = WriteContentList("content.log", "/page-a.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.DoesNotContain("/page-a.html", result);
		}

		[Fact]
		public void CompareCrawlAndContent_ThreeColumnIndex_UncrawledPageInOutput()
		{
			var index = WriteIndex("index3.log",
				"abc.html|https://example.com/page-a.html|discovery");
			var content = WriteContentList("content.log", "/page-a.html", "/page-b.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.Contains("/page-b.html", result);
		}

		[Fact]
		public void CompareCrawlAndContent_ThreeColumnIndex_DoesNotTreatAllPagesAsMissing()
		{
			// Regression guard: parts.Length == 2 would empty crawlEntries when the
			// index has 3 columns, causing every CMS page to appear in log 05.
			var index = WriteIndex("index3.log",
				"a.html|https://example.com/page-a.html|discovery",
				"b.html|https://example.com/page-b.html|discovery",
				"c.html|https://example.com/page-c.html|list");
			var content = WriteContentList("content.log",
				"/page-a.html", "/page-b.html", "/page-c.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.DoesNotContain(result, l => l.Length > 0);
		}

		// ── 4-column index (future proofing) ──────────────────────────────────────

		[Fact]
		public void CompareCrawlAndContent_FourColumnIndex_CrawledPageNotInOutput()
		{
			// If a fourth column is ever added to the index, the >= 2 check must
			// still correctly populate crawlEntries. This test will catch any
			// regression to a strict equality check.
			var index = WriteIndex("index4.log",
				"abc.html|https://example.com/page-a.html|discovery|extra-future-column");
			var content = WriteContentList("content.log", "/page-a.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.DoesNotContain("/page-a.html", result);
		}

		// ── Content.Listing ────────────────────────────────────────────────────────

		[Fact]
		public void Listing_ValidCsv_FiltersAndWritesMatchingRows()
		{
			var csvPath = Path.Combine(_tempDir, "listing.csv");
			var outPath = OutputPath();
			File.WriteAllLines(csvPath, [
				"/content/mysite/de/home/page-a.html",
				"/content/mysite/de/home/page-b.html",
				"/other/path/not-matching.html",
			], System.Text.Encoding.UTF8);

			// rowNegativeFilter must be a non-matching pattern — empty string
			// would cause line.Contains("") == true, excluding everything.
			Content.Listing(outPath, csvPath,
				rowMustContain: "/content/mysite",
				columnDelimiter: ";",
				columnPosition: 0,
				replacePrefix: false,
				addSuffix: "",
				exclusions: [],
				rowNegativeFilter: "NOMATCH_SENTINEL",
				silent: true);

			var result = File.ReadAllLines(outPath);
			Assert.Equal(2, result.Length);
			Assert.Contains("/content/mysite/de/home/page-a.html", result);
			Assert.Contains("/content/mysite/de/home/page-b.html", result);
			Assert.DoesNotContain("/other/path/not-matching.html", result);
		}

		[Fact]
		public void Listing_MissingCsv_SilentMode_WritesNoOutput()
		{
			var csvPath = Path.Combine(_tempDir, "nonexistent.csv");
			var outPath = OutputPath();

			Content.Listing(outPath, csvPath,
				rowMustContain: "/content",
				columnDelimiter: ";",
				columnPosition: 0,
				replacePrefix: false,
				addSuffix: "",
				exclusions: [],
				rowNegativeFilter: "",
				silent: true);

			// Output file should not have been created
			Assert.False(File.Exists(outPath));
		}

		[Fact]
		public void Listing_NegativeFilter_ExcludesMatchingRows()
		{
			var csvPath = Path.Combine(_tempDir, "listing2.csv");
			var outPath = OutputPath();
			File.WriteAllLines(csvPath, [
				"/content/mysite/de/home/page-a.html",
				"/content/mysite/de/home/excluded.html",
			], System.Text.Encoding.UTF8);

			Content.Listing(outPath, csvPath,
				rowMustContain: "/content/mysite",
				columnDelimiter: ";",
				columnPosition: 0,
				replacePrefix: false,
				addSuffix: "",
				exclusions: [],
				rowNegativeFilter: "excluded",
				silent: true);

			var result = File.ReadAllLines(outPath);
			Assert.Single(result);
			Assert.Contains("/content/mysite/de/home/page-a.html", result);
		}

		// ── Mixed crawled and uncrawled ───────────────────────────────────────────

		[Fact]
		public void CompareCrawlAndContent_MixedPages_OnlyMissingInOutput()
		{
			var index = WriteIndex("index3.log",
				"a.html|https://example.com/crawled.html|discovery");
			var content = WriteContentList("content.log",
				"/crawled.html", "/missing.html");
			var output = OutputPath();

			Content.CompareCrawlAndContent(index, content, output, "https://example.com");

			var result = File.ReadAllLines(output);
			Assert.DoesNotContain("/crawled.html", result);
			Assert.Contains("/missing.html", result);
		}
	}
}
