using Xunit;
using Crawler.Lexicon;

namespace Crawler.Tests.Lexicon
{
	/// <summary>
	/// Locks Signpost.EmitIfUnconfigured behaviour after its move to
	/// Lexicon (#D013): the configured-bundles no-op (Integrity owns that
	/// case), the unconfigured folder+readme creation, and the never-overwrite guard
	/// that protects an operator's edited readme. EmitIfUnconfigured writes under
	/// AppContext.BaseDirectory, so these snapshot and restore that path on dispose.
	/// </summary>
	[Collection("Logger")]
	public class SignpostTests : IDisposable
	{
		private readonly string _folder;
		private readonly string _readmePath;
		private readonly bool _folderPreexisted;
		private readonly bool _readmePreexisted;
		private readonly string? _readmeOriginal;

		public SignpostTests()
		{
			_folder = Path.Combine(AppContext.BaseDirectory, "dictionaries");
			_readmePath = Path.Combine(_folder, "readme.txt");
			_folderPreexisted = Directory.Exists(_folder);
			_readmePreexisted = File.Exists(_readmePath);
			_readmeOriginal = _readmePreexisted ? File.ReadAllText(_readmePath) : null;
		}

		public void Dispose()
		{
			// Restore the pre-test state of the shared dictionaries\readme.txt path.
			if (_readmePreexisted)
			{
				File.WriteAllText(_readmePath, _readmeOriginal!);
			}
			else if (File.Exists(_readmePath))
			{
				File.Delete(_readmePath);
			}

			if (!_folderPreexisted && Directory.Exists(_folder)
				&& !Directory.EnumerateFileSystemEntries(_folder).Any())
			{
				Directory.Delete(_folder);
			}
		}

		private static Config Configured() =>
			new() { DictionaryBundles = { new DictionaryBundleConfig() } };

		private static Config Unconfigured() => new();   // DictionaryBundles defaults to []

		[Fact]
		public void EmitIfUnconfigured_BundlesConfigured_StaysSilent()
		{
			if (File.Exists(_readmePath))
			{
				File.Delete(_readmePath);
			}

			Signpost.EmitIfUnconfigured(Configured());

			// Configured bundles -> Integrity owns it; the signpost writes nothing.
			Assert.False(File.Exists(_readmePath));
		}

		[Fact]
		public void EmitIfUnconfigured_NoBundles_CreatesFolderAndReadme()
		{
			if (File.Exists(_readmePath))
			{
				File.Delete(_readmePath);
			}

			Signpost.EmitIfUnconfigured(Unconfigured());

			Assert.True(File.Exists(_readmePath));
			Assert.StartsWith("DICTIONARIES", File.ReadAllText(_readmePath));
		}

		[Fact]
		public void EmitIfUnconfigured_ExistingReadme_NotOverwritten()
		{
			Directory.CreateDirectory(_folder);
			const string sentinel = "OPERATOR EDITED - DO NOT TOUCH";
			File.WriteAllText(_readmePath, sentinel);

			Signpost.EmitIfUnconfigured(Unconfigured());

			// An existing readme is the operator's; it must be left untouched.
			Assert.Equal(sentinel, File.ReadAllText(_readmePath));
		}
	}
}
