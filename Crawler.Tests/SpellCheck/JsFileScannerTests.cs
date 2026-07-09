using System;
using Crawler.Lexicon;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 642: external .js spell-check scanner. The scan loop is the shared ScanText core (tested
	/// via the inline path); these cover the file-scanner's own surface — which files it picks, the
	/// UTF-8-with-replacement read that underpins the mixed-encoding safety, and size formatting. Uses a
	/// generic tenant-free temp tree.
	/// </summary>
	public class JsFileScannerTests
	{
		[Fact]
		public void EnumerateJsFiles_TakesJs_ExcludesJsonAndBase64Assets()
		{
			string root = Path.Combine(Path.GetTempPath(), "jsfs_" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				File.WriteAllText(Path.Combine(root, "app.js"), "var a=1;");
				File.WriteAllText(Path.Combine(root, "data.json"), "{}");
				string sub = Path.Combine(root, "sub");
				Directory.CreateDirectory(sub);
				File.WriteAllText(Path.Combine(sub, "vendor.js"), "var b=2;");
				string stripped = Path.Combine(root, "base64assets", "sourcesstripped");
				Directory.CreateDirectory(stripped);
				File.WriteAllText(Path.Combine(stripped, "stripped.js"), "var c=3;");

				var got = JsFileScanner.EnumerateJsFiles(root).Select(Path.GetFileName).ToList();

				Assert.Contains("app.js", got);
				Assert.Contains("vendor.js", got);          // recurses subdirs
				Assert.DoesNotContain("data.json", got);     // .json mask collision excluded
				Assert.DoesNotContain("stripped.js", got);   // base64assets subtree excluded
				Assert.Equal(2, got.Count);
			}
			finally
			{
				Directory.Delete(root, true);
			}
		}

		[Fact]
		public void EnumerateJsFiles_MissingDirectory_IsEmpty()
		{
			var got = JsFileScanner.EnumerateJsFiles(Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N")));
			Assert.Empty(got);
		}

		[Fact]
		public void PathHasSegment_MatchesDirectorySegmentOnly()
		{
			Assert.True(JsFileScanner.PathHasSegment(Path.Combine("a", "base64assets", "b.js"), "base64assets"));
			Assert.False(JsFileScanner.PathHasSegment(Path.Combine("a", "scripts", "b.js"), "base64assets"));
		}

		[Fact]
		public void ReadJsFile_InvalidBytes_BecomeReplacementChar_NotMojibake()
		{
			string f = Path.Combine(Path.GetTempPath(), "jsread_" + Guid.NewGuid().ToString("N") + ".js");
			try
			{
				// 0xFF is not valid UTF-8 — must surface as U+FFFD so ScanText can skip the literal.
				File.WriteAllBytes(f, new byte[] { (byte)'"', 0xFF, (byte)'"' });
				string s = JsFileScanner.ReadJsFile(f);
				Assert.Contains('\uFFFD', s);
			}
			finally
			{
				File.Delete(f);
			}
		}

		[Fact]
		public void ReadJsFile_ValidUtf8Umlaut_IsPreserved()
		{
			string f = Path.Combine(Path.GetTempPath(), "jsread_" + Guid.NewGuid().ToString("N") + ".js");
			try
			{
				// "für" in UTF-8 (f, U+00FC as C3 BC, r) — must decode cleanly, no replacement char.
				File.WriteAllBytes(f, new byte[] { (byte)'f', 0xC3, 0xBC, (byte)'r' });
				string s = JsFileScanner.ReadJsFile(f);
				Assert.Equal("für", s);
				Assert.DoesNotContain('\uFFFD', s);
			}
			finally
			{
				File.Delete(f);
			}
		}

		[Fact]
		public void FormatSize_IsCultureInvariant()
		{
			Assert.Equal("512 B", JsFileScanner.FormatSize(512));
			Assert.Equal("1.0 KB", JsFileScanner.FormatSize(1024));
			Assert.Equal("2.0 MB", JsFileScanner.FormatSize(2L * 1024 * 1024));
		}

		// 659: per-bundle finding assembly — dedupe to distinct words (first excerpt,
		// original order) and the reach-based bulk/clear routing decision.

		[Fact]
		public void BuildBundleFindings_DedupesByWord_KeepsFirstExcerptInOrder()
		{
			var hits = new[]
			{
				new ScriptWordHit("helo", "ex1"),
				new ScriptWordHit("welt", "ex2"),
				new ScriptWordHit("helo", "ex3"), // duplicate word → dropped
			};

			var r = JsFileScanner.BuildBundleFindings(
				"bundle.js", "key", "https://site.test/bundle.js", reach: 1, reachThreshold: 5,
				pages: new List<string>(), rawHits: hits);

			Assert.Equal(2, r.Words.Count);
			Assert.Equal("helo", r.Words[0].Word);
			Assert.Equal("ex1", r.Words[0].Excerpt); // first excerpt kept
			Assert.Equal("welt", r.Words[1].Word);
		}

		[Theory]
		[InlineData(6, 5, true)]   // reach > threshold → bulk
		[InlineData(5, 5, false)]  // at threshold → not bulk
		[InlineData(2, 5, false)]  // below threshold → not bulk
		public void BuildBundleFindings_IsBulk_WhenReachExceedsThreshold(int reach, int threshold, bool expected)
		{
			var r = JsFileScanner.BuildBundleFindings(
				"b.js", "key", "https://site.test/b.js", reach, threshold,
				pages: new List<string>(), rawHits: Array.Empty<ScriptWordHit>());

			Assert.Equal(expected, r.IsBulk);
		}

		// Run's dictionary-validation early return (no usable dictionaries) — no-op
		// scan that still writes the four logs and reports an all-zero Result.

		private static JsFileScanner.Result RunEarly(
			string root, IReadOnlyList<string> dictionaries,
			IReadOnlyDictionary<string, Bundle> bundles)
			=> JsFileScanner.Run(
				root,
				Path.Combine(root, "30.log"),
				Path.Combine(root, "31.log"),
				Path.Combine(root, "32.log"),
				Path.Combine(root, "33.log"),
				"*.html",
				reachThreshold: 3,
				fileToUrl: f => "https://site.test/" + f,
				dictionaries: dictionaries,
				bundles: bundles,
				prefixesToStrip: Array.Empty<string>(),
				fugenelemente: Array.Empty<string>(),
				tokensToFilter: Array.Empty<string>());

		[Fact]
		public void Run_NoDictionariesConfigured_ReturnsEmptyResultAndWritesLogs()
		{
			string root = Path.Combine(Path.GetTempPath(), "jsfs_" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				var r = RunEarly(root, new List<string>(), new Dictionary<string, Bundle>());

				Assert.Equal(0, r.Files);
				Assert.Equal(0, r.Findings);
				Assert.Empty(r.BundleFindings);
				Assert.True(File.Exists(Path.Combine(root, "30.log")));
				Assert.True(File.Exists(Path.Combine(root, "31.log")));
				Assert.True(File.Exists(Path.Combine(root, "32.log")));
				Assert.True(File.Exists(Path.Combine(root, "33.log")));
			}
			finally
			{
				Directory.Delete(root, true);
			}
		}

		[Fact]
		public void Run_NamedDictionaryNotLoaded_ReturnsEmptyResult()
		{
			string root = Path.Combine(Path.GetTempPath(), "jsfs_" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try
			{
				// Named but absent from the bundle map → no-op early return.
				var r = RunEarly(root, new List<string> { "en" }, new Dictionary<string, Bundle>());

				Assert.Equal(0, r.Files);
				Assert.Equal(0, r.Findings);
				Assert.Empty(r.BundleFindings);
			}
			finally
			{
				Directory.Delete(root, true);
			}
		}
	}
}
