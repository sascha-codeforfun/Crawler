using Crawler.SpellCheck;
using Xunit;

namespace Crawler.Tests.SpellCheck
{
	/// <summary>
	/// Pins the stable-key derivation: the build-time cache-buster is stripped so a bundle's
	/// identity survives re-deploys, while two distinct bundles that happen to share a fingerprint
	/// stay distinct. Fixtures invented; the patterns mirror an observed shape.
	/// </summary>
	public class ScriptUrlKeyTests
	{
		[Fact]
		public void StripsTrailingCacheBuster()
		{
			Assert.Equal(
				"/etc/clientlibs/x/foo_bar.min.js",
				ScriptUrlKey.StableKey("https://h/etc/clientlibs/x/foo_bar.min.434a0151e01261649e0f17a2b6401b4d.js"));
		}

		[Fact]
		public void HashlessNameUnchanged()
		{
			Assert.Equal("/x/xyz.min.js", ScriptUrlKey.StableKey("https://h/x/xyz.min.js"));
			Assert.Equal("/x/foo_bar.js", ScriptUrlKey.StableKey("https://h/x/foo_bar.js"));
		}

		[Fact]
		public void SharedFingerprint_DistinctBundlesStayDistinct()
		{
			// Observed on the corpus: foo_bar and some_calculator shipped under the SAME hash.
			// Keying on the hash would collide them; keying on the path must not.
			const string h = "1af36ca40815c63b204487cd79530475";
			var a = ScriptUrlKey.StableKey($"https://h/x/foo_bar.min.{h}.js");
			var b = ScriptUrlKey.StableKey($"https://h/x/some_calculator.min.{h}.js");
			Assert.NotEqual(a, b);
		}

		[Fact]
		public void NonStandardFingerprintLeftIntact_NeverWrongJoined()
		{
			// A vendor agent with a trailing numeric id is not the 32-hex shape — left as-is rather
			// than mangled (it may churn, but it can never wrong-join to another asset).
			const string url = "https://h/x/ruxitagentjs_ICANVefhjqrtux_10331260218130851.js";
			Assert.Equal("/x/ruxitagentjs_ICANVefhjqrtux_10331260218130851.js", ScriptUrlKey.StableKey(url));
		}

		[Fact]
		public void EmptyIsEmpty()
		{
			Assert.Equal(string.Empty, ScriptUrlKey.StableKey(""));
		}
	}
}
