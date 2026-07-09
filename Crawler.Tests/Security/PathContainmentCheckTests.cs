using Xunit;
using Crawler.Security;

namespace Crawler.Tests.Security
{
	/// <summary>
	/// Tests for PathContainmentCheck — the pure write-surface containment guard.
	/// No Logger and no shared state: the primitive is a deterministic function, so
	/// these run fully parallel and assert the Verdict directly. Inputs are treated
	/// as attacker-controlled; the assertions are our invariants (contained under
	/// root, or refused), never "does it trust the name".
	/// </summary>
	public class PathContainmentCheckTests
	{
		private static string Root() =>
			Path.Combine(Path.GetTempPath(), $"cap-{Guid.NewGuid():N}", "download");

		private static string RootFull(string root) =>
			Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

		[Fact]
		public void Resolve_PlainHashName_Contained()
		{
			var root = Root();
			var v = PathContainmentCheck.Resolve(root, "ab12cd34.unverified");
			Assert.True(v.Safe);
			Assert.Equal("contained", v.Reason);
			Assert.StartsWith(RootFull(root), v.FullPath);
		}

		[Fact]
		public void Resolve_NestedRelativeName_StaysContained()
		{
			var root = Root();
			var v = PathContainmentCheck.Resolve(root, "sub/page.unverified");
			Assert.True(v.Safe);
			Assert.StartsWith(RootFull(root), v.FullPath);
		}

		[Fact]
		public void Resolve_InnerDotSegments_NormaliseAndStayContained()
		{
			var root = Root();
			// a/../b resolves to <root>/b — the climb is cancelled inside the root.
			var v = PathContainmentCheck.Resolve(root, "a/../b.unverified");
			Assert.True(v.Safe);
			Assert.StartsWith(RootFull(root), v.FullPath);
		}

		[Fact]
		public void Resolve_ClimbOutTraversal_Escapes()
		{
			var root = Root();
			var v = PathContainmentCheck.Resolve(root, "../../../../../../etc/passwd");
			Assert.False(v.Safe);
			Assert.Equal("outside-root", v.Reason);
			Assert.Equal(string.Empty, v.FullPath);
		}

		[Fact]
		public void Resolve_RootedCandidate_Escapes()
		{
			var root = Root();
			// A leading separator is rooted on both Windows (drive-relative) and
			// Unix (absolute); Path.Combine yields it verbatim and GetFullPath
			// resolves it away from the capture root — the query-bearing-asset bug.
			var rooted = Path.DirectorySeparatorChar + "etc" + Path.DirectorySeparatorChar + "passwd";
			var v = PathContainmentCheck.Resolve(root, rooted);
			Assert.False(v.Safe);
			Assert.Equal("outside-root", v.Reason);
		}

		[Fact]
		public void Resolve_PrefixSiblingEscape_Escapes()
		{
			var root = Root();
			// …/download vs …/download-evil — a naive prefix test would admit this;
			// the trailing-separator normalisation rejects it.
			var sibling = ".." + Path.DirectorySeparatorChar + "download-evil"
				+ Path.DirectorySeparatorChar + "x";
			var v = PathContainmentCheck.Resolve(root, sibling);
			Assert.False(v.Safe);
			Assert.Equal("outside-root", v.Reason);
		}

		[Fact]
		public void Resolve_EmptyName_Escapes()
		{
			var v = PathContainmentCheck.Resolve(Root(), "");
			Assert.False(v.Safe);
			Assert.Equal("empty-name", v.Reason);
		}

		[Fact]
		public void Resolve_EmptyRoot_Escapes()
		{
			var v = PathContainmentCheck.Resolve("", "file.unverified");
			Assert.False(v.Safe);
			Assert.Equal("empty-root", v.Reason);
		}
	}
}
