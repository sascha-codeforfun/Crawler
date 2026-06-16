using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for CharacterValidator.Scan() and ValidateListHalt().
	/// Scan() is pure logic with no dependencies.
	/// ValidateListHalt() requires Logger to be initialised — each test that
	/// exercises it writes to a dedicated temp file that is cleaned up on dispose.
	/// </summary>
	[Collection("Logger")]
	public class CharacterValidatorTests : IDisposable
	{
		private readonly string _tempLogFile;

		public CharacterValidatorTests()
		{
			_tempLogFile = Path.Combine(Path.GetTempPath(), $"crawler-test-{Guid.NewGuid()}.log");
			Logger.Initialize(_tempLogFile, silent: true);
		}

		public void Dispose()
		{
			if (File.Exists(_tempLogFile))
			{
				File.Delete(_tempLogFile);
			}
		}

		// ── Scan — clean inputs ───────────────────────────────────────────────

		[Fact]
		public void Scan_EmptyString_ReturnsNoHits()
		{
			var hits = CharacterValidator.Scan("source", "").ToList();
			Assert.Empty(hits);
		}

		[Fact]
		public void Scan_NullString_ReturnsNoHits()
		{
			var hits = CharacterValidator.Scan("source", null!).ToList();
			Assert.Empty(hits);
		}

		[Fact]
		public void Scan_PlainAscii_ReturnsNoHits()
		{
			var hits = CharacterValidator.Scan("source", "Hello-World_123").ToList();
			Assert.Empty(hits);
		}

		[Fact]
		public void Scan_AllowedGermanChars_ReturnsNoHits()
		{
			var hits = CharacterValidator.Scan("source", "äöüßÄÖÜ").ToList();
			Assert.Empty(hits);
		}

		[Fact]
		public void Scan_AllowedWesternEuropeanChars_ReturnsNoHits()
		{
			var hits = CharacterValidator.Scan("source", "àáâçèéêëñùúû").ToList();
			Assert.Empty(hits);
		}

		// ── Scan — known bad characters ───────────────────────────────────────

		[Fact]
		public void Scan_SoftHyphen_ReturnsOneHit()
		{
			var hits = CharacterValidator.Scan("source", "Hallo\u00ADWelt").ToList();
			Assert.Single(hits);
			Assert.Equal('\u00AD', hits[0].Character);
			Assert.Equal("SOFT HYPHEN", hits[0].CharName);
		}

		[Fact]
		public void Scan_EnDash_ReturnsHitWithDashSuggestion()
		{
			var hits = CharacterValidator.Scan("source", "one\u2013two").ToList();
			Assert.Single(hits);
			Assert.Equal('\u2013', hits[0].Character);
			Assert.Equal("EN DASH", hits[0].CharName);
			Assert.Contains("hyphen-minus", hits[0].Suggestion, StringComparison.OrdinalIgnoreCase);
		}

		[Fact]
		public void Scan_EmDash_ReturnsHitWithDashSuggestion()
		{
			var hits = CharacterValidator.Scan("source", "one\u2014two").ToList();
			Assert.Single(hits);
			Assert.Equal('\u2014', hits[0].Character);
			Assert.Equal("EM DASH", hits[0].CharName);
			Assert.Contains("hyphen-minus", hits[0].Suggestion, StringComparison.OrdinalIgnoreCase);
		}

		[Fact]
		public void Scan_ZeroWidthSpace_ReturnsHitWithRemoveSuggestion()
		{
			var hits = CharacterValidator.Scan("source", "word\u200Bword").ToList();
			Assert.Single(hits);
			Assert.Equal('\u200B', hits[0].Character);
			Assert.Equal("ZERO WIDTH SPACE", hits[0].CharName);
			Assert.Contains("Remove", hits[0].Suggestion, StringComparison.OrdinalIgnoreCase);
		}

		[Fact]
		public void Scan_NoBreakSpace_ReturnsHit()
		{
			var hits = CharacterValidator.Scan("source", "hello\u00A0world").ToList();
			Assert.Single(hits);
			Assert.Equal('\u00A0', hits[0].Character);
			Assert.Equal("NO-BREAK SPACE", hits[0].CharName);
		}

		[Fact]
		public void Scan_BOM_ReturnsHit()
		{
			var hits = CharacterValidator.Scan("source", "\uFEFFword").ToList();
			Assert.Single(hits);
			Assert.Equal('\uFEFF', hits[0].Character);
		}

		[Fact]
		public void Scan_LeftDoubleQuotationMark_ReturnsHit()
		{
			var hits = CharacterValidator.Scan("source", "\u201Chello\u201D").ToList();
			Assert.Equal(2, hits.Count);
			Assert.Equal("LEFT DOUBLE QUOTATION MARK", hits[0].CharName);
			Assert.Equal("RIGHT DOUBLE QUOTATION MARK", hits[1].CharName);
		}

		[Fact]
		public void Scan_UnknownNonAscii_ReturnsHitWithUPlusName()
		{
			// U+0007 BEL — not in KnownBadChars, not printable ASCII, not in AllowedNonAscii
			var hits = CharacterValidator.Scan("source", "a\u0007b").ToList();
			Assert.Single(hits);
			Assert.Equal("U+0007", hits[0].CharName);
		}

		// ── Scan — position reporting ─────────────────────────────────────────

		[Fact]
		public void Scan_ReportsCorrectPosition()
		{
			var hits = CharacterValidator.Scan("source", "abc\u200Bdef").ToList();
			Assert.Single(hits);
			Assert.Equal(3, hits[0].Position);
		}

		[Fact]
		public void Scan_MultipleHits_ReportsAllPositions()
		{
			var hits = CharacterValidator.Scan("source", "\u00ADa\u200B").ToList();
			Assert.Equal(2, hits.Count);
			Assert.Equal(0, hits[0].Position);
			Assert.Equal(2, hits[1].Position);
		}

		// ── Scan — source label passthrough ──────────────────────────────────

		[Fact]
		public void Scan_SourceLabelPassedThrough()
		{
			var hits = CharacterValidator.Scan("my-config-key", "a\u00ADb").ToList();
			Assert.Single(hits);
			Assert.Equal("my-config-key", hits[0].Source);
		}

		// ── ValidateListHalt — clean list ─────────────────────────────────────

		[Fact]
		public void ValidateListHalt_CleanList_DoesNotThrow()
		{
			var ex = Record.Exception(() =>
				CharacterValidator.ValidateListHalt("TestKey", ["hello", "world", "Straße"], silent: true));
			Assert.Null(ex);
		}

		[Fact]
		public void ValidateListHalt_EmptyList_DoesNotThrow()
		{
			var ex = Record.Exception(() =>
				CharacterValidator.ValidateListHalt("TestKey", [], silent: true));
			Assert.Null(ex);
		}

		// ── ValidateListHalt — contaminated list ──────────────────────────────

		[Fact]
		public void ValidateListHalt_ContaminatedEntry_Throws()
		{
			var ex = Record.Exception(() =>
				CharacterValidator.ValidateListHalt("TestKey", ["clean", "conta\u200Bminated"], silent: true));
			Assert.NotNull(ex);
			Assert.IsType<InvalidOperationException>(ex);
		}

		[Fact]
		public void ValidateListHalt_ContaminatedEntry_ExceptionMentionsKey()
		{
			var ex = Record.Exception(() =>
				CharacterValidator.ValidateListHalt("SpellCheckWordPrefixesToStrip", ["bad\u00AD"], silent: true));
			Assert.NotNull(ex);
			Assert.Contains("SpellCheckWordPrefixesToStrip", ex!.Message);
		}

		[Fact]
		public void ValidateListHalt_MultipleContaminatedEntries_ThrowsMentionsCount()
		{
			var ex = Record.Exception(() =>
				CharacterValidator.ValidateListHalt("TestKey", ["a\u00AD", "b\u200B"], silent: true));
			Assert.NotNull(ex);
			// Two hits — message should mention count > 1
			Assert.Contains("2", ex!.Message);
		}

		// ── ValidateListHalt — dash lookalike vs remove suggestion ────────────

		[Fact]
		public void ValidateListHalt_DashLookalike_SuggestionMentionsHyphen()
		{
			// Capture via Scan since ValidateListHalt throws before we can inspect hits.
			var hits = CharacterValidator.Scan("k", "prefix\uFF0D").ToList();
			Assert.Single(hits);
			Assert.Contains("hyphen-minus", hits[0].Suggestion, StringComparison.OrdinalIgnoreCase);
		}

		[Fact]
		public void ValidateListHalt_NonDashBadChar_SuggestionSaysRemove()
		{
			var hits = CharacterValidator.Scan("k", "word\u200C").ToList();
			Assert.Single(hits);
			Assert.Contains("Remove", hits[0].Suggestion, StringComparison.OrdinalIgnoreCase);
		}

		// ── ScanInvisibleOnly — script-agnostic, invisible-only policy ────────
		// Unlike Scan(), this permits ALL visible scripts and flags only what a
		// human cannot see. Critical for free-text config like SEO TitleTemplate
		// that may carry a non-Latin brand name.

		[Fact]
		public void ScanInvisibleOnly_PlainAscii_NoHits()
		{
			Assert.Empty(CharacterValidator.ScanInvisibleOnly("s", "{title} | Brand").ToList());
		}

		[Fact]
		public void ScanInvisibleOnly_Cyrillic_NoHits()
		{
			// "Правда" must be allowed — it is a legitimate visible brand name.
			Assert.Empty(CharacterValidator.ScanInvisibleOnly("s", "{title} | Правда").ToList());
		}

		[Fact]
		public void ScanInvisibleOnly_Cjk_NoHits()
		{
			Assert.Empty(CharacterValidator.ScanInvisibleOnly("s", "{title}｜北京公司").ToList());
		}

		[Fact]
		public void ScanInvisibleOnly_VisibleDashAndSmartQuotes_NoHits()
		{
			// Visible punctuation is allowed here (it is NOT invisible), unlike Scan().
			Assert.Empty(CharacterValidator.ScanInvisibleOnly("s", "{title} – “Brand”").ToList());
		}

		[Theory]
		[InlineData("\u200B")]   // zero-width space
		[InlineData("\u200C")]   // zero-width non-joiner
		[InlineData("\u200D")]   // zero-width joiner
		[InlineData("\uFEFF")]   // BOM / zero-width no-break space
		[InlineData("\u00A0")]   // no-break space
		[InlineData("\u202F")]   // narrow no-break space
		[InlineData("\u2028")]   // line separator
		[InlineData("\u2029")]   // paragraph separator
		[InlineData("\u202E")]   // right-to-left override (bidi control)
		public void ScanInvisibleOnly_InvisibleChar_IsFlagged(string invisible)
		{
			var hits = CharacterValidator.ScanInvisibleOnly("s", $"{{title}} | Brand{invisible}").ToList();
			Assert.Single(hits);
		}

		[Fact]
		public void ScanInvisibleOnly_OrdinarySpace_NotFlagged()
		{
			Assert.Empty(CharacterValidator.ScanInvisibleOnly("s", "a b c").ToList());
		}

		[Fact]
		public void ScanInvisibleOnly_Empty_NoHits()
		{
			Assert.Empty(CharacterValidator.ScanInvisibleOnly("s", "").ToList());
		}
	}
}
