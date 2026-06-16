namespace Crawler.Boilerplate
{
	using System.Collections.Generic;

	/// <summary>
	/// One boilerplate group: which pages it governs (<see cref="PathPrefix"/>), where its
	/// boilerplate is checked (<see cref="PagesToCheckBoiler"/>), and what counts as boilerplate
	/// (<see cref="BoilerplateSelectors"/>).
	/// </summary>
	public sealed class BoilerplateGroupConfig
	{
		/// <summary>URL path prefix this group governs; longest match wins (arbitrary depth).</summary>
		public string PathPrefix { get; set; } = string.Empty;

		/// <summary>Pages where this group's boilerplate IS checked (location independent of PathPrefix).</summary>
		public List<string> PagesToCheckBoiler { get; set; } = [];

		/// <summary>Typed selectors marking this group's boilerplate.</summary>
		public List<BoilerplateSelectorConfig> BoilerplateSelectors { get; set; } = [];
	}

	/// <summary>A typed selector entry as it appears in config: { "Type": ..., "Value": ... }.</summary>
	public sealed class BoilerplateSelectorConfig
	{
		public string Type { get; set; } = string.Empty;
		public string Value { get; set; } = string.Empty;
	}
}
