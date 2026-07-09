using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the on* event-handler harvest path in <see cref="DomTraverser"/>. When script checking
	/// is enabled, an inline event-handler attribute (onclick, …) is treated as JavaScript: its
	/// string literals are lifted by the lexer and each becomes a <see cref="RunSource.Script"/> run
	/// located at the handler attribute (<c>tag[@handler]</c>). Handler CODE — calls, keywords,
	/// identifiers — is discarded (not a string literal), and the raw handler value is never emitted
	/// as an attribute run. When script checking is off, the handler is skipped wholesale (unchanged).
	/// Gating of which literals are actually checked is RunChecker/ClassifyScriptLiteral's job; this
	/// path emits every literal, exactly like the &lt;script&gt;-body harvest.
	///
	/// Fixtures are invented, neutral JavaScript.
	/// </summary>
	public class DomTraverserEventHandlerHarvestTests
	{
		private static HtmlDocument Doc(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		private const string Mixed =
			"<html><body><a onclick=\"fExample();alert('hello world');fSomethingElse('foo bar');\">x</a></body></html>";

		[Fact]
		public void HandlerOff_EmitsNothingForTheHandler()
		{
			var runs = DomTraverser.Traverse(Doc(Mixed)).ToList();
			Assert.DoesNotContain(runs, r => r.SourcePath.Contains("onclick"));
			Assert.DoesNotContain(runs, r => r.RawText.Contains("hello") || r.RawText.Contains("fExample"));
		}

		[Fact]
		public void HandlerOn_EmitsOnlyTheStringLiterals_AsScriptRuns()
		{
			var handler = DomTraverser.Traverse(Doc(Mixed), checkJavaScript: true)
				.Where(r => r.SourcePath.Contains("onclick"))
				.ToList();

			// Exactly the two quoted literals — function names are discarded by the lexer.
			Assert.Equal(new[] { "hello world", "foo bar" }, handler.Select(r => r.RawText).ToArray());
			Assert.All(handler, r => Assert.Equal(RunSource.Script, r.Source));
			Assert.All(handler, r => Assert.Equal("a[@onclick]", r.SourcePath));
		}

		[Fact]
		public void HandlerOn_RawHandlerValue_IsNeverAnAttributeRun()
		{
			var runs = DomTraverser.Traverse(Doc(Mixed), checkJavaScript: true).ToList();
			// No attribute run carries the handler code (no fExample/fSomethingElse tokenized as prose).
			Assert.DoesNotContain(runs, r => r.Source == RunSource.Attribute && r.RawText.Contains("fExample"));
		}

		[Fact]
		public void PureCodeHandler_EmitsNoRuns()
		{
			// No string literals → nothing to check (return/this/doStuff are not literals).
			var runs = DomTraverser.Traverse(
				Doc("<html><body><a onclick=\"return doStuff(this);\">x</a></body></html>"),
				checkJavaScript: true).ToList();
			Assert.DoesNotContain(runs, r => r.SourcePath.Contains("onclick"));
		}
	}
}
