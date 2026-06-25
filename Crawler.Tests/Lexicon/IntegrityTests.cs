using System.Security.Cryptography;
using System.Text;
using Xunit;
using Crawler.Lexicon;

namespace Crawler.Tests.Lexicon
{
	/// <summary>
	/// Fileset #287 — Dictionary integrity verification.
	/// </summary>
	[Collection("Logger")]
	public class IntegrityTests : IDisposable
	{
		private readonly string _tempDir;

		public IntegrityTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"di-test-{Guid.NewGuid()}");
			Directory.CreateDirectory(_tempDir);
			// Required because CheckOrHalt writes to Logger on the failure
			// path. Without Initialize, Logger.Log throws.
			Logger.Initialize(Path.Combine(_tempDir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			if (Directory.Exists(_tempDir))
			{
				Directory.Delete(_tempDir, recursive: true);
			}
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		/// <summary>
		/// Writes the given content to a file in the temp dir and returns
		/// (path, sha256-hex-lowercase).
		/// </summary>
		private (string Path, string ExpectedSha256) TmpFileWithChecksum(string name, string content)
		{
			var path = System.IO.Path.Combine(_tempDir, name);
			var bytes = Encoding.UTF8.GetBytes(content);
			File.WriteAllBytes(path, bytes);
			var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
			return (path, hash);
		}

		private DictionaryBundleConfig Bundle(
			string lang,
			string dicFile, string dicSum,
			string affFile, string affSum) =>
			new()
			{
				LanguageCode = lang,
				// 652 — DisplayName is required (CheckOrHalt halts on a nameless bundle). These fixtures
				// exercise file/checksum integrity, not naming, so any non-empty name keeps them valid;
				// the language code stands in.
				DisplayName = lang,
				DicFile = dicFile,
				DicChecksum = dicSum,
				AffFile = affFile,
				AffChecksum = affSum,
			};

		// ── ComputeSha256 ─────────────────────────────────────────────────────

		[Fact]
		public void ComputeSha256_KnownContent_ReturnsExpectedHash()
		{
			// SHA-256 of "abc" (UTF-8 / ASCII) is well-known:
			//   ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
			var (path, expected) = TmpFileWithChecksum("known.txt", "abc");
			var actual = Integrity.ComputeSha256(path);

			Assert.Equal(expected, actual);
			Assert.Equal(
				"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
				actual);
		}

		[Fact]
		public void ComputeSha256_ReturnsLowercaseHex64Chars()
		{
			var (path, _) = TmpFileWithChecksum("sized.txt", "some content here");
			var actual = Integrity.ComputeSha256(path);

			Assert.Equal(64, actual.Length);
			Assert.Equal(actual.ToLowerInvariant(), actual);
			Assert.Matches("^[0-9a-f]{64}$", actual);
		}

		[Fact]
		public void ComputeSha256_DifferentContent_DifferentHash()
		{
			var (path1, _) = TmpFileWithChecksum("one.txt", "content A");
			var (path2, _) = TmpFileWithChecksum("two.txt", "content B");

			Assert.NotEqual(
				Integrity.ComputeSha256(path1),
				Integrity.ComputeSha256(path2));
		}

		// ── VerifyField ───────────────────────────────────────────────────────

		[Fact]
		public void VerifyField_MatchingChecksum_ReturnsPass()
		{
			var (path, sum) = TmpFileWithChecksum("de.dic", "dictionary content");

			var result = Integrity.VerifyField(
				"de", "DicFile", path, "DicChecksum", sum);

			Assert.Equal(Integrity.FieldStatus.Pass, result.Status);
			Assert.Equal(sum, result.ActualChecksum);
			Assert.Equal(sum, result.ExpectedChecksum);
		}

		[Fact]
		public void VerifyField_EmptyChecksum_ReturnsMissingChecksum()
		{
			// First-run bootstrap path: file exists, but checksum field is
			// empty in config. Status = MissingChecksum, ActualChecksum
			// populated so the operator can paste it in.
			var (path, sum) = TmpFileWithChecksum("de.dic", "dictionary content");

			var result = Integrity.VerifyField(
				"de", "DicFile", path, "DicChecksum", expectedChecksum: "");

			Assert.Equal(Integrity.FieldStatus.MissingChecksum, result.Status);
			Assert.Equal(sum, result.ActualChecksum);
			Assert.Equal("", result.ExpectedChecksum);
		}

		[Fact]
		public void VerifyField_MismatchedChecksum_ReturnsMismatch()
		{
			var (path, actualSum) = TmpFileWithChecksum("de.dic", "real content");
			var wrongSum = new string('f', 64); // valid hex shape, wrong value

			var result = Integrity.VerifyField(
				"de", "DicFile", path, "DicChecksum", wrongSum);

			Assert.Equal(Integrity.FieldStatus.Mismatch, result.Status);
			Assert.Equal(actualSum, result.ActualChecksum);
			Assert.Equal(wrongSum, result.ExpectedChecksum);
		}

		[Fact]
		public void VerifyField_MissingFile_ReturnsMissingFile()
		{
			var path = System.IO.Path.Combine(_tempDir, "does-not-exist.dic");

			var result = Integrity.VerifyField(
				"de", "DicFile", path, "DicChecksum",
				expectedChecksum: new string('a', 64));

			Assert.Equal(Integrity.FieldStatus.MissingFile, result.Status);
			Assert.Equal("", result.ActualChecksum);
		}

		[Fact]
		public void VerifyField_EmptyFilePath_ReturnsMissingFile()
		{
			// Defensive: empty DicFile path is a config error, treated as
			// MissingFile so the halt path surfaces it consistently.
			var result = Integrity.VerifyField(
				"de", "DicFile", filePath: "", "DicChecksum",
				expectedChecksum: "");

			Assert.Equal(Integrity.FieldStatus.MissingFile, result.Status);
		}

		[Fact]
		public void VerifyField_CaseInsensitiveChecksumCompare_StillPass()
		{
			// Operators may paste uppercase hex (CertUtil default on Windows).
			// We should match case-insensitively against the lowercase actual.
			var (path, lowerSum) = TmpFileWithChecksum("de.dic", "content");
			var upperSum = lowerSum.ToUpperInvariant();

			var result = Integrity.VerifyField(
				"de", "DicFile", path, "DicChecksum", upperSum);

			Assert.Equal(Integrity.FieldStatus.Pass, result.Status);
		}

		[Fact]
		public void VerifyField_LeadingTrailingWhitespaceInExpected_StillPass()
		{
			// Operators paste imperfectly. Tolerate surrounding whitespace
			// without making it a mismatch.
			var (path, sum) = TmpFileWithChecksum("de.dic", "content");
			var paddedSum = $"  {sum}  ";

			var result = Integrity.VerifyField(
				"de", "DicFile", path, "DicChecksum", paddedSum);

			Assert.Equal(Integrity.FieldStatus.Pass, result.Status);
		}

		// ── VerifyAll ─────────────────────────────────────────────────────────

		[Fact]
		public void VerifyAll_EmptyBundles_ReturnsEmpty()
		{
			var result = Integrity.VerifyAll([]);
			Assert.Empty(result);
		}

		[Fact]
		public void VerifyAll_AllPass_ReturnsAllPass()
		{
			var (dicPath, dicSum) = TmpFileWithChecksum("de.dic", "dic content");
			var (affPath, affSum) = TmpFileWithChecksum("de.aff", "aff content");

			var result = Integrity.VerifyAll([
				Bundle("de", dicPath, dicSum, affPath, affSum),
			]);

			Assert.Equal(2, result.Count);
			Assert.All(result, r => Assert.Equal(Integrity.FieldStatus.Pass, r.Status));
		}

		[Fact]
		public void VerifyAll_PerBundleTwoResults_DicThenAff()
		{
			var (dicPath, dicSum) = TmpFileWithChecksum("de.dic", "dic content");
			var (affPath, affSum) = TmpFileWithChecksum("de.aff", "aff content");

			var result = Integrity.VerifyAll([
				Bundle("de", dicPath, dicSum, affPath, affSum),
			]);

			Assert.Equal("DicFile", result[0].FieldName);
			Assert.Equal("AffFile", result[1].FieldName);
		}

		[Fact]
		public void VerifyAll_MultipleBundles_AllVerified()
		{
			var (deDic, deDicSum) = TmpFileWithChecksum("de.dic", "de");
			var (deAff, deAffSum) = TmpFileWithChecksum("de.aff", "de aff");
			var (frDic, frDicSum) = TmpFileWithChecksum("fr.dic", "fr");
			var (frAff, frAffSum) = TmpFileWithChecksum("fr.aff", "fr aff");

			var result = Integrity.VerifyAll([
				Bundle("de", deDic, deDicSum, deAff, deAffSum),
				Bundle("fr", frDic, frDicSum, frAff, frAffSum),
			]);

			Assert.Equal(4, result.Count);
			Assert.All(result, r => Assert.Equal(Integrity.FieldStatus.Pass, r.Status));
		}

		[Fact]
		public void VerifyAll_MixedFailures_AllReported()
		{
			// One bundle passes, one has missing checksum, one has mismatch.
			var (okDic, okDicSum) = TmpFileWithChecksum("ok.dic", "ok");
			var (okAff, okAffSum) = TmpFileWithChecksum("ok.aff", "ok aff");
			var (newDic, newDicSum) = TmpFileWithChecksum("new.dic", "new");
			var (newAff, newAffSum) = TmpFileWithChecksum("new.aff", "new aff");
			var (badDic, badActual) = TmpFileWithChecksum("bad.dic", "real content");

			var result = Integrity.VerifyAll([
				Bundle("ok",  okDic,  okDicSum,  okAff, okAffSum),                     // both pass
				Bundle("new", newDic, "",        newAff, ""),                          // bootstrap
				Bundle("bad", badDic, new string('0', 64), okAff, okAffSum),           // mismatch on dic
			]);

			Assert.Equal(6, result.Count);
			Assert.Equal(2, result.Count(r => r.Status == Integrity.FieldStatus.Pass
				&& r.LanguageCode == "ok"));
			Assert.Equal(2, result.Count(r => r.Status == Integrity.FieldStatus.MissingChecksum
				&& r.LanguageCode == "new"));
			Assert.Equal(1, result.Count(r => r.Status == Integrity.FieldStatus.Mismatch
				&& r.LanguageCode == "bad"));
		}

		// ── BuildHaltMessage ──────────────────────────────────────────────────

		[Fact]
		public void BuildHaltMessage_OnlyMissingChecksums_ContainsBootstrapSection()
		{
			List<Integrity.FieldResult> failures =
			[
				new("de", "DicFile", "dictionaries/de.dic", "DicChecksum",
					Integrity.FieldStatus.MissingChecksum,
					ExpectedChecksum: "",
					ActualChecksum: new string('1', 64)),
			];

			var msg = Integrity.BuildHaltMessage(failures);

			Assert.Contains("BOOTSTRAP", msg);
			Assert.Contains("\"DicChecksum\"", msg);
			Assert.Contains(new string('1', 64), msg);
			Assert.Contains("WHY THIS HALT EXISTS", msg);
		}

		[Fact]
		public void BuildHaltMessage_OnlyMismatch_ContainsMismatchSection()
		{
			List<Integrity.FieldResult> failures =
			[
				new("de", "DicFile", "dictionaries/de.dic", "DicChecksum",
					Integrity.FieldStatus.Mismatch,
					ExpectedChecksum: new string('e', 64),
					ActualChecksum:   new string('a', 64)),
			];

			var msg = Integrity.BuildHaltMessage(failures);

			Assert.Contains("MISMATCH", msg);
			Assert.Contains(new string('e', 64), msg);
			Assert.Contains(new string('a', 64), msg);
			Assert.Contains("Paste-ready replacement", msg);
			Assert.DoesNotContain("BOOTSTRAP", msg);
		}

		[Fact]
		public void BuildHaltMessage_OnlyMissingFile_ContainsMissingFileSection()
		{
			List<Integrity.FieldResult> failures =
			[
				new("de", "DicFile", "dictionaries/missing.dic", "DicChecksum",
					Integrity.FieldStatus.MissingFile,
					ExpectedChecksum: "",
					ActualChecksum:   ""),
			];

			var msg = Integrity.BuildHaltMessage(failures);

			Assert.Contains("MISSING DICTIONARY FILES", msg);
			Assert.Contains("dictionaries/missing.dic", msg);
		}

		[Fact]
		public void BuildHaltMessage_AllThreeCategories_AllSectionsPresent()
		{
			// Mixed: one of each category. Operator sees the complete
			// picture in a single halt — fix everything in one config edit.
			List<Integrity.FieldResult> failures =
			[
				new("a", "DicFile", "a.dic",       "DicChecksum",
					Integrity.FieldStatus.MissingFile,
					"", ""),
				new("b", "DicFile", "b.dic",       "DicChecksum",
					Integrity.FieldStatus.MissingChecksum,
					"", new string('b', 64)),
				new("c", "DicFile", "c.dic",       "DicChecksum",
					Integrity.FieldStatus.Mismatch,
					new string('e', 64), new string('c', 64)),
			];

			var msg = Integrity.BuildHaltMessage(failures);

			Assert.Contains("MISSING DICTIONARY FILES", msg);
			Assert.Contains("BOOTSTRAP", msg);
			Assert.Contains("MISMATCH", msg);
		}

		[Fact]
		public void BuildHaltMessage_BootstrapGroupsBundleByLanguage()
		{
			// When a bundle has BOTH Dic and Aff missing checksums, they
			// should appear together under the bundle header — not split.
			List<Integrity.FieldResult> failures =
			[
				new("de", "DicFile", "de.dic", "DicChecksum",
					Integrity.FieldStatus.MissingChecksum,
					"", new string('d', 64)),
				new("de", "AffFile", "de.aff", "AffChecksum",
					Integrity.FieldStatus.MissingChecksum,
					"", new string('a', 64)),
			];

			var msg = Integrity.BuildHaltMessage(failures);

			// The bundle header appears once.
			var bundleHeaderCount = msg.Split("\"de\":").Length - 1;
			Assert.Equal(1, bundleHeaderCount);
			Assert.Contains("\"DicChecksum\"", msg);
			Assert.Contains("\"AffChecksum\"", msg);
		}

		// ── CheckOrHalt ───────────────────────────────────────────────────────

		[Fact]
		public void CheckOrHalt_NullConfig_ReturnsTrue()
		{
			// Defensive: null config (shouldn't happen post-load-guard, but
			// don't crash).
			Assert.True(Integrity.CheckOrHalt(null!));
		}

		[Fact]
		public void CheckOrHalt_NoBundles_ReturnsTrue()
		{
			// No bundles configured = nothing to verify.
			var config = new Config { DictionaryBundles = [] };
			Assert.True(Integrity.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_AllBundlesPass_ReturnsTrue()
		{
			var (dicPath, dicSum) = TmpFileWithChecksum("de.dic", "x");
			var (affPath, affSum) = TmpFileWithChecksum("de.aff", "y");
			var config = new Config
			{
				DictionaryBundles = [Bundle("de", dicPath, dicSum, affPath, affSum)],
			};

			Assert.True(Integrity.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_BundleMismatch_ReturnsFalse()
		{
			var (dicPath, _) = TmpFileWithChecksum("de.dic", "x");
			var (affPath, _) = TmpFileWithChecksum("de.aff", "y");
			var config = new Config
			{
				DictionaryBundles =
				[
					Bundle("de", dicPath, new string('0', 64),
							   affPath, new string('0', 64)),
				],
			};

			Assert.False(Integrity.CheckOrHalt(config));
		}
	}
}
