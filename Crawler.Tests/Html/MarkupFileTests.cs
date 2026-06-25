using Xunit;
using System.Text;
using Crawler.Html;

namespace Crawler.Tests.Html
{
	/// <summary>
	/// Characterization tests for <see cref="MarkupFile.ReadHeadBytes"/>, added when
	/// the method was extracted from Tools. The pre-existing coverage was transitive
	/// (via Sitemap) and only exercised the happy "</head> found" path, leaving the
	/// non-positive-cap, no-</head>, cap-reached, and whitespace-tolerance branches
	/// unasserted. These lock the current behaviour of those branches, per Tests.md
	/// ("extract a piece of pure logic out of that file → unit-test the extracted piece").
	///
	/// RemoveByXPath is intentionally left without a direct test: it is
	/// [ExcludeFromCodeCoverage] by design and exercised end-to-end by
	/// BoilerplateSimplifierEquivalenceTests.
	/// </summary>
	public class MarkupFileTests : IDisposable
	{
		private readonly List<string> _temp = new();

		private string WriteTemp(byte[] bytes)
		{
			var p = Path.GetTempFileName();
			File.WriteAllBytes(p, bytes);
			_temp.Add(p);
			return p;
		}

		private string WriteTemp(string ascii) => WriteTemp(Encoding.ASCII.GetBytes(ascii));

		public void Dispose()
		{
			foreach (var p in _temp)
			{
				try { File.Delete(p); } catch { }
			}
		}

		[Fact]
		public void ReadHeadBytes_NonPositiveCap_ReturnsEmpty_NotCapped()
		{
			// maxBytes <= 0 short-circuits before any read: empty result, not "capped".
			var path = WriteTemp("<html><head></head></html>");

			var result = MarkupFile.ReadHeadBytes(path, 0, out bool reachedCap);

			Assert.Empty(result);
			Assert.False(reachedCap);
		}

		[Fact]
		public void ReadHeadBytes_HeadPresent_ReturnsThroughClosingHead_NotCapped()
		{
			// The happy path: bytes are returned up to and including </head>, nothing
			// after it, and reachedCap stays false.
			var path = WriteTemp("<html><head><title>x</title></head><body>rest</body></html>");

			var result = MarkupFile.ReadHeadBytes(path, 16384, out bool reachedCap);

			var text = Encoding.ASCII.GetString(result);
			Assert.EndsWith("</head>", text);
			Assert.DoesNotContain("<body>", text);
			Assert.False(reachedCap);
		}

		[Fact]
		public void ReadHeadBytes_NoClosingHead_FileUnderCap_ReturnsWholeFile_NotCapped()
		{
			// No </head> and the file fits under the cap → return the whole file,
			// reachedCap false (the cap was never the limiting factor).
			const string content = "<html><body>no closing head here</body></html>";
			var path = WriteTemp(content);

			var result = MarkupFile.ReadHeadBytes(path, content.Length + 1000, out bool reachedCap);

			Assert.Equal(content, Encoding.ASCII.GetString(result));
			Assert.False(reachedCap);
		}

		[Fact]
		public void ReadHeadBytes_NoClosingHead_FileOverCap_ReturnsCapBytes_Capped()
		{
			// No </head> within a file larger than the cap → return exactly maxBytes
			// bytes and signal reachedCap = true. This is the primary truncation
			// signal callers rely on, previously unasserted.
			var bytes = Encoding.ASCII.GetBytes(new string('a', 5000)); // contains no "</head>"
			var path = WriteTemp(bytes);

			var result = MarkupFile.ReadHeadBytes(path, 100, out bool reachedCap);

			Assert.Equal(100, result.Length);
			Assert.True(reachedCap);
		}

		[Fact]
		public void ReadHeadBytes_ClosingHeadWithWhitespaceBeforeGt_ToleratedAndIncluded()
		{
			// "</head >" (whitespace before the '>') is tolerated by the lookahead and
			// the returned bytes include through the '>'.
			const string content = "xx</head >yy";
			var path = WriteTemp(content);

			var result = MarkupFile.ReadHeadBytes(path, 16384, out bool reachedCap);

			Assert.Equal("xx</head >", Encoding.ASCII.GetString(result));
			Assert.False(reachedCap);
		}
	}
}
