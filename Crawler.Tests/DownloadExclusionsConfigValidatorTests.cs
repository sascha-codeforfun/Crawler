using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for <see cref="DownloadExclusionsConfigValidator.CheckOrHalt"/>.
	/// Pure-function tests — no I/O. Logger needs initialization because the
	/// failure path writes to it (mirrors DictionaryIntegrityTests pattern).
	/// </summary>
	[Collection("Logger")]
	public class DownloadExclusionsConfigValidatorTests : IDisposable
	{
		private readonly string _tempDir;

		public DownloadExclusionsConfigValidatorTests()
		{
			_tempDir = Path.Combine(Path.GetTempPath(), $"dxv-test-{Guid.NewGuid()}");
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

		private static CrawlLinkExclusion Entry(string value, bool enabled = true, string comment = "") =>
			new() { Value = value, Enabled = enabled, Comment = comment };

		private static Config ConfigWith(params CrawlLinkExclusion[] entries) =>
			new() { DownloadExclusions = [.. entries] };

		// ── Null / empty inputs ───────────────────────────────────────────────

		[Fact]
		public void CheckOrHalt_NullConfig_ReturnsTrue()
		{
			Assert.True(DownloadExclusionsConfigValidator.CheckOrHalt(null!));
		}

		[Fact]
		public void CheckOrHalt_EmptyList_ReturnsTrue()
		{
			// Empty list is the default shipped state and a perfectly valid
			// operator choice (no operational filtering). NOT an error.
			var config = ConfigWith();
			Assert.True(DownloadExclusionsConfigValidator.CheckOrHalt(config));
		}

		// ── Disabled entries are skipped ──────────────────────────────────────

		[Fact]
		public void CheckOrHalt_DisabledEntryWithEmptyValue_ReturnsTrue()
		{
			// Disabled entries are an explicit audit-trail state. Empty Value
			// on a disabled entry is fine — operator keeping the Comment
			// around while the entry is dormant.
			var config = ConfigWith(
				Entry("", enabled: false, comment: "TODO: re-decide this"));
			Assert.True(DownloadExclusionsConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_AllDisabled_ReturnsTrue()
		{
			var config = ConfigWith(
				Entry("/forum/", enabled: false),
				Entry("", enabled: false));
			Assert.True(DownloadExclusionsConfigValidator.CheckOrHalt(config));
		}

		// ── Valid entries pass ────────────────────────────────────────────────

		[Fact]
		public void CheckOrHalt_SingleValidEntry_ReturnsTrue()
		{
			var config = ConfigWith(Entry("/forum/"));
			Assert.True(DownloadExclusionsConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_MultipleValidEntries_ReturnsTrue()
		{
			var config = ConfigWith(
				Entry("/forum/"),
				Entry("/admin/"),
				Entry("break.html"));
			Assert.True(DownloadExclusionsConfigValidator.CheckOrHalt(config));
		}

		// ── Invalid entries halt ──────────────────────────────────────────────

		[Fact]
		public void CheckOrHalt_EnabledEntryWithEmptyValue_ReturnsFalse()
		{
			// The actual trap: an enabled entry with empty Value would match
			// every link via Contains("") and silently reject the whole crawl.
			// Halt at startup.
			var config = ConfigWith(Entry("", enabled: true));
			Assert.False(DownloadExclusionsConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_EnabledEntryWithWhitespaceValue_ReturnsFalse()
		{
			// Whitespace-only Value treated the same as empty — Contains("   ")
			// would match many URLs unintentionally, and the operator
			// probably meant to type a real value.
			var config = ConfigWith(Entry("   ", enabled: true));
			Assert.False(DownloadExclusionsConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_MixedValidAndInvalid_ReturnsFalse()
		{
			// Any invalid entry rejects the whole config, even if others are
			// valid. Halt-with-pointed-message lets the operator fix all bad
			// entries in one editing pass.
			var config = ConfigWith(
				Entry("/forum/"),
				Entry("", enabled: true),
				Entry("/admin/"));
			Assert.False(DownloadExclusionsConfigValidator.CheckOrHalt(config));
		}

		[Fact]
		public void CheckOrHalt_InvalidEntryAmongDisabled_ReturnsFalse()
		{
			// Disabled entries don't rescue an enabled invalid entry.
			var config = ConfigWith(
				Entry("/old/", enabled: false),
				Entry("", enabled: true),
				Entry("/other/", enabled: false));
			Assert.False(DownloadExclusionsConfigValidator.CheckOrHalt(config));
		}
	}
}
