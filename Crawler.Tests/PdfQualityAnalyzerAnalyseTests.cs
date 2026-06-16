using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for PdfQualityAnalyzer.Analyse — the folder orchestrator that scans
	/// *.pdf in the download directory, checks each via CheckPdf, writes the quality
	/// and remediation logs, and emits an IssueRecord per PDF that has metadata gaps.
	///
	/// PDF fixtures are minimal Latin-1 marker blobs (%PDF-1.4 … %%EOF); a blob with
	/// no metadata yields every field n/a/absent → HasGaps. The per-marker Check* /
	/// Extract* helpers are covered by PdfQualityAnalyzerTests; this drives the
	/// orchestration, the gap→IssueRecord mapping, and both Excerpt branches.
	/// SYNTHETIC fixtures. In the Logger collection: Analyse / CrawlIndex log via the
	/// static Logger; UrlCache is process-wide so URL fixtures use distinct names.
	/// </summary>
	[Collection("Logger")]
	public class PdfQualityAnalyzerAnalyseTests : IDisposable
	{
		private readonly string _dir;
		private readonly string _qualityLog;
		private readonly string _remediationLog;

		public PdfQualityAnalyzerAnalyseTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"pdfq-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
			_qualityLog = Path.Combine(_dir, "pdf_quality.log");
			_remediationLog = Path.Combine(_dir, "pdf_remediation.log");
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		// ── helpers ─────────────────────────────────────────────────────────

		private void WritePdf(string name, string body) =>
			File.WriteAllBytes(Path.Combine(_dir, name),
				Encoding.Latin1.GetBytes($"%PDF-1.4\n{body}\n%%EOF"));

		private void RegisterUrl(string filename, string url)
		{
			var path = Path.Combine(_dir, $"lookup_{Guid.NewGuid():N}.lku");
			File.WriteAllLines(path, new[] { $"{filename}|{url}|discovery" }, Encoding.UTF8);
			UrlCache.LoadCache(path);
		}

		private List<IssueTracking.IssueRecord> Analyse() =>
			PdfQualityAnalyzer.Analyse(_dir, _qualityLog, _remediationLog);

		// ── tests ───────────────────────────────────────────────────────────

		[Fact]
		public void Analyse_DirectoryMissing_ReturnsEmpty()
		{
			Assert.Empty(PdfQualityAnalyzer.Analyse(
				Path.Combine(_dir, "nope"), _qualityLog, _remediationLog));
		}

		[Fact]
		public void Analyse_NoPdfFiles_ReturnsEmptyAndWritesLog()
		{
			File.WriteAllText(Path.Combine(_dir, "note.txt"), "not a pdf");

			Assert.Empty(Analyse());
			Assert.True(File.Exists(_qualityLog)); // WriteLog called even with no results
		}

		[Fact]
		public void Analyse_PdfWithNoMetadata_FlagsAllGaps()
		{
			WritePdf("doc.pdf", string.Empty); // no markers → every field a gap

			var r = Assert.Single(Analyse());
			Assert.Equal("PDFQUALITY", r.Type);
			Assert.Contains("PDF_NO_TITLE", r.Word);
			Assert.Equal("doc.pdf", r.Url);     // unregistered → filename
			Assert.Equal("doc.pdf", r.Excerpt); // Title "n/a" → Excerpt is the page URL
		}

		[Fact]
		public void Analyse_PdfWithTitleOnly_ExcerptIsTitle()
		{
			WritePdf("titled.pdf", "/Title (My Doc)");

			var r = Assert.Single(Analyse());
			Assert.Equal("My Doc", r.Excerpt);          // Title present → Excerpt is the title
			Assert.DoesNotContain("PDF_NO_TITLE", r.Word);
			Assert.Contains("PDF_NO_DESCRIPTION", r.Word); // still gappy elsewhere
		}

		[Fact]
		public void Analyse_RegisteredFile_UsesCacheUrl()
		{
			WritePdf("registered.pdf", string.Empty);
			RegisterUrl("registered.pdf", "https://site.test/registered.pdf");

			var r = Assert.Single(Analyse());
			Assert.Equal("https://site.test/registered.pdf", r.Url);
		}

		[Fact]
		public void Analyse_MultipleGappyPdfs_OneRecordEach()
		{
			WritePdf("alpha.pdf", string.Empty);
			WritePdf("beta.pdf", string.Empty);

			Assert.Equal(2, Analyse().Count);
		}
	}
}
