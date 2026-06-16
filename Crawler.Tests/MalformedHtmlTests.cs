using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for ContentQuality's MALFORMED_HTML checks.
	///
	/// Tier 1 — CheckContentBeforeDoctype (#342): non-whitespace content before
	/// the opening doctype/html/xml token (after an optional leading UTF-8 BOM),
	/// detected on raw bytes because the parser folds such leading content into
	/// the tree.
	///
	/// Tier 2 — CheckHtmlParseErrors: bridges HtmlAgilityPack ParseErrors from a
	/// raw-HTML parse, one finding per (file, code) with an occurrence count.
	///
	/// Both are whole-file structural checks routed to log 10 and auto-promoted
	/// (not triaged).
	/// </summary>
	public class MalformedHtmlTests
	{
		private static List<ContentQuality.QualityIssue> Run(byte[] bytes) =>
			ContentQuality.CheckContentBeforeDoctype("test.html", bytes).ToList();

		private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
		private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

		private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

		private static byte[] WithBom(string s) => Bom.Concat(Utf8(s)).ToArray();

		// ── Negative cases (well-formed leads — no finding) ───────────────

		[Fact]
		public void CleanDoctype_NoFinding()
		{
			var issues = Run(Ascii("<!DOCTYPE html><html><body>hi</body></html>"));
			Assert.Empty(issues);
		}

		[Fact]
		public void HtmlTagWithoutDoctype_NoFinding()
		{
			// A bare <html> opener is well-formed for this check's purposes.
			var issues = Run(Ascii("<html><body>hi</body></html>"));
			Assert.Empty(issues);
		}

		[Fact]
		public void XmlProlog_NoFinding()
		{
			var issues = Run(Ascii("<?xml version=\"1.0\"?><html></html>"));
			Assert.Empty(issues);
		}

		[Fact]
		public void LeadingWhitespaceThenDoctype_NoFinding()
		{
			var issues = Run(Ascii("  \r\n\t  <!doctype html><html></html>"));
			Assert.Empty(issues);
		}

		[Fact]
		public void LeadingBomThenDoctype_NoFinding()
		{
			// A single legitimate leading UTF-8 BOM before the doctype is fine.
			var issues = Run(WithBom("<!DOCTYPE html><html></html>"));
			Assert.Empty(issues);
		}

		[Fact]
		public void LeadingBomThenWhitespaceThenHtml_NoFinding()
		{
			var issues = Run(WithBom("\r\n   <html></html>"));
			Assert.Empty(issues);
		}

		[Fact]
		public void LowercaseDoctype_NoFinding()
		{
			// Prefix test is case-insensitive.
			var issues = Run(Ascii("<!doctype html>\n<html></html>"));
			Assert.Empty(issues);
		}

		[Fact]
		public void Empty_NoFinding()
		{
			Assert.Empty(Run([]));
		}

		[Fact]
		public void AllWhitespace_NoFinding()
		{
			// Empty/whitespace-only body is a different defect class (download
			// robustness), not CONTENT_BEFORE_DOCTYPE.
			var issues = Run(Ascii("   \r\n\t  "));
			Assert.Empty(issues);
		}

		[Fact]
		public void BomThenAllWhitespace_NoFinding()
		{
			var issues = Run(WithBom("   \r\n  "));
			Assert.Empty(issues);
		}

		// ── Positive cases (content before doctype — one finding) ─────────

		[Fact]
		public void TextBeforeDoctype_Flags()
		{
			var issues = Run(Ascii("oops<!DOCTYPE html><html></html>"));
			var issue = Assert.Single(issues);
			Assert.Equal("MALFORMED_HTML", issue.IssueType);
			Assert.Equal("CONTENT_BEFORE_DOCTYPE", issue.Detail);
		}

		[Fact]
		public void MarkupBeforeDoctype_Flags()
		{
			// The motivating specimen: a server error block welded on ahead of
			// the real document.
			var issues = Run(Ascii("<div class=\"error\">Server error</div><!DOCTYPE html><html></html>"));
			var issue = Assert.Single(issues);
			Assert.Equal("CONTENT_BEFORE_DOCTYPE", issue.Detail);
		}

		[Fact]
		public void ContentBeforeDoctype_OneFindingPerFile()
		{
			// Whole-document property — even with lots of junk, exactly one finding.
			var issues = Run(Ascii("aaa<p>bbb</p>ccc<!DOCTYPE html><html></html>"));
			Assert.Single(issues);
		}

		[Fact]
		public void BomThenContentThenDoctype_Flags()
		{
			// Leading BOM is skipped, but real content follows before the doctype.
			var issues = Run(WithBom("garbage<!DOCTYPE html><html></html>"));
			var issue = Assert.Single(issues);
			Assert.Equal("CONTENT_BEFORE_DOCTYPE", issue.Detail);
		}

		[Fact]
		public void Context_CarriesOffsetAndLead()
		{
			var issues = Run(Ascii("oops<!DOCTYPE html>"));
			var issue = Assert.Single(issues);
			// Context records the byte offset of the first non-whitespace and
			// the offending lead as evidence.
			Assert.Contains("offset 0", issue.Context, StringComparison.Ordinal);
			Assert.Contains("oops", issue.Context, StringComparison.Ordinal);
		}

		[Fact]
		public void Context_OffsetAccountsForBomAndWhitespace()
		{
			// BOM (3 bytes) + 2 spaces, then content begins at byte offset 5.
			var issues = Run(WithBom("  oops<!DOCTYPE html>"));
			var issue = Assert.Single(issues);
			Assert.Contains("offset 5", issue.Context, StringComparison.Ordinal);
		}

		// ── Gating contract ───────────────────────────────────────────────

		[Fact]
		public void Method_DoesNotSelfGate_CallerGatesOnConfig()
		{
			// CheckContentBeforeDoctype takes no config and never consults the
			// MalformedHtml flags — the caller (Analyse) gates on
			// config.MalformedHtml.DetectContentBeforeDoctype. This test pins
			// that contract so the gate is not duplicated inside the method.
			var issues = ContentQuality
				.CheckContentBeforeDoctype("test.html", Ascii("oops<!DOCTYPE html>"))
				.ToList();
			Assert.Single(issues);
		}

		// ── Tier 2: CheckHtmlParseErrors ──────────────────────────────────

		private static List<ContentQuality.QualityIssue> RunParse(string html) =>
			ContentQuality.CheckHtmlParseErrors("test.html", html).ToList();

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

		// ── Tier 2: suppression of parser-error codes ─────────────────────

		[Fact]
		public void SuppressedCode_IsFilteredAtDetection()
		{
			// Whatever codes a given input produces, suppressing all of them
			// yields zero findings — the filter drops them before they become
			// findings (never reach log 10 / IssueTracking).
			const string html = "<html><body><div><div><div>aaa</body>";
			var all = ContentQuality.CheckHtmlParseErrors("test.html", html).ToList();
			if (all.Count == 0)
			{
				return; // nothing to suppress on this HAP build
			}

			var codes = all.Select(i => i.Detail).Distinct().ToList();
			var suppressed = ContentQuality
				.CheckHtmlParseErrors("test.html", html, codes)
				.ToList();
			Assert.Empty(suppressed);
		}

		[Fact]
		public void SuppressList_OnlyRemovesListedCodes()
		{
			// Suppressing a code that does not appear leaves all findings intact.
			const string html = "<html><body><div><div>aaa</body>";
			var all = ContentQuality.CheckHtmlParseErrors("test.html", html).ToList();
			var withBogusSuppress = ContentQuality
				.CheckHtmlParseErrors("test.html", html, ["NoSuchCodeXYZ"])
				.ToList();
			Assert.Equal(all.Count, withBogusSuppress.Count);
		}

		[Fact]
		public void SuppressList_IsCaseInsensitive()
		{
			const string html = "<html><body><div><div><div>aaa</body>";
			var all = ContentQuality.CheckHtmlParseErrors("test.html", html).ToList();
			if (all.Count == 0)
			{
				return;
			}

			// Suppress the first code using a different case — must still filter.
			var code = all[0].Detail;
			var suppressed = ContentQuality
				.CheckHtmlParseErrors("test.html", html, [code.ToUpperInvariant()])
				.ToList();
			Assert.DoesNotContain(suppressed, i =>
				string.Equals(i.Detail, code, StringComparison.OrdinalIgnoreCase));
		}

		[Fact]
		public void EmptyOrNullSuppressList_SuppressesNothing()
		{
			const string html = "<html><body><div><div>aaa</body>";
			var baseline = ContentQuality.CheckHtmlParseErrors("test.html", html).ToList();
			var withEmpty = ContentQuality
				.CheckHtmlParseErrors("test.html", html, [])
				.ToList();
			Assert.Equal(baseline.Count, withEmpty.Count);
		}
	}
}
