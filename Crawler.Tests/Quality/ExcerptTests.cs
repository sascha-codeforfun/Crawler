using Crawler.Quality;
using Xunit;

namespace Crawler.Tests.Quality
{
	public class ExcerptTests
	{
		// ── Centred (position overload) — direct unit tests ──────────────

		[Fact]
		public void Centred_Position_CentresWindowOnOffset()
		{
			var source = new string('L', 500) + "MARK" + new string('R', 500);
			var centre = 500 + 2; // middle of MARK
			var ex = Excerpt.Centred(source, centre, 100);
			Assert.Contains("MARK", ex, StringComparison.Ordinal);
			Assert.StartsWith("\u2026", ex);   // clipped left
			Assert.EndsWith("\u2026", ex);      // clipped right
		}

		[Fact]
		public void Centred_Position_NoEllipsisWhenWholeSourceFits()
		{
			var source = "short source string";
			var ex = Excerpt.Centred(source, 5, 400);
			Assert.Equal(source, ex);           // unclipped → returned verbatim, no …
		}

		[Fact]
		public void Centred_Position_ClampsOutOfRangeCentre()
		{
			var source = new string('a', 100);
			// Centre past the end must not throw and must still return a bounded window.
			var ex = Excerpt.Centred(source, 9999, 40);
			Assert.True(ex.Length <= 41); // 40 body + at most one leading …
			Assert.EndsWith("a", ex);     // window sits at the tail, no trailing …
		}

		[Fact]
		public void Centred_Position_NewlinesReplacedWithSpaces()
		{
			var source = "line1\nline2\r\nline3";
			var ex = Excerpt.Centred(source, 8, 400);
			Assert.DoesNotContain('\n', ex);
			Assert.DoesNotContain('\r', ex);
		}
	}
}
