using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for pure text-manipulation helpers in Tools:
	/// NormalizeRemoveIso6391Suffix, StripWordPrefix.
	///
	/// All three are pure functions. StripWordPrefix is internal — exposure
	/// is justified by the accompanying tests in this file per the
	/// "internal-exposure-needs-tests" rule.
	/// </summary>
	[Collection("Logger")]
	public class ToolsTextTests
	{
		// ── NormalizeRemoveIso6391Suffix ────────────────────────────────────
		//
		// NOTE: The function's name and inline comment promise stripping
		// of " (xx)" or "(xx)" two-letter ISO 639-1 suffixes, but the strip
		// branch is UNREACHABLE due to a bug in the position math. The
		// length check `s.Length - open - 1 == 2` requires exactly 2 chars
		// after the opening paren; one of those must be the closing ')'
		// (because s.EndsWith(')')); so for both char.IsLetter checks to
		// pass, the ')' would need to be a letter. It isn't. The function
		// effectively reduces to "trim leading/trailing whitespace; return
		// empty for null/empty/whitespace-only input; otherwise pass-through."
		//
		// These tests lock down the observed behaviour. The bug is tracked
		// in notes-for-later — fixing it will require updating these tests
		// AND re-running the pivot since downstream consumers may depend on
		// the current (broken) behaviour.

		[Fact]
		public void NormalizeRemoveIso6391Suffix_TwoLetterParenSuffix_NotStripped()
		{
			// The documented intent — but the implementation never reaches
			// the strip path. Locked down as-is.
			Assert.Equal("Word (en)", Tools.NormalizeRemoveIso6391Suffix("Word (en)"));
		}

		[Fact]
		public void NormalizeRemoveIso6391Suffix_TwoLetterParenSuffix_NoSpace_NotStripped()
		{
			Assert.Equal("Word(de)", Tools.NormalizeRemoveIso6391Suffix("Word(de)"));
		}

		[Fact]
		public void NormalizeRemoveIso6391Suffix_SingleLetterParenSuffix_NotStripped()
		{
			// Even single-letter parens are not stripped because the second
			// position-checked character is the ')'.
			Assert.Equal("Word (e)", Tools.NormalizeRemoveIso6391Suffix("Word (e)"));
		}

		[Fact]
		public void NormalizeRemoveIso6391Suffix_ThreeLetterParenSuffix_NotStripped()
		{
			Assert.Equal("Word (eng)", Tools.NormalizeRemoveIso6391Suffix("Word (eng)"));
		}

		[Fact]
		public void NormalizeRemoveIso6391Suffix_DigitsInParens_NotStripped()
		{
			Assert.Equal("Word (12)", Tools.NormalizeRemoveIso6391Suffix("Word (12)"));
		}

		[Fact]
		public void NormalizeRemoveIso6391Suffix_TrimsLeadingTrailingWhitespace()
		{
			// Trim() runs unconditionally at the top of the function — this
			// is the one behaviour the function still performs reliably.
			Assert.Equal("Word (en)", Tools.NormalizeRemoveIso6391Suffix("  Word (en)  "));
		}

		[Fact]
		public void NormalizeRemoveIso6391Suffix_NoParenthesesPassesThrough()
		{
			Assert.Equal("PlainWord", Tools.NormalizeRemoveIso6391Suffix("PlainWord"));
		}

		[Fact]
		public void NormalizeRemoveIso6391Suffix_NullReturnsEmpty()
		{
			Assert.Equal(string.Empty, Tools.NormalizeRemoveIso6391Suffix(null!));
		}

		[Fact]
		public void NormalizeRemoveIso6391Suffix_EmptyReturnsEmpty()
		{
			Assert.Equal(string.Empty, Tools.NormalizeRemoveIso6391Suffix(""));
		}

		[Fact]
		public void NormalizeRemoveIso6391Suffix_WhitespaceOnlyReturnsEmpty()
		{
			Assert.Equal(string.Empty, Tools.NormalizeRemoveIso6391Suffix("   "));
		}

		// ── StripWordPrefix ─────────────────────────────────────────────────

		[Fact]
		public void StripWordPrefix_NoHyphenReturnsWordUnchanged()
		{
			// The fast-path: if the word has no '-', no prefix can match.
			var result = Tools.StripWordPrefix("Suite", new[] { "BRAND" });

			Assert.Equal("Suite", result);
		}

		[Fact]
		public void StripWordPrefix_EmptyPrefixesReturnsWordUnchanged()
		{
			var result = Tools.StripWordPrefix("BRAND-Suite", Array.Empty<string>());

			Assert.Equal("BRAND-Suite", result);
		}

		[Fact]
		public void StripWordPrefix_StripsMatchingPrefix()
		{
			var result = Tools.StripWordPrefix("BRAND-Suite", new[] { "BRAND" });

			Assert.Equal("Suite", result);
		}

		[Fact]
		public void StripWordPrefix_PrefixMatchIsCaseInsensitive()
		{
			var result = Tools.StripWordPrefix("brand-Suite", new[] { "BRAND" });

			Assert.Equal("Suite", result);
		}

		[Fact]
		public void StripWordPrefix_NoHyphenIgnoresPrefixListEvenOnExactMatch()
		{
			// Early return: if the word has no '-', the function returns the
			// word unchanged regardless of what's in the prefix list. The
			// "exact match returns null" branch is only reachable for inputs
			// containing a hyphen (and matching a multi-hyphen prefix exactly).
			var result = Tools.StripWordPrefix("BRAND", new[] { "BRAND" });

			Assert.Equal("BRAND", result);
		}

		[Fact]
		public void StripWordPrefix_ExactMatchOfHyphenatedPrefixReturnsNull()
		{
			// "BRAND-Suite" exactly equals a prefix → null (nothing left to spell-check).
			var result = Tools.StripWordPrefix("BRAND-Suite", new[] { "BRAND-Suite" });

			Assert.Null(result);
		}

		[Fact]
		public void StripWordPrefix_PrefixWithTrailingHyphenAndNothingAfter_ReturnsNull()
		{
			// "BRAND-" matches "BRAND" + "-" with empty remainder → null.
			var result = Tools.StripWordPrefix("BRAND-", new[] { "BRAND" });

			Assert.Null(result);
		}

		[Fact]
		public void StripWordPrefix_PrefersLongerPrefixFirst()
		{
			// Sort-by-length-desc means "BRAND-Suite" should be tried before
			// "BRAND" alone. Both match the input; the longer one wins.
			var result = Tools.StripWordPrefix(
				"BRAND-Suite-Component",
				new[] { "BRAND", "BRAND-Suite" });

			Assert.Equal("Component", result);
		}

		[Fact]
		public void StripWordPrefix_RecursivelyStripsChainedPrefixes()
		{
			// After stripping "BRAND", the remainder "Suite-Component" is
			// re-checked against the prefix list. "Suite" matches → "Component".
			var result = Tools.StripWordPrefix(
				"BRAND-Suite-Component",
				new[] { "BRAND", "Suite" });

			Assert.Equal("Component", result);
		}

		[Fact]
		public void StripWordPrefix_NonMatchingHyphenatedWordReturnsUnchanged()
		{
			var result = Tools.StripWordPrefix("foo-bar", new[] { "BRAND" });

			Assert.Equal("foo-bar", result);
		}

		[Fact]
		public void StripWordPrefix_SkipsEmptyPrefixesInList()
		{
			// Empty/whitespace prefixes are filtered out before sorting — they
			// would otherwise match every input and produce nonsense.
			var result = Tools.StripWordPrefix(
				"BRAND-Suite",
				new[] { "", "  ", "BRAND" });

			Assert.Equal("Suite", result);
		}
	}
}
