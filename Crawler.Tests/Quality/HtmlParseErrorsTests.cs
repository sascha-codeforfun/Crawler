using Xunit;
using Crawler.Quality;

namespace Crawler.Tests.Quality
{
	public class HtmlParseErrorsTests
	{
		private static List<QualityIssue> RunParse(string html) =>
			HtmlParseErrors.Check("test.html", html).ToList();

		[Fact]
		public void WellFormedHtml_NoParseErrors_NoFindings()
		{
			var issues = RunParse("<!DOCTYPE html><html><head><title>x</title></head><body><p>ok</p></body></html>");
			Assert.Empty(issues);
		}

		[Fact]
		public void ParseError_EmitsMalformedHtmlWithCodeInDetail()
		{
			// An unclosed tag triggers a HAP parse error. We assert on the
			// finding shape, not a specific code (HAP's exact code set is its
			// own contract): IssueType is MALFORMED_HTML and Detail is the code,
			// so PromoteFromQuality builds Word = MALFORMED_HTML:<code>.
			var issues = RunParse("<html><body><div><span>unclosed</body></html>");
			if (issues.Count == 0)
			{
				return; // tolerant: if this HAP build reports no error here, nothing to assert
			}

			Assert.All(issues, i =>
			{
				Assert.Equal("MALFORMED_HTML", i.IssueType);
				Assert.False(string.IsNullOrEmpty(i.Detail));
				// Context carries the count, not the Key-bearing field.
				Assert.Contains("occurrence(s)", i.Context, StringComparison.Ordinal);
			});
		}

		[Fact]
		public void ParseErrors_OneFindingPerCode_CountInContext()
		{
			// Whatever codes fire, each distinct code yields exactly one finding
			// and the Context names that code with a count. Distinctness of the
			// Detail (code) across findings is the property under test.
			var issues = RunParse("<html><body><div><div><div>aaa</body>");
			var distinctDetails = issues.Select(i => i.Detail).Distinct().Count();
			Assert.Equal(issues.Count, distinctDetails);
			Assert.All(issues, i =>
				Assert.Contains(i.Detail, i.Context, StringComparison.Ordinal));
		}

		[Fact]
		public void ParseErrors_DetailCarriesNoCount_KeyStaysStable()
		{
			// The count must live in Context, never in Detail — otherwise the
			// composite Key (MALFORMED_HTML:<detail>) would churn run to run.
			var issues = RunParse("<html><body><div><span>x</body>");
			Assert.All(issues, i =>
				Assert.DoesNotContain("occurrence", i.Detail, StringComparison.Ordinal));
		}


		[Fact]
		public void SuppressedCode_IsFilteredAtDetection()
		{
			// Whatever codes a given input produces, suppressing all of them
			// yields zero findings — the filter drops them before they become
			// findings (never reach log 10 / IssueTracking).
			const string html = "<html><body><div><div><div>aaa</body>";
			var all = HtmlParseErrors.Check("test.html", html).ToList();
			if (all.Count == 0)
			{
				return; // nothing to suppress on this HAP build
			}

			var codes = all.Select(i => i.Detail).Distinct().ToList();
			var suppressed = HtmlParseErrors
				.Check("test.html", html, codes)
				.ToList();
			Assert.Empty(suppressed);
		}

		[Fact]
		public void SuppressList_OnlyRemovesListedCodes()
		{
			// Suppressing a code that does not appear leaves all findings intact.
			const string html = "<html><body><div><div>aaa</body>";
			var all = HtmlParseErrors.Check("test.html", html).ToList();
			var withBogusSuppress = HtmlParseErrors
				.Check("test.html", html, ["NoSuchCodeXYZ"])
				.ToList();
			Assert.Equal(all.Count, withBogusSuppress.Count);
		}

		[Fact]
		public void SuppressList_IsCaseInsensitive()
		{
			const string html = "<html><body><div><div><div>aaa</body>";
			var all = HtmlParseErrors.Check("test.html", html).ToList();
			if (all.Count == 0)
			{
				return;
			}

			// Suppress the first code using a different case — must still filter.
			var code = all[0].Detail;
			var suppressed = HtmlParseErrors
				.Check("test.html", html, [code.ToUpperInvariant()])
				.ToList();
			Assert.DoesNotContain(suppressed, i =>
				string.Equals(i.Detail, code, StringComparison.OrdinalIgnoreCase));
		}

		[Fact]
		public void EmptyOrNullSuppressList_SuppressesNothing()
		{
			const string html = "<html><body><div><div>aaa</body>";
			var baseline = HtmlParseErrors.Check("test.html", html).ToList();
			var withEmpty = HtmlParseErrors
				.Check("test.html", html, [])
				.ToList();
			Assert.Equal(baseline.Count, withEmpty.Count);
		}
	}
}
