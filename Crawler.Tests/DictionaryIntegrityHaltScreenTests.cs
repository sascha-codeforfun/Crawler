using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// 652 — pins the by-bundle CONFIG CHECK shape. The halt is rendered per bundle in stable config
	/// order (no severity sort, so findings don't wander between fix-and-reload passes); each bundle
	/// shows its whole situation in one place with colour by severity — missing file / wrong checksum
	/// are errors (red), a missing DisplayName / unset checksum are setup-incomplete (amber), the
	/// on-disk value is neutral (dim). DisplayName is required. Fixtures are generic.
	/// </summary>
	public class DictionaryIntegrityHaltScreenTests
	{
		private static DictionaryIntegrity.FieldResult R(
			string lang, string field, DictionaryIntegrity.FieldStatus status,
			string expected = "", string actual = "", string path = "dictionaries/x.dic")
			=> new(lang, field, path, field == "DicFile" ? "DicChecksum" : "AffChecksum",
				status, expected, actual);

		private static DictionaryBundleConfig B(string lang, string display)
			=> new() { LanguageCode = lang, DisplayName = display };

		private static List<ConsoleUi.CheckLine> AllLines(List<ConsoleUi.CheckBlock> blocks)
			=> blocks.SelectMany(b => b.Lines).ToList();

		[Fact]
		public void Mismatch_NamedBundle_ConfiguredIsRed_FileOnDiskIsNeutral_HeaderShowsName()
		{
			var blocks = DictionaryIntegrity.BuildHaltBlocks(
				new List<DictionaryBundleConfig> { B("de", "German") },
				new List<DictionaryIntegrity.FieldResult>
				{
					R("de", "DicFile", DictionaryIntegrity.FieldStatus.Mismatch, "OLDHASH", "NEWHASH"),
					R("de", "AffFile", DictionaryIntegrity.FieldStatus.Pass),
				});

			Assert.Contains(blocks, b => b.Label is "Problem" or "Why" or "Fix");

			var bundle = blocks.Single(b => b.HeadingOnOwnLine);
			Assert.Equal("German (de)", bundle.Label);

			var configured = bundle.Lines.Single(l => l.SubLabel == "configured");
			Assert.Equal(ConsoleUi.CheckTone.Error, configured.Tone);   // present but wrong -> red
			Assert.Equal("OLDHASH", configured.Text);

			var onDisk = bundle.Lines.Single(l => l.SubLabel == "file on disk");
			Assert.Equal(ConsoleUi.CheckTone.Data, onDisk.Tone);        // neutral, presented not emphasised
			Assert.Equal("NEWHASH", onDisk.Text);

			// The investigate guidance stays amber; nothing reproduces a paste line.
			Assert.Equal(ConsoleUi.CheckTone.Accent,
				AllLines(blocks).Single(l => l.Text.Contains("investigate")).Tone);
			Assert.DoesNotContain(AllLines(blocks), l => l.SubLabel == "paste" || l.Text.StartsWith("paste"));
		}

		[Fact]
		public void MissingChecksum_ConfiguredIsAmber_NotRed()
		{
			var blocks = DictionaryIntegrity.BuildHaltBlocks(
				new List<DictionaryBundleConfig> { B("en", "English") },
				new List<DictionaryIntegrity.FieldResult>
				{
					R("en", "DicFile", DictionaryIntegrity.FieldStatus.Pass),
					R("en", "AffFile", DictionaryIntegrity.FieldStatus.MissingChecksum, "", "FRESHHASH"),
				});

			var configured = AllLines(blocks).Single(l => l.SubLabel == "configured");
			Assert.Equal(ConsoleUi.CheckTone.Accent, configured.Tone);  // not set yet -> amber, not red
			Assert.Equal("MISSING", configured.Text);
		}

		[Fact]
		public void MissingFile_IsRed_WithPath_AndLeadsTheBundle()
		{
			var blocks = DictionaryIntegrity.BuildHaltBlocks(
				new List<DictionaryBundleConfig> { B("pt", "Portuguese") },
				new List<DictionaryIntegrity.FieldResult>
				{
					R("pt", "DicFile", DictionaryIntegrity.FieldStatus.MissingFile, path: "dictionaries/pt_PT.dicNope"),
					R("pt", "AffFile", DictionaryIntegrity.FieldStatus.Pass),
				});

			var bundle = blocks.Single(b => b.HeadingOnOwnLine);
			var notFound = bundle.Lines.Single(l => l.Text == "file not found");
			Assert.Equal(ConsoleUi.CheckTone.Error, notFound.Tone);
			Assert.Equal("DicFile", notFound.SubLabel);
			Assert.Contains(bundle.Lines, l => l.SubLabel == "path" && l.Text.Contains("pt_PT.dicNope"));
		}

		[Fact]
		public void MissingDisplayName_HaltsTheBundle_HeaderHasNoName_NameLineIsAmber()
		{
			// Files are perfectly fine; only the name is absent — the bundle still surfaces.
			var blocks = DictionaryIntegrity.BuildHaltBlocks(
				new List<DictionaryBundleConfig> { B("sk", "") },
				new List<DictionaryIntegrity.FieldResult>
				{
					R("sk", "DicFile", DictionaryIntegrity.FieldStatus.Pass),
					R("sk", "AffFile", DictionaryIntegrity.FieldStatus.Pass),
				});

			var bundle = blocks.Single(b => b.HeadingOnOwnLine);
			Assert.Equal("(sk)", bundle.Label);   // no DisplayName -> code only
			var name = bundle.Lines.Single(l => l.SubLabel == "name");
			Assert.Equal(ConsoleUi.CheckTone.Accent, name.Tone);
		}

		[Fact]
		public void CleanNamedBundle_ProducesNoBundleBlock_AndOrderIsStable()
		{
			// el is clean and named -> no block; de has a finding -> one block. Order follows config.
			var blocks = DictionaryIntegrity.BuildHaltBlocks(
				new List<DictionaryBundleConfig> { B("el", "Greek"), B("de", "German") },
				new List<DictionaryIntegrity.FieldResult>
				{
					R("el", "DicFile", DictionaryIntegrity.FieldStatus.Pass),
					R("el", "AffFile", DictionaryIntegrity.FieldStatus.Pass),
					R("de", "DicFile", DictionaryIntegrity.FieldStatus.Mismatch, "OLD", "NEW"),
					R("de", "AffFile", DictionaryIntegrity.FieldStatus.Pass),
				});

			var bundleBlocks = blocks.Where(b => b.HeadingOnOwnLine).ToList();
			Assert.Single(bundleBlocks);
			Assert.Equal("German (de)", bundleBlocks[0].Label);
		}
	}
}
