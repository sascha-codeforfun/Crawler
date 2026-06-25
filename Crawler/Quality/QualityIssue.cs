namespace Crawler.Quality
{
	internal record QualityIssue(
		string Filename,
		string IssueType,
		string Detail,
		string Context);
}
