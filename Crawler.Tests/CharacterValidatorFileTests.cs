using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for CharacterValidator.ValidateListWarn and ValidateDictionaryFileHalt,
	/// which the existing CharacterValidatorTests don't cover. ValidateListWarn logs
	/// a warning per suspicious character and continues; ValidateDictionaryFileHalt
	/// reads a dictionary file (skipping blanks, '/'-bearing lines, and the '!' pin
	/// marker), and throws if any suspicious character remains.
	///
	/// U+200B (zero-width space) is the suspicious-character fixture — unambiguously
	/// non-printable and outside the Latin allow-list. silent:true skips the console
	/// halt block. SYNTHETIC fixtures; in the Logger collection (Warn/LogError).
	/// </summary>
	[Collection("Logger")]
	public class CharacterValidatorFileTests : IDisposable
	{
		private const char Zwsp = '\u200B';

		private readonly string _dir;

		public CharacterValidatorFileTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"charval-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		private string WriteDict(params string[] lines)
		{
			var path = Path.Combine(_dir, $"dict-{Guid.NewGuid():N}.txt");
			File.WriteAllLines(path, lines, Encoding.UTF8);
			return path;
		}

		[Fact]
		public void ValidateListWarn_WithSuspiciousValue_WarnsWithoutThrowing()
		{
			var ex = Record.Exception(() =>
				CharacterValidator.ValidateListWarn("Prefixes", new[] { "clean", $"a{Zwsp}b" }));

			Assert.Null(ex);
		}

		[Fact]
		public void ValidateDictionaryFileHalt_MissingFile_ReturnsWithoutThrowing()
		{
			var missing = Path.Combine(_dir, "does-not-exist.txt");

			var ex = Record.Exception(() =>
				CharacterValidator.ValidateDictionaryFileHalt(missing, silent: true));

			Assert.Null(ex);
		}

		[Fact]
		public void ValidateDictionaryFileHalt_CleanFile_DoesNotThrow()
		{
			// Blank line, a '/'-bearing affix line, and a '!'-pinned word are all
			// skipped or stripped; the plain words carry no suspicious characters.
			var path = WriteDict("apple", "", "foo/bar", "!pinnedword", "banana");

			var ex = Record.Exception(() =>
				CharacterValidator.ValidateDictionaryFileHalt(path, silent: true));

			Assert.Null(ex);
		}

		[Fact]
		public void ValidateDictionaryFileHalt_SuspiciousChar_Throws()
		{
			var path = WriteDict("good", $"ba{Zwsp}d", "fine");

			Assert.Throws<InvalidOperationException>(() =>
				CharacterValidator.ValidateDictionaryFileHalt(path, silent: true));
		}
	}
}
