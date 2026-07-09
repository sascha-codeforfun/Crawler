using System;
using System.Collections.Generic;
using Crawler;
using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Delivery 640: the Triage row becomes a traffic-light of independently-coloured segments. These
	/// cover the pure pieces — WrapSegments (segment packing across lines) and the dictionary segment's
	/// off/report/interactive → red/amber/green mapping. The coloured rendering itself is visual.
	/// </summary>
	public class TriageRowSegmentsTests
	{
		[Fact]
		public void WrapSegments_Empty_ReturnsEmpty()
		{
			Assert.Empty(ConsoleUi.WrapSegments(new List<string>(), 40));
		}

		[Fact]
		public void WrapSegments_AllFit_OneLine()
		{
			var segs = new List<string> { "content on", "spell interactive", "dictionary report" };
			Assert.Equal(new[] { 3 }, ConsoleUi.WrapSegments(segs, 80));
		}

		[Fact]
		public void WrapSegments_Narrow_BreaksBetweenWholeSegments()
		{
			var segs = new List<string> { "content on", "spell interactive", "dictionary report" };
			// width 40: "content on"(10) + " · spell interactive"(20)=30 fits; +dictionary report overflows
			Assert.Equal(new[] { 2, 1 }, ConsoleUi.WrapSegments(segs, 40));
			// width 25: each segment lands on its own line
			Assert.Equal(new[] { 1, 1, 1 }, ConsoleUi.WrapSegments(segs, 25));
		}

		[Fact]
		public void WrapSegments_TinyWidth_FloorsWithoutCrash()
		{
			var segs = new List<string> { "content on", "spell interactive", "dictionary report" };
			Assert.Equal(new[] { 1, 1, 1 }, ConsoleUi.WrapSegments(segs, 2));
		}

		[Fact]
		public void DictionaryTriageSegment_TrafficLight()
		{
			Assert.Equal(("dictionary off", ConsoleColor.Red),
				ConfigSummary.DescribeDictionaryTriageSegment(new DictionaryMaintenanceConfig()));

			Assert.Equal(("dictionary report", ConsoleColor.DarkYellow),
				ConfigSummary.DescribeDictionaryTriageSegment(new DictionaryMaintenanceConfig { Mode = "Report" }));

			Assert.Equal(("dictionary interactive", ConsoleColor.Green),
				ConfigSummary.DescribeDictionaryTriageSegment(new DictionaryMaintenanceConfig { Mode = "Interactive" }));

			Assert.Equal(("dictionary interactive (user+site)", ConsoleColor.Green),
				ConfigSummary.DescribeDictionaryTriageSegment(
					new DictionaryMaintenanceConfig { Mode = "Interactive", UpdateUserDictionary = true, UpdateSiteSpecificDictionary = true }));
		}
	}
}
