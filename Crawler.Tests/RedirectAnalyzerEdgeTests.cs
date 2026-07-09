using System.Text;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Edge-branch tests for RedirectAnalyzer.AnalyzeRedirects, complementing
	/// RedirectAnalyzerTests (which covers the main loop / not-found / chain /
	/// depth / ignored / malformed paths with clean absolute URLs). These target
	/// the sub-branches those don't reach:
	///   • IsRedirectStatus / IsIgnoredVariableInfo on empty input, and the
	///     empty-variable-info ExtractRedirectUri guard;
	///   • ExtractRedirectUri's non-http "://" scheme path and its return-whole-
	///     value fallback;
	///   • NormalizeUrl's relative (non-absolute) fallback, with and without a
	///     query/fragment;
	///   • the two chain-break conditions (self-redirect, empty next target).
	///
	/// SYNTHETIC log fixtures (TIMESTAMP | URL | STATUS | VARIABLEINFO). In the
	/// Logger collection: FileIo writes via the static Logger.
	/// </summary>
	[Collection("Logger")]
	public class RedirectAnalyzerEdgeTests : IDisposable
	{
		private readonly string _dir;

		public RedirectAnalyzerEdgeTests()
		{
			_dir = Path.Combine(Path.GetTempPath(), $"redirect-edge-{Guid.NewGuid():N}");
			Directory.CreateDirectory(_dir);
			Logger.Initialize(Path.Combine(_dir, "test.log"), silent: true);
		}

		public void Dispose()
		{
			try { Directory.Delete(_dir, recursive: true); } catch { }
			GC.SuppressFinalize(this);
		}

		private string[] RunAndRead(params string[] inputLines)
		{
			var logPath = Path.Combine(_dir, $"in-{Guid.NewGuid():N}.log");
			var outPath = Path.Combine(_dir, $"out-{Guid.NewGuid():N}.log");
			File.WriteAllLines(logPath, inputLines, Encoding.UTF8);
			RedirectAnalyzer.AnalyzeRedirects(logPath, outPath);
			return File.Exists(outPath) ? File.ReadAllLines(outPath, Encoding.UTF8) : Array.Empty<string>();
		}

		[Fact]
		public void EmptyStatusAndEmptyVariableInfo_ProduceNoOutput()
		{
			var output = RunAndRead(
				"t | https://x.com/a |  | https://x.com/b", // empty status → not a redirect
				"t | https://x.com/c | Found | ");          // redirect status but no target URI

			Assert.Empty(output);
		}

		[Fact]
		public void NonHttpSchemeTarget_ExtractedViaSchemeColon()
		{
			var output = RunAndRead("t | https://x.com/a | Found | ftp://files.x.com/doc");

			var line = Assert.Single(output);
			Assert.Contains("ftp://files.x.com/doc", line); // matched on "://", not http/https prefix
			Assert.Contains("(NotFound)", line);
		}

		[Fact]
		public void NonUrlVariableInfo_ReturnedAsWholeValue()
		{
			// No URL-like token → ExtractRedirectUri returns the trimmed whole value;
			// NormalizeUrl takes its relative fallback (no query/fragment).
			var output = RunAndRead("t | https://x.com/a | Found | see other page");

			var line = Assert.Single(output);
			Assert.Contains("see other page", line);
			Assert.Contains("(NotFound)", line);
		}

		[Fact]
		public void RelativeTargetWithQuery_NormalizedToPath()
		{
			// Both URLs are relative → NormalizeUrl's non-absolute fallback; the
			// target carries a query so the qIdx>=0 path-only branch is exercised.
			var output = RunAndRead("t | /old/path | Found | /new/path?ref=1");

			var line = Assert.Single(output);
			Assert.Contains("/new/path?ref=1", line); // redirect target shown as-is
			Assert.Contains("(NotFound)", line);
		}

		[Fact]
		public void SelfRedirect_BreaksChain()
		{
			// a → a?session=1 : the query is stripped on normalize, so the next hop
			// equals the current one and the follow loop stops.
			var output = RunAndRead("t | https://x.com/a | Found | https://x.com/a?session=1");

			var line = Assert.Single(output);
			Assert.Contains("https://x.com/a", line);
		}

		[Fact]
		public void RedirectToRedirectWithNoTarget_BreaksChain()
		{
			// a → b, b is itself a redirect status but carries no target → the chain
			// stops on the empty next-URI rather than looping or erroring.
			var output = RunAndRead(
				"t | https://x.com/a | Found | https://x.com/b",
				"t | https://x.com/b | Found | ");

			var line = Assert.Single(output);
			Assert.Contains("https://x.com/b", line);
		}
	}
}
