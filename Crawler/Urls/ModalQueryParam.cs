namespace Crawler.Urls
{
	using System;
	using System.Text.Json;
	using System.Text.Json.Serialization;

	/// <summary>
	/// One entry in <see cref="Config.ModalQueryParameters"/>: a query-parameter name
	/// whose value carries a URL-encoded target URL (modal/overlay/lightbox carrier),
	/// plus the optional page suffix to append when the decoded target is
	/// extension-less.
	///
	/// <para>
	/// BACKGROUND — why <see cref="AppendSuffix"/> exists. Some CMSes reference modal
	/// content by an <em>extension-less</em> resource path encoded into a carrier
	/// page's query string, e.g. <c>carrier.html?modal=%2Fpath%2Fto%2Ffragment</c>.
	/// The real, fetchable page is that decoded path plus the site's page extension
	/// (e.g. <c>.html</c>); the bare extension-less form may not resolve on its own
	/// (a redirect-to-directory that then fails on a direct GET). <see cref="AppendSuffix"/>
	/// restores that extension — per-parameter and from config, so the engine stays
	/// generic (no hard-coded parameter name or extension).
	/// </para>
	///
	/// <para>
	/// NON-BREAKING SHAPE. The config array accepts BOTH shapes per element:
	/// <list type="bullet">
	///   <item><description>
	///     a bare string — <c>"lightbox"</c> — the legacy shape. Binds to
	///     <see cref="AppendSuffix"/> = <c>""</c>, i.e. today's exact behavior
	///     (extract, decode, strip inner query, NO suffix). Existing configs are
	///     untouched.
	///   </description></item>
	///   <item><description>
	///     an object — <c>{ "Param": "lightbox", "AppendSuffix": ".html" }</c> — the
	///     new shape, opting into the suffix. <c>Param</c> is required; a missing
	///     <c>Param</c> on an object element fails loudly at parse (so a future
	///     config-shape drift can't silently bind to a default the way the original
	///     regression did).
	///   </description></item>
	/// </list>
	/// Mixed arrays are allowed: <c>[ "overlay", { "Param": "lightbox", "AppendSuffix": ".html" } ]</c>.
	/// </para>
	/// </summary>
	[JsonConverter(typeof(ModalQueryParamConverter))]
	public sealed record ModalQueryParam(string Param, string AppendSuffix = "")
	{
		/// <summary>
		/// Applies <see cref="AppendSuffix"/> to a decoded modal target, idempotently.
		/// No-op when the suffix is empty (legacy string entries), when the target
		/// already carries an extension, when it ends in <c>/</c> (a directory form),
		/// or when it already ends with the suffix (prevents the <c>.html.html</c>
		/// double-suffix class). Returns <paramref name="target"/> unchanged in all
		/// those cases, so a bare-string entry reproduces the pre-change output exactly.
		/// </summary>
		public string ApplySuffix(string target)
		{
			if (string.IsNullOrEmpty(AppendSuffix) || string.IsNullOrEmpty(target))
			{
				return target;
			}

			if (target.EndsWith("/", StringComparison.Ordinal))
			{
				return target;
			}

			if (target.EndsWith(AppendSuffix, StringComparison.OrdinalIgnoreCase))
			{
				return target;
			}

			// Path.HasExtension over the path segment only — guard against a query or
			// fragment (there should be none here post-RemoveQueryString, but be safe)
			// and against a dotted directory name being mistaken for an extension is
			// out of scope; the crawler's modal targets are simple node paths.
			if (HasPathExtension(target))
			{
				return target;
			}

			return target + AppendSuffix;
		}

		private static bool HasPathExtension(string url)
		{
			// Consider only the last path segment. Avoids Path.HasExtension quirks on
			// full URLs and treats "…/foo.bar/baz" (dotted directory) correctly.
			var lastSlash = url.LastIndexOf('/');
			var lastSegment = lastSlash >= 0 ? url[(lastSlash + 1)..] : url;
			var dot = lastSegment.LastIndexOf('.');
			return dot > 0 && dot < lastSegment.Length - 1;
		}
	}

	/// <summary>
	/// System.Text.Json converter that lets each <see cref="ModalQueryParam"/> element
	/// be written as a plain string (legacy) or an object (new). See the type doc for
	/// the non-breaking contract.
	/// </summary>
	public sealed class ModalQueryParamConverter : JsonConverter<ModalQueryParam>
	{
		public override ModalQueryParam Read(
			ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			switch (reader.TokenType)
			{
				// Legacy: "lightbox"  →  suffix "" (today's behavior, unchanged).
				case JsonTokenType.String:
				{
					var name = reader.GetString();
					if (string.IsNullOrWhiteSpace(name))
					{
						throw new JsonException(
							"ModalQueryParameters: a string entry must be a non-empty parameter name.");
					}

					return new ModalQueryParam(name, string.Empty);
				}

				// New: { "Param": "lightbox", "AppendSuffix": ".html" }
				case JsonTokenType.StartObject:
				{
					using var doc = JsonDocument.ParseValue(ref reader);
					var root = doc.RootElement;

					// Param is required — fail loud rather than bind-to-default.
					if (!TryGetPropertyCaseInsensitive(root, "Param", out var paramEl)
						|| paramEl.ValueKind != JsonValueKind.String
						|| string.IsNullOrWhiteSpace(paramEl.GetString()))
					{
						throw new JsonException(
							"ModalQueryParameters: an object entry requires a non-empty 'Param' string.");
					}

					var name = paramEl.GetString()!;

					var suffix = string.Empty;
					if (TryGetPropertyCaseInsensitive(root, "AppendSuffix", out var suffixEl)
						&& suffixEl.ValueKind == JsonValueKind.String)
					{
						suffix = suffixEl.GetString() ?? string.Empty;
					}

					return new ModalQueryParam(name, suffix);
				}

				default:
					throw new JsonException(
						"ModalQueryParameters: each entry must be a string or an object " +
						"({ \"Param\": \"…\", \"AppendSuffix\": \"…\" }).");
			}
		}

		public override void Write(
			Utf8JsonWriter writer, ModalQueryParam value, JsonSerializerOptions options)
		{
			// Round-trip in the richer object shape (harmless: the reader accepts it).
			// Emitting the legacy string form when AppendSuffix is empty is also valid;
			// object form is chosen for clarity when serializing back out.
			if (string.IsNullOrEmpty(value.AppendSuffix))
			{
				writer.WriteStringValue(value.Param);
				return;
			}

			writer.WriteStartObject();
			writer.WriteString("Param", value.Param);
			writer.WriteString("AppendSuffix", value.AppendSuffix);
			writer.WriteEndObject();
		}

		private static bool TryGetPropertyCaseInsensitive(
			JsonElement obj, string name, out JsonElement value)
		{
			foreach (var prop in obj.EnumerateObject())
			{
				if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					value = prop.Value;
					return true;
				}
			}

			value = default;
			return false;
		}
	}
}
