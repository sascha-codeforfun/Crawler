using System.Collections.Generic;
using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery: the finding spine + checker wiring. The actual dictionary/compound logic is
	/// injected as a RunCheck delegate, so these tests use a FAKE checker and need no Hunspell
	/// runtime or dictionary files. They verify the part the new module owns: canonicalize →
	/// check → map each miss back to a real span on the originating node, with no word-keyed
	/// map and no first-wins race.
	///
	/// The production adapter (ToolsSpellChecker) wraps Tools.CheckSpelling verbatim and is
	/// covered by the solution build, not here.
	/// </summary>
	public class SpellCheckRunCheckerTests
	{
		private static TextRun TextRun(string text) =>
			new(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text);

		// A fake checker that flags exactly the words it is told to, one miss per DISTINCT word
		// (matching the real checker, which keys results by word).
		private static RunCheck FlagWords(params string[] words)
		{
			var set = new HashSet<string>(words);
			return (canonicalText, lang) =>
				SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", canonicalText))
					.Select(t => t.Text)
					.Where(set.Contains)
					.Distinct()
					.Select(w => new CheckMiss(w, "suggestion"));
		}

		[Fact]
		public void Emits_NoFindings_WhenCheckerReportsNoMisses()
		{
			var findings = RunChecker.Check(TextRun("alles korrekt hier"), "de", (_, _) => Enumerable.Empty<CheckMiss>()).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void Emits_Finding_WithRealSpan_ForMissedWord()
		{
			var run = TextRun("Guten Morgan zusammen");
			var findings = RunChecker.Check(run, "de", FlagWords("Morgan")).ToList();

			var f = Assert.Single(findings);
			Assert.Equal("Morgan", f.Word);
			Assert.Equal("de", f.Language);
			Assert.Equal(RunSource.TextNode, f.Source);
			Assert.Equal("p[#text]", f.SourcePath);
			Assert.Same(run.Node, f.Node);
			// span must reproduce the word from the canonical run text
			Assert.Equal(6, f.Start);
			Assert.Equal("Morgan".Length, f.Length);
		}

		[Fact]
		public void Emits_OneFindingPerOccurrence_EachWithOwnSpan()
		{
			// A word repeated in a run yields a finding PER OCCURRENCE, each with its own span.
			// Every occurrence is a distinct, separately discoverable place to fix — the checker
			// gives the word verdict, the tokenizer re-expands to located occurrences.
			var run = TextRun("Fehler und nochmal Fehler");
			var findings = RunChecker.Check(run, "de", FlagWords("Fehler")).ToList();

			Assert.Equal(2, findings.Count);
			Assert.Equal(0, findings[0].Start);
			Assert.Equal(19, findings[1].Start);
			Assert.All(findings, f => Assert.Equal("Fehler", f.Word));
			Assert.All(findings, f => Assert.Same(run.Node, f.Node));
		}

		[Fact]
		public void Canonicalizes_BeforeChecking_SoSoftHyphenWordIsWhole()
		{
			// Word carries a soft hyphen in the raw text; canonicalization removes it before the
			// checker sees it, so the flagged word and its span are the whole word.
			var run = TextRun("Ver\u00ADtrog");
			var findings = RunChecker.Check(run, "de", FlagWords("Vertrog")).ToList();

			var f = Assert.Single(findings);
			Assert.Equal("Vertrog", f.Word);
			Assert.Equal(0, f.Start);
		}

		[Fact]
		public void EmptyRun_YieldsNoFindings()
		{
			var findings = RunChecker.Check(TextRun("   "), "de", FlagWords("anything")).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void PreservesAttributeSource_OnFinding()
		{
			var run = new TextRun(HtmlNode.CreateNode("<img>"), RunSource.Attribute, "img[@alt]", "Bidl Beschreibung");
			var findings = RunChecker.Check(run, "de", FlagWords("Bidl")).ToList();

			var f = Assert.Single(findings);
			Assert.Equal(RunSource.Attribute, f.Source);
			Assert.Equal("img[@alt]", f.SourcePath);
		}

		// --- value-classifier gate: attribute/meta runs must clear the classifier before checking.
		// These guard the seam that, unwired, flagged every high-entropy token on the site.

		[Fact]
		public void AttributeRun_VeryHighEntropyToken_IsGatedOut_NoFinding()
		{
			// A token whose entropy exceeds the classifier's threshold (every char distinct) is
			// gated out by shape. (Short random salts BELOW the threshold are handled separately,
			// by the operator-declared attribute skip-list in the traverser — not here.)
			var run = new TextRun(HtmlNode.CreateNode("<div>"), RunSource.Attribute, "div[@data-x]", "aB3xK9mZ7qWpL2vN");
			var findings = RunChecker.Check(run, "de", FlagWords("aB3xK9mZ7qWpL2vN")).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void AttributeRun_UrlValue_IsGatedOut_NoFinding()
		{
			var run = new TextRun(HtmlNode.CreateNode("<a>"), RunSource.Attribute, "a[@href]", "https://example.com/some/deep/path");
			var findings = RunChecker.Check(run, "de", FlagWords("example", "deep", "path")).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void AttributeRun_ProseValue_PassesGate_AndIsChecked()
		{
			// A genuine prose attribute (alt text) must still be checked.
			var run = new TextRun(HtmlNode.CreateNode("<img>"), RunSource.Attribute, "img[@alt]", "Ein Bidl Beschreibung hier");
			var findings = RunChecker.Check(run, "de", FlagWords("Bidl")).ToList();
			Assert.Single(findings);
		}

		[Fact]
		public void TextNodeRun_AlwaysChecked_EvenIfClassifierWouldSkip()
		{
			// Text nodes are prose by construction — never gated, even if the raw text looks tokeny.
			var run = new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", "Fehla im Satz");
			var findings = RunChecker.Check(run, "de", FlagWords("Fehla")).ToList();
			Assert.Single(findings);
		}

		// Language-aware fake: each language flags a different word set, so union behaviour is testable.
		private static RunCheck FlagPerLanguage(Dictionary<string, HashSet<string>> byLang)
		{
			return (canonicalText, lang) =>
			{
				var set = byLang.TryGetValue(lang, out var s) ? s : new HashSet<string>();
				return SpellTokenizer.Tokenize(new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", canonicalText))
					.Select(t => t.Text)
					.Where(set.Contains)
					.Distinct()
					.Select(w => new CheckMiss(w, "s"));
			};
		}

		[Fact]
		public void Union_WordAcceptedByOneLanguage_NotFlagged()
		{
			// "color" is a miss in German but a valid English word. On a [de,en] page it must PASS
			// (union: accepted by any dictionary). "Xqz" is a miss in both → flagged.
			var run = TextRun("color Xqz");
			var check = FlagPerLanguage(new()
			{
				["de"] = new() { "color", "Xqz" },
				["en"] = new() { "Xqz" }, // en accepts "color"
			});

			var findings = RunChecker.Check(run, new[] { "de", "en" }, check).ToList();

			Assert.Single(findings);
			Assert.Equal("Xqz", findings[0].Word);
		}

		[Fact]
		public void Union_WordMissedByAllLanguages_FlaggedAndTaggedWithSet()
		{
			// A word missed by every language is flagged, and tagged with the full set checked.
			var run = TextRun("Wörrd");
			var check = FlagPerLanguage(new()
			{
				["de"] = new() { "Wörrd" },
				["en"] = new() { "Wörrd" },
				["fr"] = new() { "Wörrd" },
			});

			var findings = RunChecker.Check(run, new[] { "de", "en", "fr" }, check).ToList();

			var f = Assert.Single(findings);
			Assert.Equal("Wörrd", f.Word);
			Assert.Equal("de, en, fr", f.Language); // full failed set, in declared order
		}

		[Fact]
		public void Union_SingleLanguageList_BehavesLikeScalarOverload()
		{
			var run = TextRun("Guten Morgan");
			var viaList = RunChecker.Check(run, new[] { "de" }, FlagWords("Morgan")).ToList();
			var viaScalar = RunChecker.Check(run, "de", FlagWords("Morgan")).ToList();

			Assert.Single(viaList);
			Assert.Equal(viaScalar.Single().Word, viaList.Single().Word);
			Assert.Equal("de", viaList.Single().Language);
		}

		[Fact]
		public void KnownDefect_MutesDeclaredWord_FromDeclaredPath()
		{
			var run = new TextRun(HtmlNode.CreateNode("<div>x</div>"), RunSource.Attribute,
				"div[@data-pagenav-title]", "Springe zu Modernising");
			var known = new KnownDefectMatcher(new Dictionary<string, List<string>>
			{
				["div[@data-pagenav-title]"] = new() { "Springe zu*" },
			});

			// Checker flags all three words; the known-defect filter must mute Springe/zu but KEEP
			// Modernising (the varying per-page title tail).
			var findings = RunChecker.Check(run, new[] { "de" }, FlagWords("Springe", "zu", "Modernising"), known).ToList();

			Assert.DoesNotContain(findings, f => f.Word == "Springe");
			Assert.DoesNotContain(findings, f => f.Word == "zu");
			Assert.Contains(findings, f => f.Word == "Modernising");
		}

		[Fact]
		public void KnownDefect_DoesNotMute_SameWordFromDifferentPath()
		{
			var run = new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode,
				"p[#text]", "Seiteninhalt");
			var known = new KnownDefectMatcher(new Dictionary<string, List<string>>
			{
				["div[@data-pagenav-global-label]"] = new() { "Seiteninhalt" },
			});

			// "Seiteninhalt" in body text (different path) is NOT muted — only from the declared element.
			var findings = RunChecker.Check(run, new[] { "de" }, FlagWords("Seiteninhalt"), known).ToList();
			Assert.Single(findings);
		}

		// ---- 606: the HeuristicNonProseDataAttributeSuppression switch, scoped to data-* runs.
		// Synthetic neutral values; an attribute run carries a data-* SourcePath.

		private static TextRun DataAttrRun(string value) =>
			new(HtmlNode.CreateNode("<div>x</div>"), RunSource.Attribute, "div[@data-sample]", value);

		[Fact]
		public void Switch_Off_ChecksDataAttributeSlug_As605()
		{
			// With the switch off, a machine-slug data-* value flows to the checker (605 behaviour):
			// the fake checker flags it, so a finding is emitted.
			var findings = RunChecker.Check(DataAttrRun("showWidget"), new[] { "de" }, FlagWords("showWidget"),
				knownDefects: null, heuristicNonProseDataAttributeSuppression: false).ToList();
			Assert.Single(findings);
		}

		[Fact]
		public void Switch_On_SkipsDataAttributeSlug()
		{
			// With the switch on, the same slug is shape-skipped before the checker ever sees it.
			var findings = RunChecker.Check(DataAttrRun("showWidget"), new[] { "de" }, FlagWords("showWidget"),
				knownDefects: null, heuristicNonProseDataAttributeSuppression: true).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void Switch_On_StillChecksProseDataAttributeValue()
		{
			// A prose data-* value (multi-word, no machine shape) stays checked even with the switch
			// on — an unforeseen prose data-* surfaces rather than being silently swallowed.
			var findings = RunChecker.Check(DataAttrRun("echte saubere Worte"), new[] { "de" }, FlagWords("saubere"),
				knownDefects: null, heuristicNonProseDataAttributeSuppression: true).ToList();
			Assert.Single(findings);
		}

		[Fact]
		public void Switch_On_DoesNotAffectNonDataAttribute()
		{
			// The heuristic is scoped to data-* runs: a slug-shaped value in a NON-data attribute is
			// unaffected by the switch (only the universal base gate applies, which checks it).
			var run = new TextRun(HtmlNode.CreateNode("<div>x</div>"), RunSource.Attribute, "div[@custom-attr]", "showWidget");
			var findings = RunChecker.Check(run, new[] { "de" }, FlagWords("showWidget"),
				knownDefects: null, heuristicNonProseDataAttributeSuppression: true).ToList();
			Assert.Single(findings);
		}

		// ---- 607: name-guarded positional keyword suppression (align tell + exact keyword).

		private static TextRun AlignAttrRun(string value) =>
			new(HtmlNode.CreateNode("<div>x</div>"), RunSource.Attribute, "div[@data-data-align]", value);

		[Fact]
		public void Align_On_SuppressesExactPositionalKeyword()
		{
			// "center" in an align-named data-* attribute is a correctly-spelled English positional
			// word; with the switch on it is suppressed (no finding).
			var findings = RunChecker.Check(AlignAttrRun("center"), new[] { "de" }, FlagWords("center"),
				knownDefects: null, heuristicNonProseDataAttributeSuppression: true).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void Align_On_SuppressesTitleCasePositionalKeyword()
		{
			var findings = RunChecker.Check(AlignAttrRun("Center"), new[] { "de" }, FlagWords("Center"),
				knownDefects: null, heuristicNonProseDataAttributeSuppression: true).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void Align_On_StillChecksMisspelledPositionalValue()
		{
			// THE safety test: a misspelling in an align attribute is NOT a set member, so it stays
			// checked and surfaces as a finding. The rule only ever drops correctly-spelled words.
			var findings = RunChecker.Check(AlignAttrRun("middel"), new[] { "de" }, FlagWords("middel"),
				knownDefects: null, heuristicNonProseDataAttributeSuppression: true).ToList();
			Assert.Single(findings);
		}

		[Fact]
		public void Align_On_StillChecksOddCasePositionalValue()
		{
			// All-caps is an odd case the positional rule rejects (not an accepted rendering) and the
			// slug rule ignores (no lower→upper transition / digit / dot), so it stays checked. (A
			// lower→upper odd case like "cEnter" is instead caught upstream by the 606 slug shape —
			// that path is covered by the slug tests; here we assert the positional-reject path.)
			var findings = RunChecker.Check(AlignAttrRun("CENTER"), new[] { "de" }, FlagWords("CENTER"),
				knownDefects: null, heuristicNonProseDataAttributeSuppression: true).ToList();
			Assert.Single(findings);
		}

		[Fact]
		public void Align_On_DoesNotSuppressKeyword_WhenNameLacksAlignTell()
		{
			// Same value, but the attribute name has no "align" tell — the name guard means the
			// value alone never suppresses, so "center" stays checked.
			var run = new TextRun(HtmlNode.CreateNode("<div>x</div>"), RunSource.Attribute, "div[@data-color]", "center");
			var findings = RunChecker.Check(run, new[] { "de" }, FlagWords("center"),
				knownDefects: null, heuristicNonProseDataAttributeSuppression: true).ToList();
			Assert.Single(findings);
		}

		[Fact]
		public void Align_Off_ChecksPositionalKeyword_As605()
		{
			// With the switch off, the positional keyword flows to the checker (605 behaviour).
			var findings = RunChecker.Check(AlignAttrRun("center"), new[] { "de" }, FlagWords("center"),
				knownDefects: null, heuristicNonProseDataAttributeSuppression: false).ToList();
			Assert.Single(findings);
		}

		[Fact]
		public void Switch_On_NullSourcePath_TreatedAsNonData_DoesNotThrow()
		{
			// Defensive: a run with a null SourcePath must not crash the data-* routing — it is
			// simply treated as non-data and checked by the universal gate. (608 feeds more data
			// through this path, so the null guard is worth pinning.)
			var run = new TextRun(HtmlNode.CreateNode("<div>x</div>"), RunSource.Attribute, null!, "showWidget");
			var findings = RunChecker.Check(run, new[] { "de" }, FlagWords("showWidget"),
				knownDefects: null, heuristicNonProseDataAttributeSuppression: true).ToList();
			Assert.Single(findings);
		}

		// ---- 609: WORD_COLLISION cross-pass dedup — mute the merged twin a text run produces.

		private static TextRun TextNodeRun(string text) =>
			new(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", text);

		[Fact]
		public void Collision_Mutes_SeamTwinToken()
		{
			// The merged token is in the file's collision set → muted (CQ reports the defect).
			var collision = new HashSet<string>(new[] { "betaGamma" }, System.StringComparer.Ordinal);
			var findings = RunChecker.Check(TextNodeRun("alpha betaGamma omega"), new[] { "de" },
				FlagWords("betaGamma"), knownDefects: null,
				heuristicNonProseDataAttributeSuppression: false, collisionWords: collision).ToList();
			Assert.Empty(findings);
		}

		[Fact]
		public void Collision_Null_EmitsTwin_As605()
		{
			// No collision set → the merged token flows through as a normal finding.
			var findings = RunChecker.Check(TextNodeRun("alpha betaGamma omega"), new[] { "de" },
				FlagWords("betaGamma"), knownDefects: null,
				heuristicNonProseDataAttributeSuppression: false, collisionWords: null).ToList();
			Assert.Single(findings);
		}

		[Fact]
		public void Collision_DoesNotMute_OtherWordsInSameRun()
		{
			// A genuine typo sharing the run is NOT in the collision set → it still surfaces.
			var collision = new HashSet<string>(new[] { "betaGamma" }, System.StringComparer.Ordinal);
			var findings = RunChecker.Check(TextNodeRun("alpha betaGamma omega"), new[] { "de" },
				FlagWords("alpha"), knownDefects: null,
				heuristicNonProseDataAttributeSuppression: false, collisionWords: collision).ToList();
			Assert.Single(findings);
		}
	}
}
