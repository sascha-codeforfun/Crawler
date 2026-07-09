using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 629: the optional script-only fallback dictionary
	/// (SpellCheckJavaScript.ScriptFallbackDictionary). A token inside an inline-script literal that
	/// fails the page's language(s) but is valid in the fallback dictionary is not flagged — but ONLY
	/// for script runs, and only as a removal (it can never create a finding). Prose stays held to the
	/// page's languages. Uses a fake RunCheck (no Hunspell). All fixtures synthetic and generic.
	/// </summary>
	public class RunCheckerScriptFallbackTests
	{
		// Multi-word literal so the script value-gate treats it as prose and hands it to the tokenizer.
		private static TextRun ScriptRun(string text) =>
			new(HtmlNode.CreateNode("<script></script>"), RunSource.Script, "script[L1:1]", text);

		private static TextRun TextNodeRun(string text) =>
			new(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text);

		// Language-aware fake: each language flags a different word set, so the fallback union is testable.
		private static RunCheck FlagPerLanguage(Dictionary<string, HashSet<string>> byLang) =>
			(canonicalText, lang) =>
			{
				var set = byLang.TryGetValue(lang, out var s) ? s : new HashSet<string>();
				return SpellTokenizer
					.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", canonicalText))
					.Select(t => t.Text)
					.Where(set.Contains)
					.Distinct()
					.Select(w => new CheckMiss(w, "s"));
			};

		[Fact]
		public void ScriptRun_TokenAcceptedByFallback_IsSuppressed_TagStaysPageLanguage()
		{
			// "shopping" fails German but is valid English; "Fehlerwort" fails both. On a [de] page with
			// an "en" fallback, only "Fehlerwort" survives, and it stays tagged "de" (not "de, en").
			var run = ScriptRun("willkommen shopping Fehlerwort");
			var check = FlagPerLanguage(new()
			{
				["de"] = new() { "shopping", "Fehlerwort" },
				["en"] = new() { "Fehlerwort" }, // en accepts "shopping"
			});

			var findings = RunChecker.Check(run, new[] { "de" }, check, scriptFallbackLanguage: "en").ToList();

			Assert.Single(findings);
			Assert.Equal("Fehlerwort", findings[0].Word);
			Assert.Equal("de", findings[0].Language);
		}

		[Fact]
		public void ScriptRun_NoFallback_ForeignTokenStillFlagged()
		{
			var run = ScriptRun("willkommen shopping Fehlerwort");
			var check = FlagPerLanguage(new()
			{
				["de"] = new() { "shopping", "Fehlerwort" },
				["en"] = new() { "Fehlerwort" },
			});

			// No fallback supplied → "shopping" is not rescued.
			var findings = RunChecker.Check(run, new[] { "de" }, check).ToList();

			Assert.Equal(2, findings.Count);
			Assert.Contains(findings, f => f.Word == "shopping");
		}

		[Fact]
		public void ScriptRun_TokenMissedByFallbackToo_StillFlagged()
		{
			// A genuine non-word fails the page language AND the fallback → still flagged.
			var run = ScriptRun("hallo welt Fehlerwort");
			var check = FlagPerLanguage(new()
			{
				["de"] = new() { "Fehlerwort" },
				["en"] = new() { "Fehlerwort" },
			});

			var findings = RunChecker.Check(run, new[] { "de" }, check, scriptFallbackLanguage: "en").ToList();

			Assert.Single(findings);
			Assert.Equal("Fehlerwort", findings[0].Word);
		}

		[Fact]
		public void NonScriptRun_FallbackNotApplied()
		{
			// Same content as a TEXT NODE: the fallback is script-scoped, so "shopping" is NOT rescued —
			// prose stays held to the page's declared language.
			var run = TextNodeRun("willkommen shopping Fehlerwort");
			var check = FlagPerLanguage(new()
			{
				["de"] = new() { "shopping", "Fehlerwort" },
				["en"] = new() { "Fehlerwort" },
			});

			var findings = RunChecker.Check(run, new[] { "de" }, check, scriptFallbackLanguage: "en").ToList();

			Assert.Equal(2, findings.Count);
			Assert.Contains(findings, f => f.Word == "shopping");
		}
	}
}
