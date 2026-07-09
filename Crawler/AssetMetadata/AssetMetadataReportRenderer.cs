// SPDX-License-Identifier: MIT
//
// Clean-room implementation — written solely from public specifications, with
// no third-party source consulted. This layer contains no parsing; it renders a
// parsed AssetMetadata into the columns AssetQuality writes.
//
// Output is descriptive, never a verdict: it states which fields are PRESENT and
// their values. Whether any value is a problem is a human decision downstream.
//
// Poisoning defence (extracted metadata is untrusted web content): the caller's
// sanitiser strips delimiters/control chars per value, and these volume caps
// bound the column so one crafted image cannot bloat the log:
//   - per value: MaxValueChars
//   - per column: at most MaxFieldsPerColumn fields, with a "(+K more)" marker
//   - per column: a hard MaxColumnChars ceiling regardless of the above

using System.Text;

namespace Crawler.AssetMetadata
{
    /// <summary>The rendered pieces of one metadata finding.</summary>
    public sealed record AssetMetadataFinding(
        bool HasFinding,
        string Detail,   // category tokens joined with '+', e.g. GPS+Make+Copyright+IptcCopyright
        string Context,  // short "what's present" marker for the app surface
        string Exif,     // full (capped) EXIF inventory
        string Iptc,     // full (capped) IPTC inventory
        string Xmp);     // full (capped) XMP inventory

    public static class AssetMetadataReportRenderer
    {
        private const int MaxValueChars = 200;
        private const int MaxFieldsPerColumn = 100;
        private const int MaxColumnChars = 4096;

        /// <summary>
        /// Build the finding for one image. A finding is produced when at least one
        /// curated token is present (the signal set below). When produced, the three
        /// block columns carry the FULL parsed inventory for a human to read, capped.
        /// <paramref name="sanitize"/> is the host's per-value sanitiser
        /// (IssueLogWriter.SanitizeField(...).Cleaned) — injected so this component
        /// stays free of host dependencies.
        /// </summary>
        public static AssetMetadataFinding Build(AssetMetadata meta, Func<string, string> sanitize)
        {
            var tokens = Tokens(meta);
            if (tokens.Count == 0)
                return new AssetMetadataFinding(false, "", "", "", "", "");

            // Sanitise once, then drop any field that has no visible content left
            // (e.g. IPTC binary version markers that collapse to empty). A field the
            // operator cannot see should neither appear in a column nor inflate the
            // field count — so the cleaned lists drive both the columns and Context.
            var exifItems = Clean(meta.ExifItems, sanitize);
            var iptcItems = Clean(meta.IptcItems, sanitize);
            var xmpItems = Clean(meta.XmpItems, sanitize);

            string detail = string.Join("+", tokens);
            int fieldCount = exifItems.Count + iptcItems.Count + xmpItems.Count;
            string context = Context(meta, fieldCount);
            string exif = RenderColumn(exifItems);
            string iptc = RenderColumn(iptcItems);
            string xmp = RenderColumn(xmpItems);
            return new AssetMetadataFinding(true, detail, context, exif, iptc, xmp);
        }

        // Curated presence tokens, emitted in a fixed order so output is deterministic.
        // These are the fields a human may need to weigh; the block columns hold everything.
        private static List<string> Tokens(AssetMetadata m)
        {
            var t = new List<string>();
            void Add(string name, bool present) { if (present) t.Add(name); }

            Add("GPS", m.Location is not null);
            Add("Make", NonEmpty(m.Exif.Make));
            Add("Model", NonEmpty(m.Exif.Model));
            Add("Software", NonEmpty(m.Exif.Software));
            Add("Artist", NonEmpty(m.Exif.Artist));
            Add("Copyright", NonEmpty(m.Exif.Copyright));
            Add("CameraOwnerName", NonEmpty(m.Exif.CameraOwnerName));
            Add("BodySerialNumber", NonEmpty(m.Exif.BodySerialNumber));
            Add("LensSerialNumber", NonEmpty(m.Exif.LensSerialNumber));
            Add("DateTimeOriginal", NonEmpty(m.Exif.DateTimeOriginal));

            Add("IptcByline", NonEmpty(m.Iptc.Byline));
            Add("IptcCopyright", NonEmpty(m.Iptc.CopyrightNotice));
            Add("IptcCredit", NonEmpty(m.Iptc.Credit));
            Add("IptcSource", NonEmpty(m.Iptc.Source));

            Add("XmpRights", m.Xmp.Rights.Count > 0);
            Add("XmpCreator", m.Xmp.Creators.Count > 0);
            Add("XmpMarked", NonEmpty(m.Xmp.Marked));
            Add("XmpWebStatement", NonEmpty(m.Xmp.WebStatement));
            return t;
        }

        // Short, deterministic "something is present" marker for the app surface.
        // High-level presence flags only — purely observational. The field count is
        // the number of visible (non-empty, post-sanitise) fields across all blocks.
        private static string Context(AssetMetadata m, int fields)
        {
            var flags = new List<string>();
            if (m.Location is not null) flags.Add("GPS");
            bool anyCopyright = NonEmpty(m.Exif.Copyright) || NonEmpty(m.Iptc.CopyrightNotice) || m.Xmp.Rights.Count > 0;
            if (anyCopyright) flags.Add("copyright");
            if (NonEmpty(m.Exif.Make) || NonEmpty(m.Exif.Model)) flags.Add("camera");
            if (NonEmpty(m.Exif.BodySerialNumber) || NonEmpty(m.Exif.LensSerialNumber) || NonEmpty(m.Exif.CameraOwnerName))
                flags.Add("device-id");
            bool anyAuthor = NonEmpty(m.Exif.Artist) || NonEmpty(m.Iptc.Byline) || m.Xmp.Creators.Count > 0;
            if (anyAuthor) flags.Add("author");
            if (NonEmpty(m.Exif.Software)) flags.Add("software");

            string lead = flags.Count > 0 ? string.Join(", ", flags) + " present" : "metadata present";
            return $"{lead} ({fields} field{(fields == 1 ? "" : "s")})";
        }

        // Sanitise each value, cap its length, and drop fields with no visible
        // content. Order matters: sanitise → skip if empty → cap (capping a
        // non-empty value can never re-empty it).
        private static List<(string Name, string Value)> Clean(
            IReadOnlyList<MetaItem> items, Func<string, string> sanitize)
        {
            var cleaned = new List<(string, string)>(items.Count);
            foreach (var item in items)
            {
                string val = sanitize(item.Value ?? "");
                if (val.Length == 0) continue;
                if (val.Length > MaxValueChars) val = val[..(MaxValueChars - 1)] + "…";
                cleaned.Add((item.Name, val));
            }
            return cleaned;
        }

        private static string RenderColumn(List<(string Name, string Value)> items)
        {
            if (items.Count == 0) return "";

            var sb = new StringBuilder();
            int shown = 0;
            foreach (var (name, val) in items)
            {
                if (shown >= MaxFieldsPerColumn) break;
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(name).Append('=').Append(val);
                shown++;
            }

            int remaining = items.Count - shown;
            if (remaining > 0) sb.Append("; (+").Append(remaining).Append(" more)");

            string s = sb.ToString();
            if (s.Length > MaxColumnChars) s = s[..(MaxColumnChars - 1)] + "…";
            return s;
        }

        private static bool NonEmpty(string? s) => !string.IsNullOrWhiteSpace(s);
    }
}
