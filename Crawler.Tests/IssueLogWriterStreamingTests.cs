using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for IssueLogWriter's StreamingWriter async surface and the small
	/// composition arms the existing IssueLogWriterTests don't reach (it covers the
	/// static SanitizeField/ComposeLine happy paths and the sync Append paths).
	/// These exercise WriteRecordAsync, WriteRawLineAsync, DisposeAsync, the
	/// ComposeLine empty-fields early return, and SanitizeField's single-char
	/// string-delimiter delegation.
	///
	/// SYNTHETIC fixtures written to a temp file. In the Logger collection for
	/// disk-touching safety.
	/// </summary>
	[Collection("Logger")]
	public class IssueLogWriterStreamingTests : IDisposable
	{
		private readonly string _dir;

		public IssueLogWriterStreamingTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"issuelog-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		private string Path_(string name) => Path.Combine(_dir, name);

		// ── StreamingWriter async surface ───────────────────────────────────

		[Fact]
		public async Task WriteRecordAsync_SanitizesAndWritesOneLine()
		{
			var path = Path_("record.log");
			await using (var writer = IssueLogWriter.OpenStreaming(path, '|', append: false))
			{
				await writer.WriteRecordAsync("a", "b", "c");
			}

			var lines = File.ReadAllLines(path);
			Assert.Single(lines);
			Assert.Equal("a|b|c", lines[0]);
		}

		[Fact]
		public async Task WriteRawLineAsync_StripsNewlinesButKeepsDelimiters()
		{
			var path = Path_("raw.log");
			await using (var writer = IssueLogWriter.OpenStreaming(path, '|', append: false))
			{
				// Raw escape hatch: newline becomes a space, but the pipes survive.
				await writer.WriteRawLineAsync("pre|composed\nwith newline");
			}

			var line = Assert.Single(File.ReadAllLines(path));
			Assert.Contains("pre|composed", line); // delimiters preserved
			Assert.DoesNotContain("\n", line);      // newline stripped
		}

		[Fact]
		public async Task DisposeAsync_FlushesContentToFile()
		{
			var path = Path_("dispose.log");
			var writer = IssueLogWriter.OpenStreaming(path, '|', append: false);
			await writer.WriteRecordAsync("x", "y");
			await writer.DisposeAsync();

			Assert.Equal("x|y", Assert.Single(File.ReadAllLines(path)));
		}

		[Fact]
		public async Task WriteRecord_Sync_AlsoWritesOneLine()
		{
			var path = Path_("sync.log");
			await using (var writer = IssueLogWriter.OpenStreaming(path, '|', append: false))
			{
				writer.WriteRecord("one", "two");
			}

			Assert.Equal("one|two", Assert.Single(File.ReadAllLines(path)));
		}

		// ── ComposeLine empty-fields early return ───────────────────────────

		[Fact]
		public void ComposeLine_NoFields_ReturnsEmpty()
		{
			Assert.Equal(string.Empty, IssueLogWriter.ComposeLine('|'));
			Assert.Equal(string.Empty, IssueLogWriter.ComposeLine("@@@"));
		}

		// ── SanitizeField single-char string delimiter delegation ───────────

		[Fact]
		public void SanitizeField_SingleCharStringDelimiter_DelegatesToCharOverload()
		{
			// A length-1 string delimiter routes through the char overload.
			var (cleaned, changed) = IssueLogWriter.SanitizeField("a|b", "|");
			Assert.DoesNotContain("|", cleaned);
			Assert.True(changed);
		}
	}
}
