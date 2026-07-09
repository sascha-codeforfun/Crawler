using System.Text;
using Xunit;
using Crawler.Quality;

namespace Crawler.Tests.Quality
{
	/// <summary>
	/// Tests for the MALFORMED_HTML/CONTENT_BEFORE_DOCTYPE check
	/// (ContentBeforeDoctype.Check, #342): non-whitespace content before
	/// the opening doctype/html/xml token (after an optional leading UTF-8 BOM),
	/// detected on raw bytes because the parser folds such leading content into
	/// the tree.
	///
	/// It is a whole-file structural check routed to log 10 and auto-promoted
	/// (not triaged).
	/// </summary>
	public class ContentBeforeDoctypeTests
	{
		private static List<QualityIssue> Run(byte[] bytes) =>
			ContentBeforeDoctype.Check("test.html", bytes).ToList();

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
		public void Check_DoesNotSelfGate_CallerGatesOnConfig()
		{
			// ContentBeforeDoctype.Check takes no config and never consults the
			// MalformedHtml flags — the caller (Analyse) gates on
			// config.MalformedHtml.DetectContentBeforeDoctype. This test pins
			// that contract so the gate is not duplicated inside the method.
			var issues = ContentBeforeDoctype
				.Check("test.html", Ascii("oops<!DOCTYPE html>"))
				.ToList();
			Assert.Single(issues);
		}
	}
}
