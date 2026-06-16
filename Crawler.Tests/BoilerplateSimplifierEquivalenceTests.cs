using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Crawler.Boilerplate;
using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Pins the contract BoilerplateSimplifier documents but only asserts: the ON-DISK prune
	/// (raw → boilerplate nodes removed → re-serialised pruned file, the substrate consumers
	/// read) yields the SAME harvested text runs as the IN-MEMORY prune the spell engine performs
	/// (raw walked with the BoilerplateMatcher skipping boilerplate subtrees). Both paths resolve
	/// governance through the same BoilerplateResolver and the same BoilerplateGroups, but the
	/// removal itself is implemented twice — a class token is set-membership-matched in the matcher
	/// yet generated into an xpath idiom for the on-disk pruner — so the equivalence is asserted,
	/// not structural. This test is the runnable proof of that assertion and the regression gate
	/// if either side is edited without the other.
	///
	/// Equivalence is measured on the harvested run set (Source, SourcePath, RawText). Every other
	/// traversal filter (attribute-skip, meta allowlist, global-ignore, tags-to-ignore) is left at
	/// its default on BOTH sides, so the ONLY variable is boilerplate handling. The in-memory side
	/// walks the raw doc WITH the page matcher; the on-disk side walks the already-pruned file with
	/// NO matcher (the boilerplate is physically gone). Fixtures are ASCII so the shared encoding
	/// detector decodes identically on both paths and cannot confound the text comparison.
	///
	/// NOT covered here (deliberately): a class token containing an xpath-special character (e.g. a
	/// single quote). The on-disk pruner concatenates the token UNESCAPED into a quote-delimited
	/// xpath literal, while the matcher compares the literal token; that is the one surface where
	/// the two paths can diverge and it is left unproven pending a separate decision.
	/// </summary>
	public class BoilerplateSimplifierEquivalenceTests
	{
		private const string ContentMarker = "REALCONTENT";
		private const string BoilerMarker = "BOILERPLATE";

		[Fact]
		public void ClassSelector_SingleToken_PrunesIdentically()
		{
			string html = Page(
				$"<div class=\"nav_footer\"><p>{BoilerMarker}</p></div>" +
				$"<p>{ContentMarker}</p>");

			var group = Group("/", Selector("class", "nav_footer"));

			AssertPrunePathsEquivalent(html, group, "/page", boilerplateRemoved: true);
		}

		[Fact]
		public void ClassSelector_MultiToken_AndSubset_PrunesIdentically()
		{
			// Selector requires BOTH tokens. The two-token element is boilerplate; the
			// single-token decoy is NOT (subset semantics) and must survive on both paths.
			string html = Page(
				$"<div class=\"block_outer nav_footer\"><p>{BoilerMarker}</p></div>" +
				$"<div class=\"nav_footer\"><p>{ContentMarker}</p></div>");

			var group = Group("/", Selector("class", "block_outer nav_footer"));

			AssertPrunePathsEquivalent(html, group, "/page", boilerplateRemoved: true);
		}

		[Fact]
		public void ClassSelector_WholeToken_NotSubstring_PrunesIdentically()
		{
			// "nav" matches the whole-token class "nav" (pruned) but NOT the substring class
			// "navbar" (kept) — and both paths agree on that distinction. The exact-token element
			// is the positive control; the substring element is the negative control that must
			// survive carrying the real content.
			string html = Page(
				$"<div class=\"nav\"><p>{BoilerMarker}</p></div>" +
				$"<div class=\"navbar\"><p>{ContentMarker}</p></div>");

			var group = Group("/", Selector("class", "nav"));

			AssertPrunePathsEquivalent(html, group, "/page", boilerplateRemoved: true);
		}

		[Fact]
		public void XPathSelector_PrunesIdentically()
		{
			string html = Page(
				$"<div id=\"footer\"><p>{BoilerMarker}</p></div>" +
				$"<p>{ContentMarker}</p>");

			var group = Group("/", Selector("xpath", "//div[@id='footer']"));

			AssertPrunePathsEquivalent(html, group, "/page", boilerplateRemoved: true);
		}

		[Fact]
		public void CheckPage_KeepsBoilerplate_Identically()
		{
			// On a check page nothing is pruned: the matcher path passes isEntryPage=true and the
			// on-disk path emits an empty removal list, so both KEEP the boilerplate.
			string html = Page(
				$"<div class=\"nav_footer\"><p>{BoilerMarker}</p></div>" +
				$"<p>{ContentMarker}</p>");

			var group = Group("/", new[] { "/check" }, Selector("class", "nav_footer"));

			AssertPrunePathsEquivalent(html, group, "/check", boilerplateRemoved: false);
		}

		[Fact]
		public void NoGoverningGroup_ChecksEverything_Identically()
		{
			// URL outside every PathPrefix and not a check page → no governing group → fail-loud
			// default: nothing pruned on either path (matcher null vs empty removal list).
			string html = Page(
				$"<div class=\"nav_footer\"><p>{BoilerMarker}</p></div>" +
				$"<p>{ContentMarker}</p>");

			var group = Group("/governed", Selector("class", "nav_footer"));

			AssertPrunePathsEquivalent(html, group, "/elsewhere", boilerplateRemoved: false);
		}

		// ── helpers ──────────────────────────────────────────────────────────

		// Fixtures declare utf-8 so DetectEncoding.FromBytes resolves via the meta-charset branch
		// (built-in encoding, no codepage provider needed) instead of its Windows-1252 fallback,
		// which throws in a host that has not registered CodePagesEncodingProvider. The meta
		// survives the on-disk re-serialise, so both prune paths decode identically; a bare charset
		// meta carries no name/content and no text, so it adds no harvested run.
		private static string Page(string bodyInner)
			=> $"<html><head><meta charset=\"utf-8\"></head><body>{bodyInner}</body></html>";

		private static BoilerplateSelectorConfig Selector(string type, string value)
			=> new() { Type = type, Value = value };

		private static BoilerplateGroupConfig Group(string pathPrefix, params BoilerplateSelectorConfig[] selectors)
			=> new()
			{
				PathPrefix = pathPrefix,
				PagesToCheckBoiler = new List<string>(),
				BoilerplateSelectors = selectors.ToList(),
			};

		private static BoilerplateGroupConfig Group(string pathPrefix, IEnumerable<string> checkPages, params BoilerplateSelectorConfig[] selectors)
			=> new()
			{
				PathPrefix = pathPrefix,
				PagesToCheckBoiler = checkPages.ToList(),
				BoilerplateSelectors = selectors.ToList(),
			};

		/// <summary>
		/// Runs both prune paths for one fixture and asserts the harvested run sets match. Also
		/// asserts the boilerplate marker is actually absent (when removal is expected) or present
		/// (when kept) on BOTH paths, so an equal-but-no-op pass cannot masquerade as success.
		/// </summary>
		private static void AssertPrunePathsEquivalent(
			string html, BoilerplateGroupConfig group, string url, bool boilerplateRemoved)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(html);
			var resolver = new BoilerplateResolver(new List<BoilerplateGroupConfig> { group });

			// In-memory path: walk the raw doc with the page matcher (engine behaviour).
			var (matcher, isCheckPage) = resolver.Resolve(url);
			var inMemoryRuns = Project(
				DomTraverser.Traverse(DomTraverser.Parse(bytes), matcher, isCheckPage));

			// On-disk path: prune to a pruned file, then walk it with NO matcher.
			var onDiskRuns = Project(RunsFromOnDiskPrune(bytes, resolver, url));

			Assert.Equal(inMemoryRuns, onDiskRuns);

			bool boilerInMemory = inMemoryRuns.Any(r => r.Text.Contains(BoilerMarker));
			bool boilerOnDisk = onDiskRuns.Any(r => r.Text.Contains(BoilerMarker));
			Assert.Equal(boilerInMemory, boilerOnDisk);
			Assert.Equal(!boilerplateRemoved, boilerInMemory);

			// Sanity: the real content always survives on both paths.
			Assert.Contains(inMemoryRuns, r => r.Text.Contains(ContentMarker));
			Assert.Contains(onDiskRuns, r => r.Text.Contains(ContentMarker));
		}

		private static IEnumerable<TextRun> RunsFromOnDiskPrune(
			byte[] bytes, BoilerplateResolver resolver, string url)
		{
			string root = Path.Combine(Path.GetTempPath(), "boilerplate_eq_" + Guid.NewGuid().ToString("N"));
			string srcDir = Path.Combine(root, "download");
			string dstDir = Path.Combine(root, "pruned");
			Directory.CreateDirectory(srcDir);

			const string fileName = "page.html";
			File.WriteAllBytes(Path.Combine(srcDir, fileName), bytes);

			try
			{
				BoilerplateSimplifier.Run(
					srcDir,
					dstDir,
					resolver,
					_ => url,
					"*.html",
					1);

				byte[] prunedBytes = File.ReadAllBytes(Path.Combine(dstDir, fileName));

				// Materialise before the temp tree is deleted (Traverse is lazy).
				return DomTraverser.Traverse(DomTraverser.Parse(prunedBytes)).ToList();
			}
			finally
			{
				try
				{
					Directory.Delete(root, recursive: true);
				}
				catch
				{
					// Temp cleanup is best-effort; a leftover temp dir must never fail the test.
				}
			}
		}

		private static List<(RunSource Source, string Path, string Text)> Project(IEnumerable<TextRun> runs)
			=> runs.Select(r => (r.Source, r.SourcePath, r.RawText)).ToList();
	}
}
