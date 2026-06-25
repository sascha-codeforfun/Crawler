using Xunit;

namespace Crawler.Tests
{
	/// <summary>
	/// Test classes that initialise the static Logger singleton must not run in
	/// parallel with each other — they share process-wide static state and will
	/// collide on the log file. Placing them all in this collection forces xUnit
	/// to run them sequentially.
	/// Test classes that do not use Logger (AttributeNoiseDetectorTests,
	/// ContentQualityTests, ExtractorTests, PdfQualityAnalyzerTests) are
	/// not in this collection and run freely in parallel.
	/// </summary>
	[CollectionDefinition("Logger")]
	public class LoggerCollection { }
}
