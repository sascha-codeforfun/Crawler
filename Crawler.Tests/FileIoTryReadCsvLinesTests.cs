using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for FileIo.TryReadCsvLines — file read, missing file, and lock
	/// behaviour in silent mode (lock simulation via FileStream hold).
	/// </summary>
	[Collection("Logger")]
	public class FileIoTryReadCsvLinesTests : IDisposable
	{
		private readonly string _tempDir;

		public FileIoTryReadCsvLinesTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"tools-csv-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_tempDir);
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_tempDir, recursive: true); } catch { }
		}

		private string WriteCsv(params string[] lines)
		{
			var path = Path.Combine(_tempDir, $"test-{Guid.NewGuid():N}.csv");
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		// ── Normal read ───────────────────────────────────────────────────────

		[Fact]
		public void TryReadCsvLines_ValidFile_ReturnsTrueAndLines()
		{
			var path = WriteCsv("header;col", "row1;val1", "row2;val2");
			var ok = FileIo.TryReadCsvLines(path, silent: true, out var lines);
			Assert.True(ok);
			Assert.Equal(3, lines.Count());
		}

		[Fact]
		public void TryReadCsvLines_ValidFile_ContentCorrect()
		{
			var path = WriteCsv("a;b", "x;y");
			FileIo.TryReadCsvLines(path, silent: true, out var lines);
			var list = lines.ToList();
			Assert.Equal("a;b", list[0]);
			Assert.Equal("x;y", list[1]);
		}

		[Fact]
		public void TryReadCsvLines_EmptyFile_ReturnsTrueAndEmptyLines()
		{
			var path = Path.Combine(_tempDir, "empty.csv");
			File.WriteAllText(path, "");
			var ok = FileIo.TryReadCsvLines(path, silent: true, out var lines);
			Assert.True(ok);
			Assert.Empty(lines);
		}

		// ── Missing file ──────────────────────────────────────────────────────

		[Fact]
		public void TryReadCsvLines_MissingFile_SilentMode_ReturnsFalse()
		{
			var path = Path.Combine(_tempDir, "nonexistent.csv");
			var ok = FileIo.TryReadCsvLines(path, silent: true, out var lines);
			Assert.False(ok);
			Assert.Empty(lines);
		}

		// ── Locked file in silent mode ────────────────────────────────────────

		[Fact]
		public void TryReadCsvLines_LockedFile_SilentMode_ReturnsFalseAfterRetries()
		{
			var path = WriteCsv("header", "row");

			// Hold an exclusive lock on the file
			using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
				FileShare.None);

			// Should fail after retries — use a very short retry to keep test fast
			// We can't easily inject retry count, so we test the outcome: returns false
			// Note: this test may take ~RetryDelayMs * MaxRetries seconds in silent mode
			// To keep it fast we just verify the API contract without waiting for retries
			// by checking that it eventually returns false (behaviour tested via outcome)
			var ok = FileIo.TryReadCsvLines(path, silent: true, out var lines,
				Encoding.UTF8);
			Assert.False(ok);
			Assert.Empty(lines);
		}

		// ── Encoding ──────────────────────────────────────────────────────────

		[Fact]
		public void TryReadCsvLines_Latin1File_ReadsCorrectly()
		{
			var path = Path.Combine(_tempDir, "latin1.csv");
			File.WriteAllText(path, "header\nÄÖÜ", Encoding.Latin1);
			var ok = FileIo.TryReadCsvLines(path, silent: true, out var lines,
				Encoding.Latin1);
			Assert.True(ok);
			var list = lines.ToList();
			Assert.Equal("ÄÖÜ", list[1]);
		}
	}
}
