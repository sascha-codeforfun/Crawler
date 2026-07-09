using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Edge-branch tests for ConfigDeltaWriter, complementing ConfigDeltaWriterTests
	/// (which covers the diff, comment injection, and the main Write happy paths).
	/// These target Write's guard/catch arms (baseline missing, non-object root,
	/// malformed JSON) and the ExtractPropertyName / IsAtEmittedDepth arms the
	/// space-indented happy-path inputs don't reach.
	///
	/// SYNTHETIC fixtures. In the Logger collection: Write logs via the static Logger.
	/// </summary>
	[Collection("Logger")]
	public class ConfigDeltaWriterEdgeTests : IDisposable
	{
		private readonly string _dir;

		public ConfigDeltaWriterEdgeTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"cfgdelta-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		private string WriteFile(string name, string content)
		{
			var path = Path.Combine(_dir, name);
			File.WriteAllText(path, content, Encoding.UTF8);
			return path;
		}

		private string DeltaPath() => Path.Combine(_dir, "config.private.delta");

		// ── Write guards ────────────────────────────────────────────────────

		[Fact]
		public void Write_BaselineMissing_SkipsWithoutDelta()
		{
			var privatePath = WriteFile("config.private.json", "{}");
			var missingBase = Path.Combine(_dir, "config.json"); // not created

			ConfigDeltaWriter.Write(missingBase, privatePath);

			Assert.False(File.Exists(DeltaPath()));
		}

		[Fact]
		public void Write_PrivateRootNotObject_SkipsWithoutDelta()
		{
			var basePath = WriteFile("config.json", "{}");
			var privatePath = WriteFile("config.private.json", "[1, 2, 3]"); // array root

			ConfigDeltaWriter.Write(basePath, privatePath);

			Assert.False(File.Exists(DeltaPath()));
		}

		[Fact]
		public void Write_MalformedPrivateJson_SwallowsException()
		{
			var basePath = WriteFile("config.json", "{}");
			var privatePath = WriteFile("config.private.json", "{ this is not valid json");

			var ex = Record.Exception(() => ConfigDeltaWriter.Write(basePath, privatePath));

			Assert.Null(ex); // failure is logged and swallowed
			Assert.False(File.Exists(DeltaPath()));
		}

		// ── ExtractPropertyName ─────────────────────────────────────────────

		[Fact]
		public void ExtractPropertyName_NoClosingQuote_ReturnsNull()
		{
			Assert.Null(ConfigDeltaWriter.ExtractPropertyName("  \"unterminated"));
		}

		[Fact]
		public void ExtractPropertyName_NotFollowedByColon_ReturnsNull()
		{
			Assert.Null(ConfigDeltaWriter.ExtractPropertyName("  \"name\" value"));
		}

		// ── IsAtEmittedDepth ────────────────────────────────────────────────

		[Fact]
		public void IsAtEmittedDepth_TabIndented_ComparesTabColumns()
		{
			// A tab-indented source line: one tab == emitted depth 1.
			Assert.True(ConfigDeltaWriter.IsAtEmittedDepth("\t\"X\": 1", 1));
		}
	}
}
