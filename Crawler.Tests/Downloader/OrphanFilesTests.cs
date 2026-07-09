using System.Text;
using Xunit;
using Crawler.Downloader;

namespace Crawler.Tests.Downloader
{
	/// <summary>
	/// Unit tests for OrphanFiles.Detect — the pure orphan-detection helper
	/// extracted from Program.CrawlerIndexIntegrityCheck in fileset #271.
	/// </summary>
	public class OrphanFilesTests : IDisposable
	{
		private readonly string _tempDir;

		public OrphanFilesTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"orphan-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private string CreateDownloadDir()
		{
			var dir = Path.Combine(_tempDir, "download");
			Directory.CreateDirectory(dir);
			return dir;
		}

		private string CreateIndex(params string[] filenames)
		{
			// Index format: "filename | url | source"  — Detect only reads first column
			var path = Path.Combine(_tempDir, "02-crawler-index.log");
			var lines = filenames.Select(f => $"{f} | https://example.com/{f} | discovery");
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		private static void WriteFile(string dir, string name) =>
			File.WriteAllText(Path.Combine(dir, name), "<html/>", Encoding.UTF8);

		// ── Empty / missing inputs ────────────────────────────────────────────

		[Fact]
		public void Detect_MissingDownloadDirectory_ReturnsEmpty()
		{
			var index = CreateIndex("a.html");
			var orphans = OrphanFiles.Detect(
				Path.Combine(_tempDir, "no-such-dir"), index, "*.html");
			Assert.Empty(orphans);
		}

		[Fact]
		public void Detect_MissingIndexFile_ReturnsEmpty()
		{
			var dir = CreateDownloadDir();
			WriteFile(dir, "a.html");
			var orphans = OrphanFiles.Detect(
				dir, Path.Combine(_tempDir, "no-such-index.log"), "*.html");
			Assert.Empty(orphans);
		}

		[Fact]
		public void Detect_EmptyDirectoryEmptyIndex_ReturnsEmpty()
		{
			var dir = CreateDownloadDir();
			var index = CreateIndex(); // empty
			var orphans = OrphanFiles.Detect(dir, index, "*.html");
			Assert.Empty(orphans);
		}

		// ── Core behaviour ────────────────────────────────────────────────────

		[Fact]
		public void Detect_AllFilesIndexed_ReturnsEmpty()
		{
			var dir = CreateDownloadDir();
			WriteFile(dir, "a.html");
			WriteFile(dir, "b.html");
			var index = CreateIndex("a.html", "b.html");

			var orphans = OrphanFiles.Detect(dir, index, "*.html");
			Assert.Empty(orphans);
		}

		[Fact]
		public void Detect_OneOrphanPresent_ReturnsIt()
		{
			var dir = CreateDownloadDir();
			WriteFile(dir, "a.html");
			WriteFile(dir, "orphan.html");
			var index = CreateIndex("a.html");

			var orphans = OrphanFiles.Detect(dir, index, "*.html");
			Assert.Single(orphans);
			Assert.Equal("orphan.html", orphans[0]);
		}

		[Fact]
		public void Detect_MultipleOrphans_ReturnsAllSortedAscending()
		{
			var dir = CreateDownloadDir();
			WriteFile(dir, "zeta.html");
			WriteFile(dir, "alpha.html");
			WriteFile(dir, "mid.html");
			WriteFile(dir, "known.html");
			var index = CreateIndex("known.html");

			var orphans = OrphanFiles.Detect(dir, index, "*.html");
			Assert.Equal(["alpha.html", "mid.html", "zeta.html"], orphans);
		}

		[Fact]
		public void Detect_IndexEntriesNotInDirectory_AreIgnored()
		{
			// An indexed-but-missing file is not an orphan — Detect only
			// finds the reverse case (files without an index entry).
			var dir = CreateDownloadDir();
			WriteFile(dir, "present.html");
			var index = CreateIndex("present.html", "missing.html");

			var orphans = OrphanFiles.Detect(dir, index, "*.html");
			Assert.Empty(orphans);
		}

		// ── Filename comparison is case-insensitive ──────────────────────────

		[Fact]
		public void Detect_CaseInsensitiveFilenameMatch()
		{
			var dir = CreateDownloadDir();
			WriteFile(dir, "Page.html");
			var index = CreateIndex("page.html"); // lower-case in index

			var orphans = OrphanFiles.Detect(dir, index, "*.html");
			Assert.Empty(orphans);
		}

		// ── File pattern is respected ────────────────────────────────────────

		[Fact]
		public void Detect_RespectsFilePattern_NonMatchingExtensionsIgnored()
		{
			var dir = CreateDownloadDir();
			WriteFile(dir, "a.html");
			WriteFile(dir, "asset.css"); // would be unindexed but pattern excludes it
			var index = CreateIndex("a.html");

			var orphans = OrphanFiles.Detect(dir, index, "*.html");
			Assert.Empty(orphans);
		}

		// ── Robustness: blank lines and whitespace in index ──────────────────

		[Fact]
		public void Detect_IndexBlankLines_AreSkipped()
		{
			var dir = CreateDownloadDir();
			WriteFile(dir, "a.html");
			var indexPath = Path.Combine(_tempDir, "02-crawler-index.log");
			File.WriteAllLines(indexPath,
				["", "  ", "a.html | https://example.com/a.html | discovery", ""],
				Encoding.UTF8);

			var orphans = OrphanFiles.Detect(dir, indexPath, "*.html");
			Assert.Empty(orphans);
		}

		[Fact]
		public void Detect_IndexEntryFirstColumnTrimmed()
		{
			// Index entries may have leading/trailing whitespace around the filename;
			// detection trims before comparing.
			var dir = CreateDownloadDir();
			WriteFile(dir, "a.html");
			var indexPath = Path.Combine(_tempDir, "02-crawler-index.log");
			File.WriteAllLines(indexPath,
				["  a.html  | https://example.com/a.html | discovery"],
				Encoding.UTF8);

			var orphans = OrphanFiles.Detect(dir, indexPath, "*.html");
			Assert.Empty(orphans);
		}
	}
}
