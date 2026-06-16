using System.Linq;
using System.Text;
using Crawler.SpellCheck;
using Crawler.Boilerplate;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 1 of the spell-check redesign: the data spine + DOM traversal.
	/// Verifies that the DOM becomes a uniform, source-tagged run stream and that
	/// tokens retain a faithful character span back into their run. No spell checking,
	/// no chrome classification, no canonicalization yet — those are later stages.
	///
	/// Traversal is tested against a pre-loaded HtmlDocument so the unit under test is
	/// the walk itself, not encoding detection. Decoding (DetectEncoding.FromBytes) is
	/// covered by a single, provider-independent test that carries an explicit charset
	/// (resolving to built-in UTF-8 via the meta branch, never the 1252 codepage that
	/// requires a registered CodePages provider not present in the test host).
	///
	/// Key invariant under test: attributes are emitted REGARDLESS of name (no allow/block
	/// list), and every run is bound to its node so provenance is intrinsic.
	/// </summary>
	public class SpellCheckDomTraverserTests
	{
		private static HtmlDocument Doc(string html)
		{
			var doc = new HtmlDocument();
			doc.LoadHtml(html);
			return doc;
		}

		[Fact]
		public void Traverse_EmitsTextNodeRun_WithParentTagLabel()
		{
			var runs = DomTraverser.Traverse(Doc("<html><body><p>Hallo Welt</p></body></html>")).ToList();

			var textRun = runs.Single(r => r.Source == RunSource.TextNode && r.RawText.Contains("Hallo"));
			Assert.Equal("p[#text]", textRun.SourcePath);
			Assert.NotNull(textRun.Node);
		}

		[Fact]
		public void Traverse_EmitsAttributeRun_ForAnyAttribute_NotFilteredByName()
		{
			// Both a "known prose" attribute (alt) and an arbitrary data-* must appear:
			// inclusion is by presence of a value, never by attribute name.
			var runs = DomTraverser.Traverse(Doc("<img alt=\"Ein Bild\" data-frei=\"frei erfunden\">")).ToList();

			Assert.Contains(runs, r => r.Source == RunSource.Attribute && r.SourcePath == "img[@alt]");
			Assert.Contains(runs, r => r.Source == RunSource.Attribute && r.SourcePath == "img[@data-frei]");
		}

		[Fact]
		public void Traverse_SkipsDeclaredAttributeNames_ButKeepsOthers()
		{
			// A declared technical attribute (salt) is dropped; sibling prose attributes survive.
			var skip = new HashSet<string>(new[] { "salt", "proof" }, System.StringComparer.OrdinalIgnoreCase);
			var html = "<div salt=\"Xy7Qm\" proof=\"000111\" note=\"show note\">x</div>";

			var runs = DomTraverser.Traverse(Doc(html), skip).ToList();

			Assert.DoesNotContain(runs, r => r.SourcePath == "div[@salt]");
			Assert.DoesNotContain(runs, r => r.SourcePath == "div[@proof]");
			Assert.Contains(runs, r => r.SourcePath == "div[@note]"); // prose attr kept
		}

		[Fact]
		public void Traverse_SkipAttributeNames_CaseInsensitive()
		{
			// Declared "SALT" (upper) must match the element's "salt" (lower) — names are folded.
			var skip = new HashSet<string>(new[] { "SALT" }, System.StringComparer.OrdinalIgnoreCase);
			var runs = DomTraverser.Traverse(Doc("<div salt=\"Xy7Qm\">x</div>"), skip).ToList();
			Assert.DoesNotContain(runs, r => r.SourcePath == "div[@salt]");
		}

		[Fact]
		public void Traverse_SkipAttributeNames_AppliedOnEntryPagePathToo()
		{
			// Entry/no-boilerplate pages route through the base walk — the skip must still apply,
			// since technical attributes (salts) appear on every page including entry pages.
			var skip = new HashSet<string>(new[] { "salt" }, System.StringComparer.OrdinalIgnoreCase);
			var runs = DomTraverser.Traverse(Doc("<div salt=\"Xy7Qm\">x</div>"), matcher: null, isEntryPage: true, skipAttributeNames: skip).ToList();
			Assert.DoesNotContain(runs, r => r.SourcePath == "div[@salt]");
		}

		[Fact]
		public void DefaultNonProseAttributes_AreClassIdStyle_CaseInsensitive()
		{
			Assert.Contains("class", DomTraverser.DefaultNonProseAttributes);
			Assert.Contains("id", DomTraverser.DefaultNonProseAttributes);
			Assert.Contains("style", DomTraverser.DefaultNonProseAttributes);
			Assert.Contains("CLASS", DomTraverser.DefaultNonProseAttributes); // case-insensitive set
		}

		[Fact]
		public void Traverse_WithDefaultNonProseSet_SkipsClassValue_KeepsAltProse()
		{
			// Using the built-in default set, a class value (never prose) is dropped while real
			// alt prose survives — this is the class-name noise case (CSS classes are never prose).
			var html = "<img class=\"widget-foo bar-baz\" alt=\"Ein Bidl\">";
			var runs = DomTraverser.Traverse(Doc(html), DomTraverser.DefaultNonProseAttributes).ToList();

			Assert.DoesNotContain(runs, r => r.SourcePath == "img[@class]");
			Assert.Contains(runs, r => r.SourcePath == "img[@alt]"); // prose kept
		}

		[Fact]
		public void Traverse_SkipsScriptContent_ByDefault()
		{
			var html = "<html><body><script>var fehla = document.getElementById('x');</script><p>Echter Text</p></body></html>";
			var runs = DomTraverser.Traverse(Doc(html)).ToList();

			Assert.DoesNotContain(runs, r => r.Source == RunSource.TextNode && r.RawText.Contains("getElementById"));
			Assert.Contains(runs, r => r.Source == RunSource.TextNode && r.RawText.Contains("Echter")); // real prose kept
		}

		[Fact]
		public void Traverse_ChecksScriptContent_WhenEnabled()
		{
			// With script checking on, string LITERALS are lifted as Script runs (decoded, located) —
			// not the raw JS body as a text run. An identifier/number-only statement yields nothing;
			// a string literal yields a Script run carrying its decoded text.
			var html = "<html><body><script>var msg = 'echter fehler';</script></body></html>";
			var runs = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: true).ToList();

			Assert.Contains(runs, r => r.Source == RunSource.Script && r.RawText == "echter fehler");
			Assert.DoesNotContain(runs, r => r.Source == RunSource.TextNode && r.RawText.Contains("msg"));
		}

		[Fact]
		public void Traverse_AlwaysSkipsStyleContent_RegardlessOfJavaScriptFlag()
		{
			var html = "<html><head><style>.a{display:none;color:red}</style></head><body><p>Echt</p></body></html>";

			var off = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: false).ToList();
			var on = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: true).ToList();

			Assert.DoesNotContain(off, r => r.RawText.Contains("display"));
			Assert.DoesNotContain(on, r => r.RawText.Contains("display")); // style skipped even with JS on
			Assert.Contains(off, r => r.RawText.Contains("Echt"));
		}

		[Fact]
		public void Traverse_SkipsEventHandlerAttributes_WhenJavaScriptOff()
		{
			var html = "<button onclick=\"alert('x')\" onmouseover=\"doThing()\">Klick</button>";
			var runs = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: false).ToList();

			Assert.DoesNotContain(runs, r => r.SourcePath == "button[@onclick]");
			Assert.DoesNotContain(runs, r => r.SourcePath == "button[@onmouseover]");
			Assert.Contains(runs, r => r.Source == RunSource.TextNode && r.RawText.Contains("Klick")); // label prose kept
		}

		[Fact]
		public void Traverse_LexesEventHandlerLiterals_WhenJavaScriptOn()
		{
			// When JS checking is on, an on* handler is treated as JavaScript: its string literals
			// are lifted by the lexer and emitted as Script runs located at the handler attribute,
			// while the handler CODE (calls/identifiers) is discarded. (Previously the raw handler
			// value was emitted as one Attribute run; the JS switch now routes it through the lexer.)
			var html = "<button onclick=\"alert('x')\">Klick</button>";
			var runs = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: true).ToList();

			var run = Assert.Single(runs, r => r.SourcePath == "button[@onclick]");
			Assert.Equal(RunSource.Script, run.Source);
			Assert.Equal("x", run.RawText);
		}

		[Fact]
		public void Traverse_EventHandlerMatchIsExact_NotOnPrefix()
		{
			// "ontology" and "once" merely START with "on" — they are NOT event handlers and must
			// ALWAYS be emitted (checked), even with JavaScript off. Guards against prefix-matching.
			var html = "<div ontology=\"Eine Beschreibung\" once=\"true\">x</div>";
			var runs = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: false).ToList();

			Assert.Contains(runs, r => r.SourcePath == "div[@ontology]");
			Assert.Contains(runs, r => r.SourcePath == "div[@once]");
		}

		[Fact]
		public void Traverse_SkipsEmptyAndWhitespaceOnlyValues()
		{
			var runs = DomTraverser.Traverse(Doc("<div data-empty=\"\" data-blank=\"   \">text</div>")).ToList();

			Assert.DoesNotContain(runs, r => r.SourcePath == "div[@data-empty]");
			Assert.DoesNotContain(runs, r => r.SourcePath == "div[@data-blank]");
			Assert.Contains(runs, r => r.Source == RunSource.TextNode && r.RawText.Contains("text"));
		}

		[Fact]
		public void Parse_DecodesViaMetaCharset_AndTraverse_EmitsMetaContentRun_WithNameLabel()
		{
			// charset=utf-8 makes FromBytes resolve through the meta branch (built-in UTF-8),
			// so this exercises the real decode->parse->traverse path without depending on the
			// 1252 codepage provider.
			var html = "<html><head><meta charset=\"utf-8\">"
				+ "<meta name=\"description\" content=\"Eine Beschreibung\"></head><body></body></html>";
			var runs = DomTraverser.RunsFromBytes(Encoding.UTF8.GetBytes(html)).ToList();

			Assert.Contains(runs, r => r.Source == RunSource.Meta && r.SourcePath == "meta[@name=description]");
		}

		[Fact]
		public void Traverse_MetaDefault_ChecksDescriptionAndKeywords_DitchesTechnicalMeta()
		{
			var html = "<html><head>"
				+ "<meta name=\"description\" content=\"Eine Beschreibung\">"
				+ "<meta name=\"keywords\" content=\"Kaufen Mieten\">"
				+ "<meta name=\"viewport\" content=\"width=device-width\">"
				+ "<meta name=\"robots\" content=\"noindex,follow\">"
				+ "<meta http-equiv=\"content-type\" content=\"text/html; charset=UTF-8\">"
				+ "</head><body></body></html>";
			var runs = DomTraverser.Traverse(Doc(html)).ToList();

			Assert.Contains(runs, r => r.SourcePath == "meta[@name=description]");
			Assert.Contains(runs, r => r.SourcePath == "meta[@name=keywords]");
			Assert.DoesNotContain(runs, r => r.SourcePath == "meta[@name=viewport]");   // device-width ditched
			Assert.DoesNotContain(runs, r => r.SourcePath == "meta[@name=robots]");     // noindex ditched
			Assert.DoesNotContain(runs, r => r.Source == RunSource.Meta && r.RawText.Contains("text/html")); // nameless/http-equiv content ditched
			Assert.DoesNotContain(runs, r => r.SourcePath == "meta[@http-equiv]");      // http-equiv ATTR value (content-type) skipped
			Assert.DoesNotContain(runs, r => r.RawText == "content-type");              // the actual leak: no content-type word anywhere
		}

		[Fact]
		public void Traverse_MetaCharsetAndProperty_AttributesSkipped()
		{
			// All non-content meta attributes are technical, never prose: charset, property, name value.
			var html = "<html><head>"
				+ "<meta charset=\"utf-8\">"
				+ "<meta property=\"og:type\" content=\"website\">"  // property not on name-allowlist → content ditched too
				+ "</head><body></body></html>";
			var runs = DomTraverser.Traverse(Doc(html)).ToList();

			Assert.DoesNotContain(runs, r => r.SourcePath == "meta[@charset]");
			Assert.DoesNotContain(runs, r => r.SourcePath == "meta[@property]");
			Assert.DoesNotContain(runs, r => r.RawText.Contains("website")); // no allowlisted name → content ditched
		}

		[Fact]
		public void Tokenize_EmailInText_SuppressesFragments_KeepsSurroundingProse()
		{
			// "name@example.de" shown as visible text must not split into name/example/de. Surrounding
			// German prose is still checked. '@' is unambiguous → zero false-positive risk.
			var run = new TextRun(null!, RunSource.TextNode, "a[#text]",
				"Schreiben Sie an kontakt@beispiel-firma.de bezüglich Ihrer Anfrage.");
			var tokens = SpellTokenizer.Tokenize(run).Select(t => t.Text).ToList();

			Assert.DoesNotContain("kontakt", tokens);
			Assert.DoesNotContain("beispiel-firma", tokens);
			Assert.DoesNotContain("de", tokens);
			Assert.Contains("Schreiben", tokens);  // surrounding prose kept
			Assert.Contains("bezüglich", tokens);
			Assert.Contains("Anfrage", tokens);
		}

		[Fact]
		public void Tokenize_NoEmail_AllWordsRetained()
		{
			// Guard: ordinary prose (no '@') is tokenized exactly as before — email scan changes nothing.
			var run = new TextRun(null!, RunSource.TextNode, "p[#text]", "Ein einfacher deutscher Satz.");
			var tokens = SpellTokenizer.Tokenize(run).Select(t => t.Text).ToList();

			Assert.Contains("Ein", tokens);
			Assert.Contains("einfacher", tokens);
			Assert.Contains("deutscher", tokens);
			Assert.Contains("Satz", tokens);
		}

		[Fact]
		public void Tokenize_UrlForms_Suppressed_KeepsSurroundingProse()
		{
			// www., scheme, and bare-TLD-anchored domains in visible text must not split into fake
			// words; the surrounding German prose stays checked.
			var run = new TextRun(null!, RunSource.TextNode, "a[#text]",
				"Diese können unter www.beispiel.de heruntergeladen werden.");
			var tokens = SpellTokenizer.Tokenize(run).Select(t => t.Text).ToList();

			Assert.DoesNotContain("www", tokens);
			Assert.DoesNotContain("beispiel", tokens);
			Assert.DoesNotContain("de", tokens);
			Assert.Contains("können", tokens);            // surrounding prose kept
			Assert.Contains("heruntergeladen", tokens);
			Assert.Contains("werden", tokens);
		}

		[Fact]
		public void Tokenize_BareDomain_NoWww_Suppressed()
		{
			// Bare domain without www/scheme, anchored on a known TLD (.de).
			var run = new TextRun(null!, RunSource.TextNode, "a[#text]", "Mehr auf beispielportal.de erfahren.");
			var tokens = SpellTokenizer.Tokenize(run).Select(t => t.Text).ToList();

			Assert.DoesNotContain("beispielportal", tokens);
			Assert.Contains("Mehr", tokens);
			Assert.Contains("auf", tokens);
			Assert.Contains("erfahren", tokens);
		}

		[Fact]
		public void Tokenize_SchemeUrl_WithPath_Suppressed()
		{
			var run = new TextRun(null!, RunSource.TextNode, "a[#text]",
				"Siehe https://www.beispiel.de/pfad/seite.html für Details.");
			var tokens = SpellTokenizer.Tokenize(run).Select(t => t.Text).ToList();

			Assert.DoesNotContain("https", tokens);
			Assert.DoesNotContain("beispiel", tokens);
			Assert.DoesNotContain("pfad", tokens);
			Assert.Contains("Siehe", tokens);
			Assert.Contains("Details", tokens);
		}

		[Fact]
		public void Tokenize_GermanAbbreviation_NotEatenAsDomain()
		{
			// CRITICAL FP guard: "z.B." and "e.g." look domain-ish (word.word) but the trailing
			// segment is NOT a known TLD, so the TLD-anchored pattern must NOT match them. The real
			// words around them, and the abbreviation letters, must still tokenize.
			var run = new TextRun(null!, RunSource.TextNode, "p[#text]",
				"Nutzen Sie z.B. die App, d.h. die mobile Version, e.g. unterwegs.");
			var tokens = SpellTokenizer.Tokenize(run).Select(t => t.Text).ToList();

			Assert.Contains("Nutzen", tokens);
			Assert.Contains("App", tokens);
			Assert.Contains("mobile", tokens);
			Assert.Contains("Version", tokens);
			Assert.Contains("unterwegs", tokens);
			// the abbreviation letters survive (z / B / d / h / e / g not swallowed as a domain)
			Assert.Contains("z", tokens);
			Assert.Contains("d", tokens);
		}

		[Fact]
		public void Tokenize_SentenceBoundary_NotEatenAsDomain()
		{
			// "...werden. Diese..." — a period between two words at a sentence boundary must NOT be
			// read as a domain (Diese is not a TLD). Both words survive.
			var run = new TextRun(null!, RunSource.TextNode, "p[#text]",
				"Das wird durchgeführt werden. Diese Angaben sind wichtig.");
			var tokens = SpellTokenizer.Tokenize(run).Select(t => t.Text).ToList();

			Assert.Contains("werden", tokens);
			Assert.Contains("Diese", tokens);
			Assert.Contains("Angaben", tokens);
			Assert.Contains("wichtig", tokens);
		}

		[Fact]
		public void Traverse_MetaOverride_ReplacesDefault_Entirely()
		{
			var html = "<html><head>"
				+ "<meta name=\"description\" content=\"Eine Beschreibung\">"
				+ "<meta name=\"keywords\" content=\"Fahren Fliegen\">"
				+ "</head><body></body></html>";
			var only = new HashSet<string>(new[] { "description" }, System.StringComparer.OrdinalIgnoreCase);
			var runs = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: false, metaContentNames: only).ToList();

			Assert.Contains(runs, r => r.SourcePath == "meta[@name=description]");
			Assert.DoesNotContain(runs, r => r.SourcePath == "meta[@name=keywords]"); // override dropped keywords
		}

		[Fact]
		public void Traverse_MetaOverride_EmptyList_ChecksNoMeta()
		{
			var html = "<html><head><meta name=\"description\" content=\"Eine Beschreibung\"></head><body></body></html>";
			var none = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
			var runs = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: false, metaContentNames: none).ToList();

			Assert.DoesNotContain(runs, r => r.Source == RunSource.Meta); // declared-empty = check nothing
		}

		[Fact]
		public void Traverse_GlobalXpathToIgnore_PrunesTechnicalElement_KeepsRealProse()
		{
			// A tracking pixel (alt is normally prose-tier, but this element is technical plumbing).
			var html = "<html><body><img id=\"pix\" alt=\"keep it\"><p>Echter Text</p></body></html>";
			var globalIgnore = new BoilerplateMatcher(new[] { new BoilerplateSelector("xpath", "//img[@id='pix']") });

			var runs = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: false,
				metaContentNames: null, globalIgnore: globalIgnore).ToList();

			Assert.DoesNotContain(runs, r => r.SourcePath == "img[@alt]"); // pixel pruned
			Assert.Contains(runs, r => r.RawText.Contains("Echter"));      // real prose kept
		}

		[Fact]
		public void Traverse_GlobalXpathToIgnore_AppliesOnEntryPages_UnlikeBoilerplate()
		{
			// Orthogonality: on an ENTRY page boilerplate is CHECKED (not pruned), but the global
			// technical-ignore must STILL prune the pixel. Uses the boilerplate-aware overload with
			// isEntryPage:true so the plain-walk path is taken — the global ignore must survive it.
			var html = "<html><body><img id=\"pix\" alt=\"keep it\"><nav class=\"boiler\">Menü</nav></body></html>";
			var boiler = new BoilerplateMatcher(new[] { new BoilerplateSelector("class", "boiler") });
			var globalIgnore = new BoilerplateMatcher(new[] { new BoilerplateSelector("xpath", "//img[@id='pix']") });

			var runs = DomTraverser.Traverse(Doc(html), boiler, isEntryPage: true,
				skipAttributeNames: null, checkJavaScript: false, metaContentNames: null, globalIgnore: globalIgnore).ToList();

			Assert.DoesNotContain(runs, r => r.SourcePath == "img[@alt]");          // pixel pruned even on entry page
			Assert.Contains(runs, r => r.RawText.Contains("Menü"));                 // boilerplate CHECKED on entry page
		}

		[Fact]
		public void DefaultBooleanAttributes_ContainNovalidateAndDisabled_CaseInsensitive()
		{
			Assert.Contains("novalidate", DomTraverser.DefaultBooleanAttributes);
			Assert.Contains("disabled", DomTraverser.DefaultBooleanAttributes);
			Assert.Contains("NOVALIDATE", DomTraverser.DefaultBooleanAttributes); // case-insensitive
		}

		[Fact]
		public void Traverse_BooleanAttributeValue_Skipped_WhenInSkipSet()
		{
			// Models the runner wiring: boolean set is unioned into skipAttributeNames. The redundant
			// novalidate="novalidate" value is not checked; a real prose attribute survives.
			var html = "<form novalidate=\"novalidate\" title=\"Echtes Formular\"><input disabled=\"disabled\"></form>";
			var runs = DomTraverser.Traverse(Doc(html), DomTraverser.DefaultBooleanAttributes).ToList();

			Assert.DoesNotContain(runs, r => r.SourcePath == "form[@novalidate]");
			Assert.DoesNotContain(runs, r => r.SourcePath == "input[@disabled]");
			Assert.Contains(runs, r => r.SourcePath == "form[@title]"); // real prose attribute kept
		}

		[Fact]
		public void Traverse_BooleanMatchIsExact_NotPrefix()
		{
			// "disabledtext" merely starts with "disabled" — NOT a boolean attribute, must be checked.
			var html = "<div disabledtext=\"Eine Beschreibung\">x</div>";
			var runs = DomTraverser.Traverse(Doc(html), DomTraverser.DefaultBooleanAttributes).ToList();

			Assert.Contains(runs, r => r.SourcePath == "div[@disabledtext]");
		}

		[Fact]
		public void Traverse_HtmlTagsToIgnore_StripsSubtree_TextAndNestedAttributes()
		{
			// <select> is ignored: its option text AND nested input attributes are dropped; prose
			// outside it survives. Models the market-data / form-control noise case.
			var tags = new HashSet<string>(new[] { "select" }, System.StringComparer.OrdinalIgnoreCase);
			var html = "<div><p>Echter Text</p>"
				+ "<select><option value=\"Xy7Qm\">adidas</option><input alt=\"Zumindest\"></select></div>";
			var runs = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: false,
				metaContentNames: null, globalIgnore: null, htmlTagsToIgnore: tags).ToList();

			Assert.DoesNotContain(runs, r => r.RawText.Contains("adidas"));     // option text stripped
			Assert.DoesNotContain(runs, r => r.RawText.Contains("Xy7Qm"));      // nested option value stripped
			Assert.DoesNotContain(runs, r => r.RawText.Contains("Zumindest"));  // nested input attr stripped
			Assert.Contains(runs, r => r.RawText.Contains("Echter"));           // prose outside kept
		}

		[Fact]
		public void Traverse_HtmlTagsToIgnore_StripsDeeplyNestedContent()
		{
			// Deep subtree: text inside <svg><g><text> must be stripped, not just direct children.
			var tags = new HashSet<string>(new[] { "svg" }, System.StringComparer.OrdinalIgnoreCase);
			var html = "<div><svg><g><text>Diagrammbeschriftung</text></g></svg><p>Fließtext</p></div>";
			var runs = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: false,
				metaContentNames: null, globalIgnore: null, htmlTagsToIgnore: tags).ToList();

			Assert.DoesNotContain(runs, r => r.RawText.Contains("Diagrammbeschriftung")); // deep svg text stripped
			Assert.Contains(runs, r => r.RawText.Contains("Fließtext"));                  // sibling prose kept
		}

		[Fact]
		public void Traverse_HtmlTagsToIgnore_NonListedTag_StillChecked()
		{
			// A tag NOT in the ignore set is checked normally — only declared tags are stripped.
			var tags = new HashSet<string>(new[] { "svg" }, System.StringComparer.OrdinalIgnoreCase);
			var html = "<section><p>Echter Inhalt</p></section>";
			var runs = DomTraverser.Traverse(Doc(html), skipAttributeNames: null, checkJavaScript: false,
				metaContentNames: null, globalIgnore: null, htmlTagsToIgnore: tags).ToList();

			Assert.Contains(runs, r => r.RawText.Contains("Echter")); // section not in set → checked
		}

		[Fact]
		public void Tokenize_RetainsSpan_BackIntoRunText()
		{
			var run = new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", "Guten Morgen");

			var tokens = SpellTokenizer.Tokenize(run).ToList();

			Assert.Equal(2, tokens.Count);
			Assert.Equal("Guten", tokens[0].Text);
			Assert.Equal(0, tokens[0].Start);
			Assert.Equal("Morgen", tokens[1].Text);
			Assert.Equal(6, tokens[1].Start);
			// The span must reproduce the token verbatim from the run text.
			Assert.Equal(run.RawText.Substring(tokens[1].Start, tokens[1].Length), tokens[1].Text);
		}

		[Fact]
		public void Tokenize_KeepsUmlautWordIntact()
		{
			var run = new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", "Aktivitäten");

			var tokens = SpellTokenizer.Tokenize(run).Where(t => t.Text.Length > 1).ToList();

			Assert.Single(tokens);
			Assert.Equal("Aktivitäten", tokens[0].Text);
		}

		// ── Inline phrasing assembly ──────────────────────────────────────────────────
		// A phrasing element (b/i/em/strong/span/…) creates no rendered word boundary; it
		// only fractures the DOM into sibling text nodes. The traverser glues those back
		// into the single word the reader sees, instead of emitting the fragments.

		[Fact]
		public void Traverse_GluesWordSplitByInlineBold_IntoSingleWord()
		{
			// "<b>G</b>iro<b>k</b>onto" renders as "Girokonto"; the old per-node walk emitted
			// "G","iro","k","onto". Assembly must yield the whole word as one run.
			var runs = DomTraverser.Traverse(Doc("<html><body><p>Ein <b>G</b>iro<b>k</b>onto heute</p></body></html>")).ToList();

			var textRun = Assert.Single(runs, r => r.Source == RunSource.TextNode);
			Assert.Contains("Girokonto", textRun.RawText);
			Assert.DoesNotContain(runs, r => r.Source == RunSource.TextNode && r.RawText == "iro");
			Assert.DoesNotContain(runs, r => r.Source == RunSource.TextNode && r.RawText == "onto");
		}

		[Fact]
		public void Traverse_AcronymInitialsBoldedPerLetter_ReassembleToWords()
		{
			// The defect shape: each acronym initial wrapped in its own <b> (no semantic <abbr>).
			var html = "<html><body><p>steht für <b>I</b>nternational <b>B</b>ank <b>A</b>ccount <b>N</b>umber</p></body></html>";
			var runs = DomTraverser.Traverse(Doc(html)).ToList();

			var text = string.Concat(runs.Where(r => r.Source == RunSource.TextNode).Select(r => r.RawText));
			Assert.Contains("International", text);
			Assert.Contains("Account", text);
			// None of the headless tails leak as standalone runs.
			foreach (var frag in new[] { "nternational", "ccount", "umber", "ank" })
			{
				Assert.DoesNotContain(runs, r => r.Source == RunSource.TextNode && r.RawText == frag);
			}
		}

		[Fact]
		public void Traverse_ReassembledRun_IsLabelledWithBlock_NotInlineWrapper()
		{
			// Provenance: the word lives in the paragraph, so the run is bound to the block
			// (p[#text]) — not the <b> it was fractured by. This also matches the block-level
			// path convention the known-defect config uses.
			var runs = DomTraverser.Traverse(Doc("<html><body><p><b>I</b>nternational</p></body></html>")).ToList();

			var textRun = Assert.Single(runs, r => r.Source == RunSource.TextNode);
			Assert.Equal("p[#text]", textRun.SourcePath);
			Assert.Contains("International", textRun.RawText);
		}

		[Fact]
		public void Traverse_PreservesSpaceBetweenInlineWrappedWords()
		{
			// "<b>foo</b> <b>bar</b>" has a real whitespace-only text node between the spans;
			// it must survive as a separator so the words do not glue into "foobar".
			var runs = DomTraverser.Traverse(Doc("<html><body><p><b>foo</b> <b>bar</b></p></body></html>")).ToList();

			var textRun = Assert.Single(runs, r => r.Source == RunSource.TextNode);
			Assert.Contains("foo", textRun.RawText);
			Assert.Contains("bar", textRun.RawText);
			Assert.DoesNotContain("foobar", textRun.RawText.Replace(" ", "X")); // a space sits between them
		}

		[Fact]
		public void Traverse_BlockBoundary_DoesNotGlueAcrossParagraphs()
		{
			var runs = DomTraverser.Traverse(Doc("<html><body><p>Giro</p><p>konto</p></body></html>"))
				.Where(r => r.Source == RunSource.TextNode).ToList();

			Assert.Equal(2, runs.Count);
			Assert.DoesNotContain(runs, r => r.RawText.Contains("Girokonto"));
		}

		[Fact]
		public void Traverse_BrBoundary_DoesNotGlueAcrossLineBreak()
		{
			// A <br> is a rendered break — "Giro<br>konto" must not become "Girokonto".
			var runs = DomTraverser.Traverse(Doc("<html><body><p>Giro<br>konto</p></body></html>"))
				.Where(r => r.Source == RunSource.TextNode).ToList();

			Assert.DoesNotContain(runs, r => r.RawText.Contains("Girokonto"));
		}

		[Fact]
		public void Traverse_AnchorIsNotGlued_FractureStaysSeparate()
		{
			// <a> is deliberately EXCLUDED from the glue set (it wraps whole words and would
			// drag in href noise). A word fractured by an anchor stays split — by design.
			var runs = DomTraverser.Traverse(Doc("<html><body><p>Giro<a href=\"#\">k</a>onto</p></body></html>"))
				.Where(r => r.Source == RunSource.TextNode).ToList();

			Assert.DoesNotContain(runs, r => r.RawText.Contains("Girokonto"));
		}

		[Fact]
		public void Traverse_NestedBlockInsideBlock_BreaksOnExit()
		{
			// "<div>A<p>B</p>C</div>": C follows B in document order with no element entered
			// between them — only the ancestor-path test catches the exit from <p>. B and C
			// must not glue into "BC".
			var runs = DomTraverser.Traverse(Doc("<html><body><div>A<p>B</p>C</div></body></html>"))
				.Where(r => r.Source == RunSource.TextNode).ToList();

			Assert.DoesNotContain(runs, r => r.RawText.Replace(" ", "") == "BC");
			Assert.Contains(runs, r => r.RawText.Trim() == "B" && r.SourcePath == "p[#text]");
		}

		[Fact]
		public void Traverse_GluingStopsAtPrunedSubtree_DoesNotConcatenateAcrossScript()
		{
			// A script body between two letters must not be glued into the surrounding word.
			var html = "<html><body><p>Giro<script>var x=1;</script>konto</p></body></html>";
			var runs = DomTraverser.Traverse(Doc(html)).ToList();

			Assert.DoesNotContain(runs, r => r.Source == RunSource.TextNode && r.RawText.Contains("var x"));
			Assert.DoesNotContain(runs, r => r.Source == RunSource.TextNode && r.RawText.Replace(" ", "").Contains("Girovar"));
		}

		// --- Delivery 605: the built-in DefaultNonProseAttributes set is the spec-derived
		// non-prose attribute index (URI/IDREF/enum/token/numeric/code), so the engine ignores
		// these with no configuration on any site. Two invariants matter: the spec attributes ARE
		// present, and the prose-bearing attributes are NOT (the safety guarantee).

		[Theory]
		[InlineData("class")]
		[InlineData("id")]
		[InlineData("style")]
		[InlineData("rel")]            // the universal flood: rel="noopener" on every external link
		[InlineData("type")]
		[InlineData("target")]
		[InlineData("media")]
		[InlineData("href")]
		[InlineData("role")]
		[InlineData("itemprop")]
		[InlineData("scope")]
		[InlineData("method")]
		[InlineData("name")]
		[InlineData("for")]
		[InlineData("src")]
		[InlineData("colspan")]
		[InlineData("tabindex")]
		[InlineData("datetime")]
		[InlineData("aria-describedby")]   // IDREF-list — the classifier reads its spaces as prose
		[InlineData("aria-labelledby")]
		[InlineData("aria-live")]
		[InlineData("aria-hidden")]
		public void DefaultNonProseAttributes_ContainsSpecNonProseAttribute(string attr)
		{
			Assert.Contains(attr, DomTraverser.DefaultNonProseAttributes);
		}

		[Theory]
		[InlineData("alt")]
		[InlineData("title")]
		[InlineData("placeholder")]
		[InlineData("label")]
		[InlineData("value")]                  // context-dependent (text inputs carry prose)
		[InlineData("content")]                // meta prose, governed by the meta-name allowlist
		[InlineData("download")]
		[InlineData("abbr")]
		[InlineData("aria-label")]
		[InlineData("aria-placeholder")]
		[InlineData("aria-valuetext")]
		[InlineData("aria-roledescription")]
		[InlineData("aria-description")]
		public void DefaultNonProseAttributes_ExcludesProseBearingAttribute(string attr)
		{
			// SAFETY: a prose-bearing attribute must stay CHECKABLE. Adding any of these to the
			// default skip set would silently swallow real spelling errors.
			Assert.DoesNotContain(attr, DomTraverser.DefaultNonProseAttributes);
		}

		[Fact]
		public void Traverse_WithDefaultNonProseSkip_DropsSpecAttributes_KeepsProseAndText()
		{
			// End-to-end: skipping by the default set drops rel/href/role token values but keeps
			// the alt prose and the element text.
			var html = "<a href=\"/de/konto\" rel=\"noopener noreferrer\" role=\"button\">Kontakt</a>"
				+ "<img alt=\"Ein Hund im Garten\">";
			var runs = DomTraverser.Traverse(Doc(html), DomTraverser.DefaultNonProseAttributes).ToList();

			Assert.DoesNotContain(runs, r => r.SourcePath == "a[@href]");
			Assert.DoesNotContain(runs, r => r.SourcePath == "a[@rel]");
			Assert.DoesNotContain(runs, r => r.SourcePath == "a[@role]");
			Assert.Contains(runs, r => r.SourcePath == "img[@alt]");          // prose attr kept
			Assert.Contains(runs, r => r.Source == RunSource.TextNode && r.RawText.Contains("Kontakt"));
		}
	}
}
