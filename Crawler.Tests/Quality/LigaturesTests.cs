using Crawler.Quality;
using Xunit;

namespace Crawler.Tests.Quality
{
	public class LigaturesTests
	{
		private static ContentQualityConfig DefaultConfig() => new()
		{
			ContentQualityExcerptRadius    = 120,
			ContentQualityQuoteFullSentence = false,  // keep tests deterministic
			ContentQualityMaxExcerpt  = 400,
		};

		// ── Check ────────────────────────────────────────────────────

		[Fact]
		public void Check_NoLigatures_ReturnsEmpty()
		{
			var issues = Ligatures.Check("f.html", "The office is open.", DefaultConfig()).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_FiLigature_ReturnsOneIssue()
		{
			var issues = Ligatures.Check("f.html", "o\uFB01ce", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Equal("LIGATURE", issues[0].IssueType);
			Assert.Contains("U+FB01", issues[0].Detail);
		}

		[Fact]
		public void Check_FlLigature_ReturnsOneIssue()
		{
			var issues = Ligatures.Check("f.html", "o\uFB02oor", DefaultConfig()).ToList();
			Assert.Single(issues);
			Assert.Contains("U+FB02", issues[0].Detail);
		}

		[Fact]
		public void Check_MultipleLigatures_ReturnsAllHits()
		{
			// fi and ffl ligatures in same string
			var issues = Ligatures.Check("f.html", "\uFB01nd the \uFB04uent", DefaultConfig()).ToList();
			Assert.Equal(2, issues.Count);
		}

		[Fact]
		public void Check_FilenamePassedThrough()
		{
			var issues = Ligatures.Check("page-001.html", "o\uFB01ce", DefaultConfig()).ToList();
			Assert.Equal("page-001.html", issues[0].Filename);
		}
	}
}
