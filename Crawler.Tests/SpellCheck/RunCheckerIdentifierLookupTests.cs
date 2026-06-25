using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the JS identifier-lookup guard: a string literal that is the quoted argument to a
	/// vetted DOM identifier lookup (getElementById and the four siblings) is an id/name/class
	/// reference, not prose, so the whole literal is dropped from spell-checking. The guard fires
	/// on BOTH script paths because both funnel through <see cref="RunChecker"/>.
	///
	/// All fixtures use SYNTHETIC ids ("mywidget", "myclass", …) and synthetic surrounding code.
	/// </summary>
	public class RunCheckerIdentifierLookupTests
	{
		private static RunCheck FlagEverything()
			=> (text, language) =>
				SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.Script, "s", text))
					.Select(t => t.Text)
					.Where(w => w.Any(char.IsLetter))
					.Distinct()
					.Select(w => new CheckMiss(w, string.Empty));

		private static TextRun Script(string literal, string? context) =>
			new(HtmlNode.CreateNode("<p>x</p>"), RunSource.Script, "script", literal) { ScriptContext = context };

		private static List<string> Findings(string literal, string? context) =>
			RunChecker.Check(Script(literal, context), "de", FlagEverything()).Select(f => f.Word).ToList();

		// ── each vetted signature suppresses its quoted argument ────────

		[Theory]
		[InlineData("mywidget", "var a=document.getElementById(\"mywidget\");")]
		[InlineData("myfield", "var n=document.getElementsByName(\"myfield\");")]
		[InlineData("myclass", "var c=document.getElementsByClassName(\"myclass\");")]
		[InlineData("myclass", "if(el.classList.contains(\"myclass\")){go();}")]
		[InlineData("myfrag", "if(el.className.indexOf(\"myfrag\")>=0){go();}")]
		public void Signature_SuppressesQuotedArgument(string literal, string context)
		{
			Assert.Empty(Findings(literal, context));
		}

		// ── bare method (no anchor) must NOT suppress ───────────────────

		[Theory]
		[InlineData("myword", "if(node.contains(\"myword\")){go();}")]      // bare contains( = node.contains
		[InlineData("myword", "var i=bodyText.indexOf(\"myword\");")]        // bare indexOf( on prose
		public void BareMethod_DoesNotSuppress(string literal, string context)
		{
			Assert.Equal(new[] { literal }, Findings(literal, context));
		}

		// ── property key shape is not a call signature (→ TokensToFilter, not here) ──

		[Fact]
		public void PropertyKey_DoesNotSuppress()
		{
			Assert.Equal(new[] { "mywidget" }, Findings("mywidget", "$.ajax({dataType:\"mywidget\"});"));
		}

		// ── quote-agnostic and whitespace-tolerant ──────────────────────

		[Fact]
		public void SingleQuotes_Suppress()
		{
			Assert.Empty(Findings("mywidget", "document.getElementById('mywidget');"));
		}

		[Fact]
		public void WhitespaceAfterParen_Suppresses()
		{
			Assert.Empty(Findings("mywidget", "document.getElementById( \"mywidget\" );"));
		}

		// ── anchored to THIS literal: a different arg in the window is not mis-attributed ──

		[Fact]
		public void DifferentLiteralInWindow_NotSuppressed()
		{
			// The lookup's argument is "otherid"; our literal "mywidget" is unrelated → surfaces.
			Assert.Equal(new[] { "mywidget" }, Findings("mywidget", "document.getElementById(\"otherid\"); var s=\"mywidget\";"));
		}

		[Fact]
		public void OwnLookupAmongMany_Suppressed()
		{
			// Two lookups in the window; our literal has its own → suppressed.
			Assert.Empty(Findings("mywidget", "var a=document.getElementById(\"otherid\"),b=document.getElementById(\"mywidget\");"));
		}

		// ── null / empty context is safe (guard declines, literal surfaces) ──

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("var s = \"mywidget\";")] // present but no lookup signature
		public void NoSignatureContext_TokenSurfaces(string? context)
		{
			Assert.Equal(new[] { "mywidget" }, Findings("mywidget", context));
		}
	}
}
