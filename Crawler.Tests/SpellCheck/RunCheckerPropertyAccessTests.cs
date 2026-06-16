using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 632: RunChecker drops a flagged token that is the HEAD of a lone JavaScript property
	/// access ("head.prop", lowercase tail standing against a boundary) — a code fragment, not a word.
	/// Script runs only; it can only ever REMOVE a finding. Two guards keep a German missing-space typo
	/// surfaced: the tail must start lowercase, and no continuing prose may follow it. Uses a fake
	/// RunCheck (no Hunspell). All fixtures are synthetic and neutral.
	/// </summary>
	public class SpellCheckRunCheckerPropertyAccessTests
	{
		// Multi-word literal so the script value-gate treats it as prose and re-expands per token; a
		// bare "obj.prop" single-token literal is already dropped earlier as a machine slug.
		private static TextRun ScriptRun(string text) =>
			new(HtmlNode.CreateNode("<script></script>"), RunSource.Script, "script[L1:1]", text);

		private static TextRun TextNodeRun(string text) =>
			new(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text);

		private static RunCheck FlagWords(params string[] words)
		{
			var set = new HashSet<string>(words);
			return (canonicalText, _) =>
				SpellTokenizer
					.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", canonicalText))
					.Select(t => t.Text)
					.Where(set.Contains)
					.Distinct()
					.Select(w => new CheckMiss(w, "de"));
		}

		[Fact]
		public void ScriptRun_LonePropertyAccessHead_AtEnd_IsDropped()
		{
			// "widgetstate.disabled" — head fails the dictionary, lowercase tail, runs to the literal end.
			var findings = RunChecker.Check(ScriptRun("flag widgetstate.disabled"), "de", FlagWords("widgetstate")).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void ScriptRun_DottedChainHead_IsDropped()
		{
			// A chain "foobar.baz.qux" is even more clearly code — the second dot is a boundary.
			var findings = RunChecker.Check(ScriptRun("set foobar.baz.qux done"), "de", FlagWords("foobar")).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void ScriptRun_MissingSpaceProse_LowercaseTailThenMoreWords_IsKept()
		{
			// "xyzzy.bitte mehr texte hier" — looks like a missing space before a continuing sentence;
			// the lowercase word after the space means prose continues, so the head stays surfaced.
			var findings = RunChecker.Check(ScriptRun("xyzzy.bitte mehr texte hier"), "de", FlagWords("xyzzy")).ToList();
			Assert.Contains(findings, f => f.Word == "xyzzy");
		}

		[Fact]
		public void ScriptRun_CapitalisedTail_IsKept()
		{
			// "foo.Bar" — a capitalised tail is a real sentence break, never a property access.
			var findings = RunChecker.Check(ScriptRun("text foo.Bar mehr worte hier"), "de", FlagWords("foo")).ToList();
			Assert.Contains(findings, f => f.Word == "foo");
		}

		[Fact]
		public void NonScriptRun_PropertyAccessShape_IsKept()
		{
			// The rule is script-scoped: the identical shape in a text node is left surfaced.
			var findings = RunChecker.Check(TextNodeRun("flag widgetstate.disabled"), "de", FlagWords("widgetstate")).ToList();
			Assert.Contains(findings, f => f.Word == "widgetstate");
		}
	}
}
