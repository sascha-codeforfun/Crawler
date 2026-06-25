using HtmlAgilityPack;
using Xunit;
using Crawler.Quality;

namespace Crawler.Tests.Quality
{
	public class LanguageMismatchTests
	{
		private static HtmlDocument Doc(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		[Fact]
		public void Check_Matching_ReturnsEmpty()
		{
			var doc = Doc("<html lang=\"de\"><head><meta name=\"language\" content=\"de\"></head></html>");
			var issues = LanguageMismatch.Check("f.html", doc).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_Mismatch_ReturnsIssue()
		{
			var doc = Doc("<html lang=\"de\"><head><meta name=\"language\" content=\"en\"></head></html>");
			var issues = LanguageMismatch.Check("f.html", doc).ToList();
			Assert.Single(issues);
			Assert.Equal("LANGUAGE_MISMATCH", issues[0].IssueType);
		}

		[Fact]
		public void Check_SubcodeNormalised_MatchingBaseCode_ReturnsEmpty()
		{
			// de-DE vs de should not trigger — both normalise to "de"
			var doc = Doc("<html lang=\"de-DE\"><head><meta name=\"language\" content=\"de\"></head></html>");
			var issues = LanguageMismatch.Check("f.html", doc).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_NoMetaTag_ReturnsEmpty()
		{
			var doc = Doc("<html lang=\"de\"><head></head></html>");
			var issues = LanguageMismatch.Check("f.html", doc).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_NoHtmlLang_ReturnsEmpty()
		{
			var doc = Doc("<html><head><meta name=\"language\" content=\"de\"></head></html>");
			var issues = LanguageMismatch.Check("f.html", doc).ToList();
			Assert.Empty(issues);
		}

		[Fact]
		public void Check_DetailMentionsBothLangs()
		{
			var doc = Doc("<html lang=\"de\"><head><meta name=\"language\" content=\"en\"></head></html>");
			var issues = LanguageMismatch.Check("f.html", doc).ToList();
			Assert.Contains("de", issues[0].Detail);
			Assert.Contains("en", issues[0].Detail);
		}
	}
}
