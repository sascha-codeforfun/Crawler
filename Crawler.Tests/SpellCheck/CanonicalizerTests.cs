using System.Linq;
using Crawler.SpellCheck;
using HtmlAgilityPack;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery: canonicalize-once. Verifies the single normalization rule whose output
	/// feeds both tokenizer and excerpt, so they cannot diverge. Decode is SINGLE by
	/// deliberate choice — correct, and parity-neutral on the target corpus: it resolves
	/// real entities but does not over-decode a double-encoded literal.
	/// </summary>
	public class SpellCheckCanonicalizerTests
	{
		[Fact]
		public void Decode_Single_ResolvesNamedEntity()
		{
			Assert.Equal("München", Canonicalizer.Canonicalize("M&uuml;nchen"));
		}

		[Fact]
		public void Decode_Single_ResolvesAmpersand()
		{
			Assert.Equal("Tom & Jerry", Canonicalizer.Canonicalize("Tom &amp; Jerry"));
		}

		[Fact]
		public void Decode_Single_DoesNotOverDecode_DoubleEncodedEntity()
		{
			// &amp;uuml; is the literal text "&uuml;", not "ü". Single decode keeps it literal;
			// double decode would silently corrupt it to "ü".
			Assert.Equal("&uuml;", Canonicalizer.Canonicalize("&amp;uuml;"));
		}

		[Fact]
		public void SoftHyphen_IsDropped()
		{
			Assert.Equal("Vertrag", Canonicalizer.Canonicalize("Ver\u00ADtrag"));
		}

		[Fact]
		public void LiteralShyEntity_FromDoubleEncoding_IsDropped_JoiningWord()
		{
			// Source was double-encoded "Grund&amp;shy;gebühr"; single decode leaves the literal
			// "&shy;", which must be treated as a soft hyphen and dropped so the compound joins.
			Assert.Equal("Grundgebühr", Canonicalizer.Canonicalize("Grund&amp;shy;gebühr"));
		}

		[Fact]
		public void LiteralShyEntity_AlreadyLiteral_IsDropped()
		{
			Assert.Equal("Produktbeschreibung", Canonicalizer.Canonicalize("Produkt&shy;beschreibung"));
		}

		[Fact]
		public void OtherLiteralEntity_NotConverted_StaysLiteral()
		{
			// Scope guard: only &shy; is mapped. A double-encoded letter entity is NOT over-decoded
			// (it may be a real content defect to surface), so it stays literal.
			Assert.Equal("&uuml;", Canonicalizer.Canonicalize("&amp;uuml;"));
		}

		[Fact]
		public void EncodedInlineTags_AreStripped_AsBoundaries()
		{
			// data-pagenav-title="…Qualifizierte&lt;br&gt;Energieberatung&lt;sup&gt;2&lt;/sup&gt;"
			// decodes to literal tags; they must drop, becoming word boundaries (never "Energieberatung2").
			Assert.Equal(
				"Qualifizierte Energieberatung 2",
				Canonicalizer.Canonicalize("Qualifizierte&lt;br&gt;Energieberatung&lt;sup&gt;2&lt;/sup&gt;"));
		}

		[Fact]
		public void FormatVocabularyTags_AreStripped_ButQuotedProseSurvives()
		{
			// Documentation referencing ISO 20022 element names inline: the <TwnNm>/<Ctry> tokens are
			// format syntax (out of scope, removed); the German+English prose around them survives.
			Assert.Equal(
				"Stadt „Town Name“ ( ) und Land „Country“ ( )",
				Canonicalizer.Canonicalize("Stadt „Town Name“ (<TwnNm>) und Land „Country“ (<Ctry>)"));
		}

		[Fact]
		public void AltTitleProse_IsPreserved_WhenTagStripped()
		{
			Assert.Equal(
				"International Bank Account Number IBAN",
				Canonicalizer.Canonicalize("<abbr title=\"International Bank Account Number\">IBAN</abbr>"));
		}

		[Fact]
		public void NonProseAttributes_AreDropped_OnlyAltTitleKept()
		{
			// src/class are not prose: dropped with the tag; alt is kept.
			Assert.Equal(
				"Foto der Veranstaltung",
				Canonicalizer.Canonicalize("<img src=\"/x/y.jpg\" class=\"hero\" alt=\"Foto der Veranstaltung\">"));
		}

		[Fact]
		public void BareLessThan_InProse_IsNotTouched()
		{
			// '<' not followed by a letter is not a tag — math/comparisons survive intact.
			Assert.Equal("a < b und 3<5", Canonicalizer.Canonicalize("a < b und 3<5"));
		}

		[Fact]
		public void EnDash_BecomesHyphen()
		{
			Assert.Equal("2020-2021", Canonicalizer.Canonicalize("2020\u20132021"));
		}

		[Fact]
		public void CurlyQuotes_BecomeApostrophe()
		{
			Assert.Equal("it's a 'test'", Canonicalizer.Canonicalize("it\u2019s a \u2018test\u2019"));
		}

		[Fact]
		public void Whitespace_CollapsedAndTrimmed()
		{
			Assert.Equal("a b c", Canonicalizer.Canonicalize("  a\t\n  b   c  "));
		}

		[Fact]
		public void Empty_ReturnsEmpty()
		{
			Assert.Equal(string.Empty, Canonicalizer.Canonicalize(""));
		}

		[Fact]
		public void OutputFeedsTokenizer_TokenIsSubstringOfCanonicalText()
		{
			// The divergence-killer: tokenizing the canonical output yields tokens that are
			// literal substrings of that same canonical text — tokenizer and excerpt agree
			// because both read the one canonical form. The soft hyphen inside the word is
			// gone, so the token is the whole word and is findable in the excerpt.
			var canonical = Canonicalizer.Canonicalize("Akti\u00ADvitäten");
			Assert.Equal("Aktivitäten", canonical);

			var run = new TextRun(HtmlNode.CreateNode("<p>x</p>"), RunSource.TextNode, "p[#text]", canonical);
			var token = SpellTokenizer.Tokenize(run).Single(t => t.Text.Length > 1);

			Assert.Equal("Aktivitäten", token.Text);
			Assert.Contains(token.Text, canonical);
		}
	}
}
