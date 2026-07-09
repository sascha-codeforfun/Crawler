using Xunit;
using Crawler.Logging;

namespace Crawler.Tests.Logging
{
	/// <summary>
	/// Characterization tests for <see cref="CrawlLogWriter.WriteSaved"/>. The method
	/// writes the 00-crawler.log "saved" row whose six pipe-delimited fields are read
	/// back by the settle phase — a machine contract with no other coverage — so
	/// these lock the line shape and the null/empty defaulting.
	///
	/// Field order:  timestamp | url | saved | fileName | source | contentType
	/// </summary>
	public class CrawlLogWriterTests : IDisposable
	{
		private readonly string _logFile;

		public CrawlLogWriterTests()
		{
			_logFile = Path.Combine(
				Path.GetTempPath(), $"logsavedraw-{Guid.NewGuid():N}.log");
		}

		public void Dispose()
		{
			try { File.Delete(_logFile); } catch { }
		}

		private string[] ReadFields()
		{
			var lines = File.ReadAllLines(_logFile);
			Assert.Single(lines);
			return lines[0].Split(" | ");
		}

		[Fact]
		public void WritesSixFieldPipeDelimitedLine()
		{
			CrawlLogWriter.WriteSaved(
				"https://example.com/page", "page.htm", _logFile,
				source: "discovery", contentType: "text/html");

			var f = ReadFields();
			Assert.Equal(6, f.Length);
			Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", f[0]); // timestamp
			Assert.Equal("https://example.com/page", f[1]);
			Assert.Equal("saved", f[2]);
			Assert.Equal("page.htm", f[3]);
			Assert.Equal("discovery", f[4]);
			Assert.Equal("text/html", f[5]);
		}

		[Fact]
		public void NullContentType_RecordedAsNa()
		{
			CrawlLogWriter.WriteSaved(
				"https://example.com/a", "a.htm", _logFile,
				source: "discovery", contentType: null);

			Assert.Equal("n/a", ReadFields()[5]);
		}

		[Fact]
		public void EmptyContentType_RecordedAsNa()
		{
			CrawlLogWriter.WriteSaved(
				"https://example.com/a", "a.htm", _logFile,
				source: "discovery", contentType: "");

			Assert.Equal("n/a", ReadFields()[5]);
		}

		[Fact]
		public void EmptySource_DefaultsToDiscovery()
		{
			CrawlLogWriter.WriteSaved(
				"https://example.com/a", "a.htm", _logFile,
				source: "", contentType: "text/html");

			Assert.Equal("discovery", ReadFields()[4]);
		}

		[Fact]
		public void AppendsRatherThanOverwrites()
		{
			CrawlLogWriter.WriteSaved("https://example.com/1", "1.htm", _logFile, "discovery", "text/html");
			CrawlLogWriter.WriteSaved("https://example.com/2", "2.htm", _logFile, "list", "application/pdf");

			var lines = File.ReadAllLines(_logFile);
			Assert.Equal(2, lines.Length);
			Assert.Contains("https://example.com/1", lines[0]);
			Assert.Contains("https://example.com/2", lines[1]);
		}
	}
}
