using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Crawler;            // CharacterValidator
using Crawler.Lexicon;   // Loader, Bundle, UsageTracker
using Xunit;

namespace Crawler.Tests.Lexicon
{
	// SYNTHETIC fixtures with explicit code points. Covers the foreign-language
	// dictionary: relaxed letter policy, // comment + ! pin parsing, the strict-policy
	// guard the triage [U]/[S] path uses, file validation, and consult-only acceptance
	// that never touches the orphan/prune tracker.
	public class ForeignDictionaryTests : IDisposable
	{
		private readonly List<string> _temp = new();

		private string TempFile(string content)
		{
			var path = Path.GetTempFileName();
			File.WriteAllText(path, content, new UTF8Encoding(false));
			_temp.Add(path);
			return path;
		}

		public void Dispose()
		{
			foreach (var p in _temp)
			{
				try { File.Delete(p); } catch { /* best effort */ }
			}
		}

		// ── ScanForeign: relaxed (any-script letter) policy ─────────────────

		[Theory]
		[InlineData("\u010Dovek")]                          // čovek
		[InlineData("\u00F6\u011Fe")]                       // öğe (ö + ğ)
		[InlineData("izg\u016B\u0161ana")]                  // izgūšana (ū + š)
		[InlineData("\u043F\u0440\u0438\u0432\u0435\u0442")] // привет (Cyrillic)
		public void ScanForeign_AnyScriptLetters_Clean(string word)
		{
			Assert.Empty(CharacterValidator.ScanForeign("t", word));
		}

		[Theory]
		[InlineData("a\u200Bb")]   // ZERO WIDTH SPACE
		[InlineData("a\u2013b")]   // EN DASH
		[InlineData("a\u2019b")]   // curly apostrophe
		[InlineData("a\u00A0b")]   // NO-BREAK SPACE
		public void ScanForeign_AwkwardChars_StillFlagged(string word)
		{
			Assert.NotEmpty(CharacterValidator.ScanForeign("t", word));
		}

		// ── ForeignDictionaryWord: // comment + ! pin parse ─────────────────

		[Theory]
		[InlineData("// just a comment", "")]
		[InlineData("   // indented comment", "")]
		[InlineData("izg\u016B\u0161ana // Latvian extraction", "izg\u016B\u0161ana")]
		[InlineData("!\u010Dovek", "\u010Dovek")]
		[InlineData("!\u010Dovek // pinned and commented", "\u010Dovek")]
		[InlineData("word/flag", "word/flag")] // no comment — kept; the /-skip handles it
		[InlineData("", "")]
		public void ForeignDictionaryWord_Parses(string raw, string expected)
		{
			Assert.Equal(expected, CharacterValidator.ForeignDictionaryWord(raw));
		}

		// ── FirstStrictViolation: the triage [U]/[S] guard decision ─────────

		[Fact]
		public void FirstStrictViolation_ForeignLetter_ReturnsFirstOffender()
		{
			var v = CharacterValidator.FirstStrictViolation("izg\u016B\u0161ana");
			Assert.NotNull(v);
			Assert.Equal('\u016B', v!.Character); // ū is the first char the strict policy rejects
		}

		[Theory]
		[InlineData("hello")]
		[InlineData("\u00C4pfel")] // Äpfel — Ä is in the strict Western allow-list
		public void FirstStrictViolation_StrictlyClean_Null(string word)
		{
			Assert.Null(CharacterValidator.FirstStrictViolation(word));
		}

		// ── ValidateForeignDictionaryFileHalt ───────────────────────────────

		[Fact]
		public void ValidateForeign_LettersAndComments_DoesNotThrow()
		{
			// The comments carry an em-dash and curly quotes — must NOT be scanned,
			// only the bare words (which are letters) are.
			var path = TempFile(
				"\u010Dovek\n" +
				"// section \u2014 \u201Cforeign words\u201D\n" +
				"izg\u016B\u0161ana // Latvian \u2014 verified\n");
			CharacterValidator.ValidateForeignDictionaryFileHalt(path, silent: true);
		}

		[Fact]
		public void ValidateForeign_InvisibleInWord_Throws()
		{
			var path = TempFile("da\u200Bta\n"); // ZWSP inside the word itself
			Assert.Throws<InvalidOperationException>(
				() => CharacterValidator.ValidateForeignDictionaryFileHalt(path, silent: true));
		}

		// ── ReadForeignDictionaryWords: file → bare words ───────────────────

		[Fact]
		public void ReadForeignWords_StripsCommentsPins_SkipsAffixAndBlanks()
		{
			var path = TempFile(
				"\u010Dovek\n" +
				"// pure comment line\n" +
				"\n" +
				"izg\u016B\u0161ana // trailing comment\n" +
				"!\u00F6\u011Fe\n" +
				"stem/AFFIX\n"); // affix-flag line → skipped
			var words = Loader.ReadForeignDictionaryWords(path);
			Assert.Equal(new[] { "\u010Dovek", "izg\u016B\u0161ana", "\u00F6\u011Fe" }, words);
		}

		// ── Bundle.Check: SharedForeign accepted, NEVER tracked ─────────────

		[Fact]
		public void Check_SharedForeign_AcceptedButNotTracked()
		{
			UsageTracker.Reset();
			var word = "izg\u016B\u0161ana_" + Guid.NewGuid().ToString("N");
			var b = new Bundle();
			b.SharedForeign.Add(word);

			Assert.True(b.Check(word));                                // accepted
			Assert.DoesNotContain(word, UsageTracker.SnapshotUser());  // not recorded as user
			Assert.DoesNotContain(word, UsageTracker.SnapshotSite());  // not recorded as site
		}
	}
}
