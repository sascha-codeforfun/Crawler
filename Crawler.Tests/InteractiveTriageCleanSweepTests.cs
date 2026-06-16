using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Tests for InteractiveTriage.CleanSweepSnapshot and its keep-list predicate
	/// ShouldKeepEntry, introduced in fileset #293.
	///
	/// The contract under test:
	///   - ShouldKeepEntry returns true for "download" (case-insensitive),
	///     "00-crawler.log", and "01-crawler.log" (case-insensitive); false
	///     for everything else.
	///   - CleanSweepSnapshot deletes every entry (file or directory) in the
	///     snapshot folder for which ShouldKeepEntry returns false.
	///   - Subtrees (directories like base64assets/)
	///     get recursively deleted.
	///   - The three ground-truth artifacts (download/, 00-crawler.log,
	///     01-crawler.log) survive.
	///   - Errors per entry are logged and the sweep continues for the rest.
	///
	/// Operator value: enables fast iteration on config changes against a stable
	/// snapshot. The three ground-truth artifacts are sufficient for full replay
	/// re-derivation of everything else; none can be reproduced without
	/// re-crawling (00 carries the per-saved Content-Type the settle phase reads,
	/// 01 is its settled projection, download/ holds the raw bodies + headers).
	/// </summary>
	[Collection("Logger")]
	public class InteractiveTriageCleanSweepTests
	{
		// CleanSweepSnapshot calls Logger.LogInfo/LogWarning, which requires
		// Logger.Initialize to have run. Other test classes use the same pattern
		// (see ConfigValidationTests, DictionaryCheckTests, etc.).
		public InteractiveTriageCleanSweepTests()
		{
			var tempLog = Path.Combine(Path.GetTempPath(), $"sweep-test-logger-{Guid.NewGuid()}.log");
			Logger.Initialize(tempLog, silent: true);
		}

		// ── ShouldKeepEntry predicate ─────────────────────────────────────

		[Theory]
		[InlineData("download")]
		[InlineData("00-crawler.log")]
		[InlineData("01-crawler.log")]
		public void ShouldKeepEntry_KeepsFundamentalArtifacts(string entryName)
		{
			Assert.True(InteractiveTriage.ShouldKeepEntry(entryName));
		}

		[Theory]
		[InlineData("DOWNLOAD")]
		[InlineData("Download")]
		[InlineData("00-CRAWLER.LOG")]
		[InlineData("00-Crawler.Log")]
		[InlineData("01-CRAWLER.LOG")]
		[InlineData("01-Crawler.Log")]
		public void ShouldKeepEntry_IsCaseInsensitive(string entryName)
		{
			Assert.True(InteractiveTriage.ShouldKeepEntry(entryName));
		}

		[Theory]
		[InlineData("02-crawler-index.log")]
		[InlineData("10-content-quality-issues.log")]
		[InlineData("22-cms-template-authoring-defects.log")]
		[InlineData("testfolder")]
		[InlineData("testfolder2")]
		[InlineData("base64assets")]
		[InlineData("sitemap.xml")]
		[InlineData("11-spell-error-sources.log")]
		[InlineData("SpellErrorTicketDraft.log")]
		[InlineData("anything-else.txt")]
		public void ShouldKeepEntry_DeletesEverythingElse(string entryName)
		{
			Assert.False(InteractiveTriage.ShouldKeepEntry(entryName));
		}

		[Fact]
		public void ShouldKeepEntry_DoesNotMatchPartialName()
		{
			// "download" as substring inside a longer name must not be kept;
			// only exact names are protected.
			Assert.False(InteractiveTriage.ShouldKeepEntry("downloads"));
			Assert.False(InteractiveTriage.ShouldKeepEntry("00-crawler.log.bak"));
			Assert.False(InteractiveTriage.ShouldKeepEntry("00-crawler-extra.log"));
			Assert.False(InteractiveTriage.ShouldKeepEntry("01-crawler.log.bak"));
			Assert.False(InteractiveTriage.ShouldKeepEntry("01-crawler-extra.log"));
		}

		// ── CleanSweepSnapshot integration ────────────────────────────────

		private static string CreateTempSnapshotDir()
		{
			var dir = Path.Combine(Path.GetTempPath(), $"sweep-test-{Guid.NewGuid()}");
			Directory.CreateDirectory(dir);
			return dir;
		}

		private static void SetupSnapshotContent(string snapshotDir)
		{
			// Three ground-truth artifacts (must survive sweep)
			Directory.CreateDirectory(Path.Combine(snapshotDir, "download"));
			File.WriteAllText(Path.Combine(snapshotDir, "download", "page1.html"), "<html/>");
			File.WriteAllText(Path.Combine(snapshotDir, "download", "page2.html"), "<html/>");
			File.WriteAllText(Path.Combine(snapshotDir, "00-crawler.log"), "ts|url|saved|file|src|text/html");
			File.WriteAllText(Path.Combine(snapshotDir, "01-crawler.log"), "url|file");

			// Derived artifacts (must be deleted)
			Directory.CreateDirectory(Path.Combine(snapshotDir, "testfolder"));
			File.WriteAllText(Path.Combine(snapshotDir, "testfolder", "page1.html"), "<html/>");
			Directory.CreateDirectory(Path.Combine(snapshotDir, "testfolder2"));
			File.WriteAllText(Path.Combine(snapshotDir, "testfolder2", "page1.html"), "<html/>");
			Directory.CreateDirectory(Path.Combine(snapshotDir, "base64assets"));

			File.WriteAllText(Path.Combine(snapshotDir, "02-crawler-index.log"), "x");
			File.WriteAllText(Path.Combine(snapshotDir, "10-content-quality-issues.log"), "x");
			File.WriteAllText(Path.Combine(snapshotDir, "22-cms-template-authoring-defects.log"), "x");
			File.WriteAllText(Path.Combine(snapshotDir, "sitemap.xml"), "<urlset/>");
			File.WriteAllText(Path.Combine(snapshotDir, "11-spell-error-sources.log"), "x");
		}

		[Fact]
		public void CleanSweepSnapshot_KeepsFundamentalArtifacts()
		{
			var dir = CreateTempSnapshotDir();
			try
			{
				SetupSnapshotContent(dir);

				InteractiveTriage.CleanSweepSnapshot(dir);

				Assert.True(Directory.Exists(Path.Combine(dir, "download")));
				Assert.True(File.Exists(Path.Combine(dir, "download", "page1.html")));
				Assert.True(File.Exists(Path.Combine(dir, "download", "page2.html")));
				Assert.True(File.Exists(Path.Combine(dir, "00-crawler.log")));
				Assert.True(File.Exists(Path.Combine(dir, "01-crawler.log")));
			}
			finally
			{
				if (Directory.Exists(dir))
				{
					Directory.Delete(dir, recursive: true);
				}
			}
		}

		[Fact]
		public void CleanSweepSnapshot_DeletesDerivedSubtrees()
		{
			var dir = CreateTempSnapshotDir();
			try
			{
				SetupSnapshotContent(dir);

				InteractiveTriage.CleanSweepSnapshot(dir);

				Assert.False(Directory.Exists(Path.Combine(dir, "testfolder")));
				Assert.False(Directory.Exists(Path.Combine(dir, "testfolder2")));
				Assert.False(Directory.Exists(Path.Combine(dir, "base64assets")));
			}
			finally
			{
				if (Directory.Exists(dir))
				{
					Directory.Delete(dir, recursive: true);
				}
			}
		}

		[Fact]
		public void CleanSweepSnapshot_DeletesDerivedLogFiles()
		{
			var dir = CreateTempSnapshotDir();
			try
			{
				SetupSnapshotContent(dir);

				InteractiveTriage.CleanSweepSnapshot(dir);

				Assert.False(File.Exists(Path.Combine(dir, "02-crawler-index.log")));
				Assert.False(File.Exists(Path.Combine(dir, "10-content-quality-issues.log")));
				Assert.False(File.Exists(Path.Combine(dir, "22-cms-template-authoring-defects.log")));
				Assert.False(File.Exists(Path.Combine(dir, "sitemap.xml")));
				Assert.False(File.Exists(Path.Combine(dir, "11-spell-error-sources.log")));
			}
			finally
			{
				if (Directory.Exists(dir))
				{
					Directory.Delete(dir, recursive: true);
				}
			}
		}

		[Fact]
		public void CleanSweepSnapshot_EmptySnapshot_NoError()
		{
			var dir = CreateTempSnapshotDir();
			try
			{
				// Just the fundamentals.
				Directory.CreateDirectory(Path.Combine(dir, "download"));
				File.WriteAllText(Path.Combine(dir, "01-crawler.log"), "url|file");

				InteractiveTriage.CleanSweepSnapshot(dir);

				Assert.True(Directory.Exists(Path.Combine(dir, "download")));
				Assert.True(File.Exists(Path.Combine(dir, "01-crawler.log")));
			}
			finally
			{
				if (Directory.Exists(dir))
				{
					Directory.Delete(dir, recursive: true);
				}
			}
		}

		[Fact]
		public void CleanSweepSnapshot_NonexistentPath_DoesNotThrow()
		{
			// Robustness: missing path should be logged as warning but not throw.
			var fakeDir = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid()}");
			InteractiveTriage.CleanSweepSnapshot(fakeDir);
			// No assertion needed; just must not throw.
		}

		[Fact]
		public void CleanSweepSnapshot_OnlyGroundTruth_PreservesAll()
		{
			// Minimum-fundamental case: snapshot has only the three protected
			// entries. Sweep must be a no-op on them.
			var dir = CreateTempSnapshotDir();
			try
			{
				Directory.CreateDirectory(Path.Combine(dir, "download"));
				File.WriteAllText(Path.Combine(dir, "download", "page.html"), "<html/>");
				File.WriteAllText(Path.Combine(dir, "00-crawler.log"), "ts|url|saved|file|src|text/html");
				File.WriteAllText(Path.Combine(dir, "01-crawler.log"), "url|file");

				InteractiveTriage.CleanSweepSnapshot(dir);

				Assert.Equal(3, Directory.GetFileSystemEntries(dir).Length);
				Assert.True(File.Exists(Path.Combine(dir, "download", "page.html")));
			}
			finally
			{
				if (Directory.Exists(dir))
				{
					Directory.Delete(dir, recursive: true);
				}
			}
		}

		[Fact]
		public void CleanSweepSnapshot_HiddenSystemFile_GetsDeleted()
		{
			// Sanity: nothing in the keep-list besides download/ and 01-crawler.log.
			// Anything else — including hidden or odd-named files — must be deleted.
			var dir = CreateTempSnapshotDir();
			try
			{
				Directory.CreateDirectory(Path.Combine(dir, "download"));
				File.WriteAllText(Path.Combine(dir, "01-crawler.log"), "x");
				File.WriteAllText(Path.Combine(dir, ".hidden"), "x");
				File.WriteAllText(Path.Combine(dir, "Thumbs.db"), "x");

				InteractiveTriage.CleanSweepSnapshot(dir);

				Assert.False(File.Exists(Path.Combine(dir, ".hidden")));
				Assert.False(File.Exists(Path.Combine(dir, "Thumbs.db")));
				Assert.True(File.Exists(Path.Combine(dir, "01-crawler.log")));
			}
			finally
			{
				if (Directory.Exists(dir))
				{
					Directory.Delete(dir, recursive: true);
				}
			}
		}

		// ── Containment guarantee ─────────────────────────────────────────
		// Critical: deletions must stay inside the snapshot folder. The sweep
		// must never touch siblings, parents, or anything outside the snapshot
		// path, regardless of file system trickery.

		[Fact]
		public void CleanSweepSnapshot_SiblingDirectory_Untouched()
		{
			// Set up: parent dir with a snapshot and a sibling. Sweep the
			// snapshot. Sibling and its contents must remain.
			var parentDir = Path.Combine(Path.GetTempPath(), $"sweep-parent-{Guid.NewGuid()}");
			Directory.CreateDirectory(parentDir);
			try
			{
				var snapshotDir = Path.Combine(parentDir, "snapshot");
				var siblingDir = Path.Combine(parentDir, "sibling");
				Directory.CreateDirectory(snapshotDir);
				Directory.CreateDirectory(siblingDir);
				SetupSnapshotContent(snapshotDir);
				File.WriteAllText(Path.Combine(siblingDir, "important.dat"), "must-survive");
				Directory.CreateDirectory(Path.Combine(siblingDir, "subtree"));
				File.WriteAllText(Path.Combine(siblingDir, "subtree", "file.txt"), "must-survive");

				InteractiveTriage.CleanSweepSnapshot(snapshotDir);

				Assert.True(Directory.Exists(siblingDir));
				Assert.True(File.Exists(Path.Combine(siblingDir, "important.dat")));
				Assert.True(Directory.Exists(Path.Combine(siblingDir, "subtree")));
				Assert.True(File.Exists(Path.Combine(siblingDir, "subtree", "file.txt")));
				Assert.Equal("must-survive", File.ReadAllText(Path.Combine(siblingDir, "important.dat")));
			}
			finally
			{
				if (Directory.Exists(parentDir))
				{
					Directory.Delete(parentDir, recursive: true);
				}
			}
		}

		[Fact]
		public void CleanSweepSnapshot_ParentDirectory_Untouched()
		{
			// Set up: snapshot inside parent. Sweep the snapshot. The parent
			// dir itself must remain (we don't delete the snapshot folder
			// itself either — only its contents).
			var parentDir = Path.Combine(Path.GetTempPath(), $"sweep-parent2-{Guid.NewGuid()}");
			Directory.CreateDirectory(parentDir);
			try
			{
				var snapshotDir = Path.Combine(parentDir, "snapshot");
				Directory.CreateDirectory(snapshotDir);
				SetupSnapshotContent(snapshotDir);
				File.WriteAllText(Path.Combine(parentDir, "parent-marker.txt"), "must-survive");

				InteractiveTriage.CleanSweepSnapshot(snapshotDir);

				Assert.True(Directory.Exists(parentDir));
				Assert.True(File.Exists(Path.Combine(parentDir, "parent-marker.txt")));
				Assert.True(Directory.Exists(snapshotDir),
					"snapshot folder itself must remain; only its contents get filtered");
			}
			finally
			{
				if (Directory.Exists(parentDir))
				{
					Directory.Delete(parentDir, recursive: true);
				}
			}
		}

		[Fact]
		public void CleanSweepSnapshot_SymlinkToOutside_NotFollowed()
		{
			// Critical safety test: if a symlink/junction exists inside the
			// snapshot and points OUT of it, the sweep must NOT follow it.
			// We create the link, run sweep, then verify the external target
			// is untouched. Skips silently on platforms/permissions where
			// symlink creation is not supported (Windows non-dev/non-admin).
			var parentDir = Path.Combine(Path.GetTempPath(), $"sweep-symlink-{Guid.NewGuid()}");
			Directory.CreateDirectory(parentDir);
			try
			{
				var snapshotDir = Path.Combine(parentDir, "snapshot");
				var outsideDir = Path.Combine(parentDir, "outside");
				Directory.CreateDirectory(snapshotDir);
				Directory.CreateDirectory(outsideDir);
				File.WriteAllText(Path.Combine(outsideDir, "valuable.dat"), "must-survive");
				Directory.CreateDirectory(Path.Combine(snapshotDir, "download"));
				File.WriteAllText(Path.Combine(snapshotDir, "01-crawler.log"), "x");

				var linkPath = Path.Combine(snapshotDir, "escape-link");
				try
				{
					// .NET 8 cross-platform symlink creation.
					Directory.CreateSymbolicLink(linkPath, outsideDir);
				}
				catch (Exception)
				{
					// Symlink creation requires elevated permissions on some
					// systems (Windows without Developer Mode / admin). If we
					// can't create the link, skip the test rather than fail.
					return;
				}

				InteractiveTriage.CleanSweepSnapshot(snapshotDir);

				// The external target must be intact regardless of whether the
				// link itself was deleted.
				Assert.True(Directory.Exists(outsideDir),
					"External directory targeted by the symlink must not be deleted by the sweep");
				Assert.True(File.Exists(Path.Combine(outsideDir, "valuable.dat")),
					"Files in the external directory must not be deleted");
				Assert.Equal("must-survive",
					File.ReadAllText(Path.Combine(outsideDir, "valuable.dat")));
			}
			finally
			{
				if (Directory.Exists(parentDir))
				{
					try { Directory.Delete(parentDir, recursive: true); }
					catch { /* cleanup best-effort */ }
				}
			}
		}
	}
}
