using Crawler;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// 658 — pins the structured-signal translation behind the calm JSON-parse halt
	/// (<see cref="ConfigJsonErrorScreen"/>): a System.Text.Json path becomes a readable setting name, an
	/// array index becomes a 1-based human ordinal, and the zero-based LineNumber becomes a 1-based editor
	/// line. The screen prose itself is constant and not asserted here — only the logic with edge cases.
	/// </summary>
	public class ConfigJsonErrorScreenTests
	{
		private const string Arrow = "\u2192";

		[Fact]
		public void FriendlyPath_NestedWithArrayIndex_ReadsAsSettingAndOrdinal()
		{
			Assert.Equal(
				$"SpellCheckEngine {Arrow} SpellCheckJavaScript {Arrow} TokensToFilter, entry #31",
				ConfigJsonErrorScreen.FriendlyPath("$.SpellCheckEngine.SpellCheckJavaScript.TokensToFilter[30]"));
		}

		[Fact]
		public void FriendlyPath_IndexInTheMiddle_AttachesToItsSegment()
		{
			Assert.Equal($"Sites, entry #3 {Arrow} Url", ConfigJsonErrorScreen.FriendlyPath("$.Sites[2].Url"));
		}

		[Fact]
		public void FriendlyPath_SimpleProperty_IsJustTheName()
		{
			Assert.Equal("BaseDirectory", ConfigJsonErrorScreen.FriendlyPath("$.BaseDirectory"));
		}

		[Fact]
		public void FriendlyPath_FirstElement_IsEntryOne()
		{
			Assert.Equal("A, entry #1", ConfigJsonErrorScreen.FriendlyPath("$.A[0]"));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void FriendlyPath_NoPath_IsNeutralPlaceholder(string? path)
		{
			Assert.Equal("(location not reported)", ConfigJsonErrorScreen.FriendlyPath(path));
		}

		[Fact]
		public void FriendlyPath_RootOnly_IsTopLevel()
		{
			Assert.Equal("(top level)", ConfigJsonErrorScreen.FriendlyPath("$"));
		}

		[Theory]
		[InlineData(184, "185")] // zero-based 184 -> editor line 185
		[InlineData(0, "1")]
		public void HumanLine_ConvertsZeroBasedToEditorLine(long zeroBased, string expected)
		{
			Assert.Equal(expected, ConfigJsonErrorScreen.HumanLine(zeroBased));
		}

		[Fact]
		public void HumanLine_Null_IsPlaceholder()
		{
			Assert.Equal("(not reported)", ConfigJsonErrorScreen.HumanLine(null));
		}
	}
}
