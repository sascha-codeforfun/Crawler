using System.Linq;
using Crawler;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 637: WriteStepRow wraps long details on " · " boundaries, hanging-indented to the value
	/// column, so rows no longer spill to column 0 in a narrow (default-size) console. These cover the
	/// pure WrapDetail helper; the indentation/printing itself is visual (validated on a real run).
	/// </summary>
	public class ConsoleUiWrapTests
	{
		[Fact]
		public void ShortDetail_StaysOnOneLine()
		{
			var lines = ConsoleUi.WrapDetail("16 configured", 49);
			Assert.Single(lines);
			Assert.Equal("16 configured", lines[0]);
		}

		[Fact]
		public void LongDetail_WrapsAtSeparators_WithContinuationCue()
		{
			// 4 three-char chunks at width 11 break predictably; the continued line ends with " ·".
			var lines = ConsoleUi.WrapDetail("aaa · bbb · ccc · ddd", 11);
			Assert.Equal(new[] { "aaa · bbb ·", "ccc · ddd" }, lines);
		}

		[Fact]
		public void EverySeparatedLine_FitsWidth_AndOnlyContinuedLinesCarryCue()
		{
			var detail = "aa · bb · cc · dd · ee · ff · gg · hh";
			var lines = ConsoleUi.WrapDetail(detail, 12);

			Assert.True(lines.Count >= 2);
			Assert.All(lines, l => Assert.True(l.Length <= 12));
			// all but the last line end with the continuation cue
			Assert.All(lines.Take(lines.Count - 1), l => Assert.EndsWith(" ·", l));
			Assert.False(lines[^1].EndsWith(" ·"));
		}

		[Fact]
		public void UnbreakableSegment_LongerThanWidth_IsLeftWhole()
		{
			// No " · " to break on (a path) — returned intact rather than hard-split mid-token.
			var path = "D:\\very\\long\\path\\to\\content-list.csv";
			var lines = ConsoleUi.WrapDetail(path, 10);
			Assert.Single(lines);
			Assert.Equal(path, lines[0]);
		}
	}
}
