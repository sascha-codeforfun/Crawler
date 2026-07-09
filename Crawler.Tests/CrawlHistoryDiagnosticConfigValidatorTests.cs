using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for <see cref="CrawlHistoryDiagnosticConfigValidator.CheckOrHalt"/>.
	/// Pure-function tests — no I/O, no filesystem. Logger needs initialization
	/// because the failure path writes to it (mirrors DictionaryIntegrityTests).
	/// </summary>
	[Collection("Logger")]
	public class CrawlHistoryDiagnosticConfigValidatorTests : IDisposable
	{
		private readonly string _tempDir;

		public CrawlHistoryDiagnosticConfigValidatorTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"chdv-test-{Guid.NewGuid()}");
			Directory.CreateDirectory(_tempDir);
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

		private static CrawlHistoryDiagnosticHeaderExtractor Extractor(
			string label, string pattern, bool enabled = true, string comment = "") =>
			new() { Label = label, Pattern = pattern, Enabled = enabled, Comment = comment };

		private static Config ConfigWith(params CrawlHistoryDiagnosticHeaderExtractor[] extractors) =>
			new()
			{
				CrawlHistoryDiagnostic = new CrawlHistoryDiagnosticConfig
				{
					HeaderExtractors = [.. extractors],
				},
			};

		// ── Null / empty inputs ───────────────────────────────────────────────

		[Fact]
		public void CheckOrHalt_NullConfig_ReturnsTrue()
		{
			// Defensive null guard — validator must not crash on a missing Config.
			Assert.True(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(null!));
		}

		[Fact]
		public void CheckOrHalt_NullDiagnosticContainer_ReturnsTrue()
		{
			// Config exists but CrawlHistoryDiagnostic is null (could happen with
			// hand-rolled Config instances or stripped-down JSON).
			var config = new Config { CrawlHistoryDiagnostic = null! };
			Assert.True(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_EmptyHeaderExtractorsList_ReturnsTrue()
		{
			// Default-constructed CrawlHistoryDiagnostic has empty lists.
			// Nothing to validate → pass.
			var config = ConfigWith();
			Assert.True(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		// ── Disabled entries are skipped ──────────────────────────────────────

		[Fact]
		public void CheckOrHalt_DisabledEntryWithInvalidRegex_ReturnsTrue()
		{
			// Disabled entries are allowed to carry broken patterns — the operator
			// may have disabled the entry precisely BECAUSE it was broken, and is
			// keeping the audit trail until they can fix it.
			var config = ConfigWith(
				Extractor("Broken", "([unclosed", enabled: false));
			Assert.True(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_AllDisabled_ReturnsTrue()
		{
			var config = ConfigWith(
				Extractor("A", "valid=([^;]+)", enabled: false),
				Extractor("B", "also=([^;]+)", enabled: false));
			Assert.True(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		// ── Valid entries pass ────────────────────────────────────────────────

		[Fact]
		public void CheckOrHalt_SingleValidEntry_ReturnsTrue()
		{
			var config = ConfigWith(
				Extractor("Cookie", "FOO=([^;]+)"));
			Assert.True(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_MultipleValidEntries_ReturnsTrue()
		{
			var config = ConfigWith(
				Extractor("Cookie", "FOO=([^;]+)"),
				Extractor("Request", "X-Request-Id:\\s*(\\S+)"));
			Assert.True(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_RegexWithMultipleCaptureGroups_ReturnsTrue()
		{
			// Multiple groups are permitted; group 1 wins silently per documented
			// contract. Validator only requires AT LEAST one capture group.
			var config = ConfigWith(
				Extractor("Both", "(foo)=(bar)"));
			Assert.True(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		// ── Invalid entries halt ──────────────────────────────────────────────

		[Fact]
		public void CheckOrHalt_EmptyLabel_ReturnsFalse()
		{
			var config = ConfigWith(
				Extractor("", "FOO=([^;]+)"));
			Assert.False(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_WhitespaceLabel_ReturnsFalse()
		{
			var config = ConfigWith(
				Extractor("   ", "FOO=([^;]+)"));
			Assert.False(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_EmptyPattern_ReturnsFalse()
		{
			var config = ConfigWith(
				Extractor("Label", ""));
			Assert.False(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_WhitespacePattern_ReturnsFalse()
		{
			var config = ConfigWith(
				Extractor("Label", "   "));
			Assert.False(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_InvalidRegexSyntax_ReturnsFalse()
		{
			// Unclosed capture group — ArgumentException at Regex construction.
			var config = ConfigWith(
				Extractor("Broken", "([unclosed"));
			Assert.False(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_RegexWithoutCaptureGroup_ReturnsFalse()
		{
			// Pattern compiles but has no capture group — nothing to extract.
			// Validator requires GetGroupNumbers().Length >= 2 (group 0 is the
			// implicit whole-match group, plus at least one user group).
			var config = ConfigWith(
				Extractor("NoCapture", "literal"));
			Assert.False(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		// ── Mixed entries: invalid present anywhere → halt ────────────────────

		[Fact]
		public void CheckOrHalt_OneValidOneInvalid_ReturnsFalse()
		{
			var config = ConfigWith(
				Extractor("Good", "FOO=([^;]+)"),
				Extractor("Bad", "([unclosed"));
			Assert.False(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_ValidThenInvalidThenDisabled_ReturnsFalse()
		{
			// Disabled entry doesn't rescue an invalid earlier entry.
			var config = ConfigWith(
				Extractor("Good", "FOO=([^;]+)"),
				Extractor("Bad", "([unclosed"),
				Extractor("Hidden", "([alsobad", enabled: false));
			Assert.False(CrawlHistoryDiagnosticConfigValidator.CheckOrHalt(config));
		}
	}
}
