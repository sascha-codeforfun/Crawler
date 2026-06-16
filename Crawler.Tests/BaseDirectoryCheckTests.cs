using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Unit tests for <see cref="BaseDirectoryCheck.IsRootReachable"/>, the pure
	/// reachability predicate behind the startup working-folder halt. The predicate
	/// is the testable core; the surrounding CheckOrHalt only adds logging and the
	/// console screen, which are environment side-effects not asserted here.
	/// </summary>
	public class BaseDirectoryCheckTests
	{
		[Fact]
		public void IsRootReachable_FalseWhenDriveRootMissing()
		{
			// Drive-letter roots are a Windows concept; on other platforms there is no
			// equivalent "missing root" (everything roots at "/"), so the case does not apply.
			if (!OperatingSystem.IsWindows())
			{
				return;
			}

			// Pick a drive letter not present on this machine, so the root is genuinely
			// unreachable (the "X: with no X: drive" case from the field).
			var used = System.IO.DriveInfo.GetDrives()
				.Select(d => char.ToUpperInvariant(d.Name[0]))
				.ToHashSet();
			char? freeLetter = null;
			for (char c = 'Z'; c >= 'D'; c--)
			{
				if (!used.Contains(c))
				{
					freeLetter = c;
					break;
				}
			}
			Assert.True(freeLetter.HasValue, "No unused drive letter available to run this test.");

			Assert.False(BaseDirectoryCheck.IsRootReachable($"{freeLetter!.Value}:\\YOURCRAWLERFOLDERNAME"));
		}

		[Fact]
		public void IsRootReachable_TrueWhenFolderMissingButRootExists()
		{
			// A missing folder on an existing root is reachable — it is auto-created
			// downstream. GetTempPath() sits on a present root.
			var missingFolder = System.IO.Path.Combine(
				System.IO.Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}");

			Assert.True(BaseDirectoryCheck.IsRootReachable(missingFolder));
		}

		[Fact]
		public void IsRootReachable_TrueForRelativePath()
		{
			// A relative BaseDirectory roots under the program directory (always present).
			Assert.True(BaseDirectoryCheck.IsRootReachable("crawl-output"));
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("   ")]
		public void IsRootReachable_TrueWhenNullOrEmpty(string? baseDirectory)
		{
			// Emptiness is Config.ValidateConfig's concern, not this check's.
			Assert.True(BaseDirectoryCheck.IsRootReachable(baseDirectory));
		}
	}
}
