using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for SEO.ExtractDataAsync — verifies the per-file HTML field
	/// extraction (title / description / robots / keywords / h1 count), the
	/// filename → url/source lookup via UrlCache, the delimited output shape
	/// (header row, "@@@" delimiter, UTF-8 BOM) and the byte-level encoding
	/// detection path.
	///
	/// In the Logger collection: ExtractDataAsync reaches UrlCache lookups that
	/// log via the static Logger singleton, so these must not run in parallel
	/// with other Logger-touching classes.
	///
	/// UrlCache is process-wide static with no reset, so every test uses a
	/// GUID-unique source filename; lookups are keyed by filename and therefore
	/// isolated from any entries other tests may have loaded.
	/// </summary>
	[Collection("Logger")]
	public class SeoExtractDataTests : IDisposable
	{
		private const string Delimiter = "@@@";

		private readonly string _tempDir;

		public SeoExtractDataTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"SeoTests_{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── helpers ───────────────────────────────────────────────────────────

		// Unique HTML filename so the UrlCache lookup is deterministic.
		private static string UniqueHtmlName() => $"page_{Guid.NewGuid():N}.html";

		private string WriteHtml(string filename, string html)
		{
			var path = Path.Combine(_tempDir, filename);
			File.WriteAllText(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			return path;
		}

		private string WriteBytes(string filename, byte[] bytes)
		{
			var path = Path.Combine(_tempDir, filename);
			File.WriteAllBytes(path, bytes);
			return path;
		}

		// Output lives in the temp dir but with an extension the "*.html" pattern
		// never matches, so it is not re-read as an input.
		private string OutputPath() => Path.Combine(_tempDir, $"seo_{Guid.NewGuid():N}.out");

		private static string Doc(
			string? title = null,
			string? description = null,
			string? robots = null,
			string? keywords = null,
			int h1 = 0)
		{
			var head = new StringBuilder();
			if (title != null) head.Append($"<title>{title}</title>");
			if (description != null) head.Append($"<meta name=\"description\" content=\"{description}\">");
			if (robots != null) head.Append($"<meta name=\"robots\" content=\"{robots}\">");
			if (keywords != null) head.Append($"<meta name=\"keywords\" content=\"{keywords}\">");
			var body = new StringBuilder();
			for (int i = 0; i < h1; i++) body.Append("<h1>H</h1>");
			return $"<html><head>{head}</head><body>{body}</body></html>";
		}

		private static string[] Lines(string outputPath) =>
			File.ReadAllLines(outputPath); // UTF-8 read strips the BOM

		private static string[] Cols(string line) => line.Split(Delimiter);

		private static string[] DataRows(string outputPath) =>
			Lines(outputPath).Skip(1).ToArray();

		private async Task<string[]> RunSingleAsync(string html, string filename)
		{
			WriteHtml(filename, html);
			var output = OutputPath();
			await SEO.ExtractDataAsync(_tempDir, output, "*.html");
			return Lines(output);
		}

		// ── header ──────────────────────────────────────────────────────────

		[Fact]
		public async Task Header_WrittenFirst_WithExpectedColumns()
		{
			var lines = await RunSingleAsync(Doc(title: "T"), UniqueHtmlName());

			var header = Cols(lines[0]);
			Assert.Equal(
				new[]
				{
					"url", "robotsValue", "titleValue", "titleLength",
					"descriptionValue", "descriptionLength", "keywordsValue",
					"source", "h1Count"
				},
				header);
		}

		// ── field extraction ───────────────────────────────────────────────

		[Fact]
		public async Task AllFieldsPopulated_ParsedWithLengths()
		{
			var html = Doc(
				title: "Hello World",
				description: "Desc here",
				robots: "noindex",
				keywords: "a,b,c",
				h1: 1);

			var rows = (await RunSingleAsync(html, UniqueHtmlName())).Skip(1).ToArray();
			var c = Cols(Assert.Single(rows));

			Assert.Equal("noindex", c[1]);       // robots
			Assert.Equal("Hello World", c[2]);   // title
			Assert.Equal("11", c[3]);            // title length
			Assert.Equal("Desc here", c[4]);     // description
			Assert.Equal("9", c[5]);             // description length
			Assert.Equal("a,b,c", c[6]);         // keywords
			Assert.Equal("1", c[8]);             // h1 count
		}

		[Fact]
		public async Task MissingMetaNodes_EmptyStringsAndZeroLengths()
		{
			var rows = (await RunSingleAsync(Doc(title: "T"), UniqueHtmlName())).Skip(1).ToArray();
			var c = Cols(Assert.Single(rows));

			Assert.Equal("T", c[2]);   // title still parsed
			Assert.Equal("1", c[3]);
			Assert.Equal("", c[1]);    // robots absent
			Assert.Equal("", c[4]);    // description absent
			Assert.Equal("0", c[5]);   // description length
			Assert.Equal("", c[6]);    // keywords absent
			Assert.Equal("0", c[8]);   // h1 count
		}

		[Fact]
		public async Task NoTitleNode_EmptyTitleAndZeroLength()
		{
			// Document without a <title> element at all: SelectSingleNode("//title")
			// returns null, so titleValue falls through to string.Empty. (An empty
			// <title></title> would not exercise this — InnerText is "" not null.)
			var rows = (await RunSingleAsync(Doc(h1: 1), UniqueHtmlName())).Skip(1).ToArray();
			var c = Cols(Assert.Single(rows));

			Assert.Equal("", c[2]);   // title
			Assert.Equal("0", c[3]);  // title length
		}

		[Theory]
		[InlineData(0, "0")]
		[InlineData(1, "1")]
		[InlineData(3, "3")]
		public async Task H1Count_ReflectsNumberOfH1Elements(int count, string expected)
		{
			var rows = (await RunSingleAsync(Doc(title: "T", h1: count), UniqueHtmlName())).Skip(1).ToArray();
			var c = Cols(Assert.Single(rows));
			Assert.Equal(expected, c[8]);
		}

		// ── url / source lookup ─────────────────────────────────────────────

		[Fact]
		public async Task UrlAndSource_PopulatedFromCache()
		{
			var filename = UniqueHtmlName();
			var url = $"https://example.test/{Guid.NewGuid():N}";

			var lookup = Path.Combine(_tempDir, $"lookup_{Guid.NewGuid():N}.log");
			File.WriteAllLines(lookup, new[] { $"{filename}|{url}|discovery" }, Encoding.UTF8);
			UrlCache.LoadCache(lookup);

			var rows = (await RunSingleAsync(Doc(title: "T"), filename)).Skip(1).ToArray();
			var c = Cols(Assert.Single(rows));

			Assert.Equal(url, c[0]);          // url
			Assert.Equal("discovery", c[7]);  // source
		}

		[Fact]
		public async Task UrlAndSource_AbsentFromCache_FallBackToErrorAndEmpty()
		{
			// Fresh GUID filename never loaded into the cache.
			var rows = (await RunSingleAsync(Doc(title: "T"), UniqueHtmlName())).Skip(1).ToArray();
			var c = Cols(Assert.Single(rows));

			Assert.Equal("error", c[0]);  // url not found
			Assert.Equal("", c[7]);       // source not recorded
		}

		// ── multiple files & pattern ────────────────────────────────────────

		[Fact]
		public async Task MultipleFiles_OneDataRowEach()
		{
			WriteHtml(UniqueHtmlName(), Doc(title: "Alpha"));
			WriteHtml(UniqueHtmlName(), Doc(title: "Beta"));

			var output = OutputPath();
			await SEO.ExtractDataAsync(_tempDir, output, "*.html");

			var rows = DataRows(output);
			Assert.Equal(2, rows.Length);

			var titles = rows.Select(r => Cols(r)[2]).ToHashSet();
			Assert.Contains("Alpha", titles);
			Assert.Contains("Beta", titles);
		}

		[Fact]
		public async Task FilePattern_OnlyMatchingFilesProcessed()
		{
			WriteHtml(UniqueHtmlName(), Doc(title: "Included"));
			// Non-matching extension: a .txt file that happens to contain markup.
			WriteHtml($"note_{Guid.NewGuid():N}.txt", Doc(title: "Excluded"));

			var output = OutputPath();
			await SEO.ExtractDataAsync(_tempDir, output, "*.html");

			var rows = DataRows(output);
			var row = Cols(Assert.Single(rows));
			Assert.Equal("Included", row[2]);
		}

		// ── output encoding & sanitization ──────────────────────────────────

		[Fact]
		public async Task Output_StartsWithUtf8Bom()
		{
			WriteHtml(UniqueHtmlName(), Doc(title: "T"));
			var output = OutputPath();
			await SEO.ExtractDataAsync(_tempDir, output, "*.html");

			var bytes = File.ReadAllBytes(output);
			Assert.True(bytes.Length >= 3);
			Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
		}

		[Fact]
		public async Task FieldWithLineBreak_StaysSingleRecord()
		{
			// A title carrying an embedded newline must not split the record;
			// the line-break is sanitized away by the composition path.
			var html = Doc(title: "Line1\nLine2");
			var lines = await RunSingleAsync(html, UniqueHtmlName());

			Assert.Equal(2, lines.Length); // header + exactly one record
			var title = Cols(lines[1])[2];
			Assert.DoesNotContain('\n', title);
			Assert.DoesNotContain('\r', title);
		}

		[Fact]
		public async Task Utf16LeEncodedFile_TitleDecodedCorrectly()
		{
			// FF FE BOM → DetectEncoding returns UTF-16 LE; non-ASCII title must
			// round-trip through the byte-level detection path.
			const string title = "Über Café";
			var html = Doc(title: title);

			var bytes = Encoding.Unicode.GetPreamble()
				.Concat(Encoding.Unicode.GetBytes(html))
				.ToArray();
			WriteBytes(UniqueHtmlName(), bytes);

			var output = OutputPath();
			await SEO.ExtractDataAsync(_tempDir, output, "*.html");

			var row = Cols(Assert.Single(DataRows(output)));
			Assert.Equal(title, row[2]);
		}
	}
}
