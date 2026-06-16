using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Locks the behavior of <see cref="Tools.GenerateFileName"/>, the function
	/// that derives the on-disk filename for every downloaded asset. Regression
	/// here changes filenames on disk — breaks cross-archive correlation, breaks
	/// sidecar pairing (the <c>.header</c> companion file derives its name from
	/// the body filename via <c>Path.ChangeExtension</c>), breaks any external
	/// tooling that grew used to the current pattern. The on-disk schema is a
	/// load-bearing contract; lock it.
	///
	/// No HTTP, no filesystem — pure-function tests.
	/// </summary>
	public class ToolsGenerateFileNameTests
	{
		// ── No query string branch: queryHash + extension-from-segments ──────

		[Fact]
		public void NoQueryString_PathHasExtension_UsesPathExtension()
		{
			// URL has no query string AND a segment carries a '.' (extension).
			// Behavior: filename is "<sha256 of full URL>.<extension from segment>".
			// The path-segment scan walks all segments and picks the LAST one
			// containing '.', then uses that segment AS the extension.
			//
			// Lock the SHA-256 prefix length (64 hex chars) plus extension shape.
			// The hash value itself is content-deterministic on the input URL, so
			// a fixed input produces a fixed output — we assert the exact string.
			var result = Tools.GenerateFileName("https://www.example.com/path/page.html");

			// SHA-256 of "https://www.example.com/path/page.html" plus the last
			// segment "page.html" used as extension verbatim.
			Assert.Equal(
				"0c075699e3cbc7bf7604814b6d22d63da8ff6ff93e6981d8f2d728f68cc4e0cbpage.html",
				result);
		}

		[Fact]
		public void NoQueryString_PathHasNoExtension_DefaultsExtensionDotHtm()
		{
			// URL has no query string AND no path segment contains a '.'.
			// Behavior: extension defaults to ".htm" and the filename is
			// "<sha256>.htm".
			var result = Tools.GenerateFileName("https://www.example.com/path/page");

			Assert.Equal(
				"c0f8f2299f71f75ee325f453a70e7e3fc37e0607db288dc6feec2d709d92f1c7.htm",
				result);
		}

		[Fact]
		public void NoQueryString_MultipleDottedSegments_LastSegmentWins()
		{
			// "Last segment containing '.' wins" is the behavior of the foreach
			// loop. Lock it so a refactor (e.g. first-match-wins) shows up.
			// Path /a.b/c.d/e.f → extension becomes "e.f".
			var result = Tools.GenerateFileName("https://www.example.com/a.b/c.d/e.f");

			// SHA-256 of the URL followed by the last dotted segment as extension.
			Assert.Equal(
				"5343a689e01d9450e5553da42e782fa7463633a81234c2a872c91a9c5f3466e0e.f",
				result);
		}

		// ── Query string branch: AbsolutePath + ".htmlx" suffix if no ext ────

		[Fact]
		public void WithQueryString_AbsolutePathNoExtension_AppendsHtmlx()
		{
			// When the URL HAS a query string, the function takes a completely
			// different path: it returns AbsolutePath (which is just the path
			// portion, NO query) with ".htmlx" appended if the path has no
			// extension.
			//
			// This branch is the visible asymmetry in the function and worth
			// locking — the no-query branch produces a hashed name; the with-
			// query branch produces a path-derived name. That asymmetry is
			// load-bearing for some downstream tooling.
			var result = Tools.GenerateFileName("https://www.example.com/path/page?id=42");

			Assert.Equal("/path/page.htmlx", result);
		}

		[Fact]
		public void WithQueryString_AbsolutePathHasExtension_ReturnsPathAsIs()
		{
			// Path already has an extension AND URL has a query → return the
			// AbsolutePath verbatim (no ".htmlx" appended, no hash).
			var result = Tools.GenerateFileName("https://www.example.com/path/page.html?id=42");

			Assert.Equal("/path/page.html", result);
		}

		// ── Root URL edge cases ──────────────────────────────────────────────

		[Fact]
		public void NoQueryString_RootUrl_DefaultsToHtm()
		{
			// "/" is the only segment; it contains no '.', so extension stays at
			// the default ".htm".
			var result = Tools.GenerateFileName("https://www.example.com/");

			Assert.Equal(
				"49365e2b6b265ccba4bed01f5fa3cbcf6a028e5354d2b647f5eb37be735991c5.htm",
				result);
		}

		// ── Determinism: same input → same output ────────────────────────────

		[Fact]
		public void GenerateFileName_DeterministicForSameInput()
		{
			// Two calls with identical input must produce identical output. Locks
			// against any accidental introduction of nondeterminism (timestamps,
			// random salts, etc.).
			var a = Tools.GenerateFileName("https://www.example.com/page.html");
			var b = Tools.GenerateFileName("https://www.example.com/page.html");

			Assert.Equal(a, b);
		}

		[Fact]
		public void GenerateFileName_DifferentUrls_ProduceDifferentNames()
		{
			// Distinct URLs must produce distinct filenames. The query-hash branch
			// guarantees this by construction (SHA-256), but lock it as a
			// behavioral contract.
			var a = Tools.GenerateFileName("https://www.example.com/page-a");
			var b = Tools.GenerateFileName("https://www.example.com/page-b");

			Assert.NotEqual(a, b);
		}
	}
}
