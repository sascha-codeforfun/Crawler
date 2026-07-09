using System.Linq;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 633: the bulk inline-&lt;script&gt; scan (BulkScanPageScript). The full harvest+scan is a
	/// disk/Hunspell path validated by the real run (it writes logs 28/29); these tests cover the pure,
	/// safety-critical logic — that provenance HEADERS never become findings (the lexer skips them), that
	/// a literal is attributed to the right page, and the context window. Fixtures are synthetic.
	/// </summary>
	public class SpellCheckBulkScriptScannerTests
	{
		[Fact]
		public void ProvenanceHeaders_ProduceNoLiterals_EvenWhenTheyContainQuotes()
		{
			// A header written as a // comment is skipped by the lexer — so even an (adversarial) header
			// carrying an apostrophe contributes no string literal. Only the script body's literal remains.
			string blob =
				"// ===== https://example.test/page.html · it's a script #1 =====\n" +
				"var label = \"realword\";\n" +
				"// ===== https://example.test/other.html · script #2 =====\n" +
				"console.log('secondword');\n";

			var literals = JsStringLiteralExtractor.Extract(blob).Select(l => l.Text).ToList();

			Assert.Equal(new[] { "realword", "secondword" }, literals);
			// Nothing from the header lines (no "page", "script", "https", quoted header fragments).
			Assert.DoesNotContain(literals, t => t.Contains("page") || t.Contains("script") || t.Contains("example"));
		}

		[Fact]
		public void SourceForOffset_AttributesLiteralToPrecedingHeader()
		{
			string blob =
				"// ===== pageA · script #1 =====\n" +   // offset 0
				"var a = \"x\";\n" +
				"// ===== pageB · script #1 =====\n" +   // later offset
				"var b = \"y\";\n";

			var headers = BulkScriptScanner.HeaderOffsets(blob);
			Assert.Equal(2, headers.Count);
			Assert.Equal("pageA", headers[0].Source);
			Assert.Equal("pageB", headers[1].Source);

			int offsetInA = blob.IndexOf("\"x\"", System.StringComparison.Ordinal);
			int offsetInB = blob.IndexOf("\"y\"", System.StringComparison.Ordinal);
			Assert.Equal("pageA", BulkScriptScanner.SourceForOffset(headers, offsetInA));
			Assert.Equal("pageB", BulkScriptScanner.SourceForOffset(headers, offsetInB));
		}

		[Fact]
		public void SourceForOffset_BeforeAnyHeader_IsUnknown()
		{
			var headers = BulkScriptScanner.HeaderOffsets("// ===== pageA · script #1 =====\nvar a = \"x\";\n");
			Assert.Equal("(unknown)", BulkScriptScanner.SourceForOffset(headers, -1));
		}

		[Fact]
		public void ContextWindow_IsSingleLine_AndWhitespaceCollapsed()
		{
			string blob = "line one here\nconsole.log('true widget.disabled');\nline three\n";
			int start = blob.IndexOf("'true", System.StringComparison.Ordinal);
			string ctx = BulkScriptScanner.ContextWindow(blob, start, 22);

			Assert.DoesNotContain("\n", ctx);
			Assert.Contains("widget.disabled", ctx);
			Assert.DoesNotContain("line one", ctx);   // stopped at the newline boundary
			Assert.DoesNotContain("line three", ctx);
		}
	}
}
