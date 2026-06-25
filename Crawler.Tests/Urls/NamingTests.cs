using Xunit;
using Crawler.Urls;

namespace Crawler.Tests.Urls
{
	/// <summary>
	/// Locks the behavior of <see cref="Naming.GenerateFileName"/>, the function
	/// that derives the on-disk filename for every downloaded asset. Regression
	/// here changes filenames on disk — breaks cross-archive correlation, breaks
	/// sidecar pairing (the <c>.header</c> companion file derives its name from
	/// the body filename via <c>Path.ChangeExtension</c>), breaks any external
	/// tooling that grew used to the current pattern. The on-disk schema is a
	/// load-bearing contract; lock it. Also covers the internal SHA-256 helper
	/// <see cref="Naming.Hash"/> that backs the generated name.
	///
	/// No HTTP, no filesystem — pure-function tests.
	/// </summary>
	public class NamingTests
	{
		// ── GenerateFileName: no query string branch (queryHash + ext) ───────

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
			var result = Naming.GenerateFileName("https://www.example.com/path/page.html");

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
			var result = Naming.GenerateFileName("https://www.example.com/path/page");

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
			var result = Naming.GenerateFileName("https://www.example.com/a.b/c.d/e.f");

			// SHA-256 of the URL followed by the last dotted segment as extension.
			Assert.Equal(
				"5343a689e01d9450e5553da42e782fa7463633a81234c2a872c91a9c5f3466e0e.f",
				result);
		}

		// ── GenerateFileName: query string branch (now hashed + contained) ───

		[Fact]
		public void WithQueryString_NoPathExtension_HashesWithDefaultHtm()
		{
			// A query-bearing URL no longer falls through to uri.AbsolutePath (a
			// rooted, separator-bearing path that could escape the capture root).
			// It is hashed like any other URL — the full AbsoluteUri (query
			// included) is hashed, and an extensionless path defaults to ".htm".
			var result = Naming.GenerateFileName("https://www.example.com/path/page?id=42");

			Assert.Equal(
				"d9964337198f11c94d6b2756a5df2a9d153221953ad3032aaecc3b6fa940a179.htm",
				result);
		}

		[Fact]
		public void WithQueryString_PathHasExtension_HashesWithSegmentExtension()
		{
			// Path extension is preserved via the segment scan; the query is part
			// of the hash so distinct query variants map to distinct files.
			var result = Naming.GenerateFileName("https://www.example.com/path/page.html?id=42");

			Assert.Equal(
				"992a51b19619b9cc43a44100cf9425b7256ecc202bb100f5e92a6ba204edce59page.html",
				result);
		}

		[Fact]
		public void WithQueryString_RootedPathAsset_NameCannotEscapeRoot()
		{
			// Regression for the containment escape: a query-bearing asset whose
			// path is deep and rooted must still resolve to a single contained
			// segment — no separators, no leading separator — so Path.Combine can
			// never place it outside the download directory.
			var result = Naming.GenerateFileName(
				"https://assets.example.test/etc/clientlibs/widget/fonts/icons.eot?cea82454fe6c");

			Assert.DoesNotContain('/', result);
			Assert.DoesNotContain('\\', result);
			Assert.False(System.IO.Path.IsPathRooted(result));
			Assert.EndsWith("icons.eot", result);
		}

		// ── GenerateFileName: root URL edge case ─────────────────────────────

		[Fact]
		public void NoQueryString_RootUrl_DefaultsToHtm()
		{
			// "/" is the only segment; it contains no '.', so extension stays at
			// the default ".htm".
			var result = Naming.GenerateFileName("https://www.example.com/");

			Assert.Equal(
				"49365e2b6b265ccba4bed01f5fa3cbcf6a028e5354d2b647f5eb37be735991c5.htm",
				result);
		}

		// ── GenerateFileName: determinism ────────────────────────────────────

		[Fact]
		public void GenerateFileName_DeterministicForSameInput()
		{
			// Two calls with identical input must produce identical output. Locks
			// against any accidental introduction of nondeterminism (timestamps,
			// random salts, etc.).
			var a = Naming.GenerateFileName("https://www.example.com/page.html");
			var b = Naming.GenerateFileName("https://www.example.com/page.html");

			Assert.Equal(a, b);
		}

		[Fact]
		public void GenerateFileName_DifferentUrls_ProduceDifferentNames()
		{
			// Distinct URLs must produce distinct filenames. The query-hash branch
			// guarantees this by construction (SHA-256), but lock it as a
			// behavioral contract.
			var a = Naming.GenerateFileName("https://www.example.com/page-a");
			var b = Naming.GenerateFileName("https://www.example.com/page-b");

			Assert.NotEqual(a, b);
		}

		// ── Hash (internal SHA-256 helper) ───────────────────────────────────

		[Fact]
		public void Hash_IsStableForSameInput()
		{
			var h1 = Naming.Hash("https://example.com/page");
			var h2 = Naming.Hash("https://example.com/page");

			Assert.Equal(h1, h2);
		}

		[Fact]
		public void Hash_DiffersForDifferentInputs()
		{
			var h1 = Naming.Hash("https://example.com/page-a");
			var h2 = Naming.Hash("https://example.com/page-b");

			Assert.NotEqual(h1, h2);
		}

		[Fact]
		public void Hash_ReturnsSha256HexString_64Chars()
		{
			var h = Naming.Hash("anything");

			Assert.Equal(64, h.Length);
			Assert.Matches("^[0-9a-f]+$", h);
		}

		[Fact]
		public void Hash_ThrowsOnEmptyInput()
		{
			Assert.Throws<ArgumentException>(() => Naming.Hash(""));
		}

		[Fact]
		public void Hash_ThrowsOnNullInput()
		{
			Assert.Throws<ArgumentException>(() => Naming.Hash(null!));
		}
	}
}
