// SPDX-License-Identifier: MIT
//
// Clean-room implementation — written solely from public specifications, with
// no third-party source consulted. This is the public entry point of the
// Crawler.AssetMetadata component: detect the container (JPEG / PNG / WebP /
// GIF) from its signature, dispatch to the spec-built readers, and return the
// EXIF, IPTC and XMP results separately for the caller to surface.
//
// It reports what is present and its value. It makes no judgement about whether
// any value matters — that is a human decision in the caller. Malformed input
// degrades to warnings; it never throws.

using System.Text;

namespace Crawler.AssetMetadata
{
    /// <summary>All embedded metadata read from one image, grouped by block.</summary>
    public sealed class AssetMetadata
    {
        public ImageFormat Format { get; internal set; } = ImageFormat.Unknown;
        public bool IsImage { get; internal set; }

        public ExifResult Exif { get; internal set; } = new();
        public IptcResult Iptc { get; internal set; } = new();
        public XmpResult Xmp { get; internal set; } = new();

        private readonly List<string> _warnings = new();
        public IReadOnlyList<string> Warnings => _warnings;
        internal void AddWarnings(IEnumerable<string> w) => _warnings.AddRange(w);
        internal void AddWarning(string w) => _warnings.Add(w);

        public GpsLocation? Location => Exif.Location;

        /// <summary>Flat EXIF items for the detail column: a synthesised decoded GPS
        /// line first (when coordinates resolve), then every parsed tag by name.</summary>
        public IReadOnlyList<MetaItem> ExifItems
        {
            get
            {
                var items = new List<MetaItem>();
                if (Location is { } loc) items.Add(new MetaItem("GPSLocation", loc.ToString()));
                foreach (var t in Exif.Tags) items.Add(new MetaItem(t.Name, t.Display));
                return items;
            }
        }

        /// <summary>Flat IPTC items: every parsed dataset by name.</summary>
        public IReadOnlyList<MetaItem> IptcItems =>
            Iptc.Fields.Select(f => new MetaItem(f.Name, f.Value)).ToList();

        /// <summary>Flat XMP items: the curated rights/attribution properties that were present.</summary>
        public IReadOnlyList<MetaItem> XmpItems
        {
            get
            {
                var items = new List<MetaItem>();
                void Many(string name, IReadOnlyList<string> vs) { foreach (var v in vs) items.Add(new MetaItem(name, v)); }
                void One(string name, string? v) { if (!string.IsNullOrWhiteSpace(v)) items.Add(new MetaItem(name, v)); }
                Many("dc:rights", Xmp.Rights);
                Many("dc:creator", Xmp.Creators);
                Many("dc:title", Xmp.Titles);
                Many("dc:description", Xmp.Descriptions);
                Many("dc:subject", Xmp.Keywords);
                Many("xmpRights:UsageTerms", Xmp.UsageTerms);
                One("xmpRights:Marked", Xmp.Marked);
                One("xmpRights:WebStatement", Xmp.WebStatement);
                One("photoshop:Credit", Xmp.Credit);
                One("photoshop:Source", Xmp.Source);
                One("photoshop:Headline", Xmp.Headline);
                return items;
            }
        }
    }

    public static class AssetMetadataReader
    {
        // JPEG application-segment signatures.
        private static readonly byte[] ExifSig = "Exif\0\0"u8.ToArray();
        private static readonly byte[] PhotoshopSig = "Photoshop 3.0\0"u8.ToArray();

        /// <summary>Read all embedded metadata from a file. Never throws.</summary>
        public static AssetMetadata Read(string filePath)
        {
            try { return Read((ReadOnlySpan<byte>)File.ReadAllBytes(filePath)); }
            catch (Exception ex)
            {
                var m = new AssetMetadata();
                m.AddWarning($"Could not read file: {ex.GetType().Name}: {ex.Message}");
                return m;
            }
        }

        /// <summary>Read all embedded metadata from in-memory image bytes. Never throws.</summary>
        public static AssetMetadata Read(ReadOnlySpan<byte> data)
        {
            var meta = new AssetMetadata();
            try
            {
                meta.Format = Detect(data);
                switch (meta.Format)
                {
                    case ImageFormat.Jpeg: meta.IsImage = true; ReadJpeg(data, meta); break;
                    case ImageFormat.Png: meta.IsImage = true; ReadPng(data, meta); break;
                    case ImageFormat.Webp: meta.IsImage = true; ReadWebp(data, meta); break;
                    case ImageFormat.Gif: meta.IsImage = true; break; // no standard embedded EXIF/IPTC/XMP to extract
                    default: break;
                }
            }
            catch (Exception ex)
            {
                meta.AddWarning($"Unhandled read error: {ex.GetType().Name}: {ex.Message}");
            }
            return meta;
        }

        private static ImageFormat Detect(ReadOnlySpan<byte> d)
        {
            if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ImageFormat.Jpeg;
            if (PngReader.IsPng(d)) return ImageFormat.Png;
            if (WebpReader.IsWebp(d)) return ImageFormat.Webp;
            if (d.Length >= 6 && d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F') return ImageFormat.Gif;
            return ImageFormat.Unknown;
        }

        private static void ReadJpeg(ReadOnlySpan<byte> data, AssetMetadata meta)
        {
            var segs = new List<JpegSegment>();
            var warnings = new List<string>();
            JpegSegmentScanner.Scan(data, segs, warnings);

            bool exifDone = false, xmpDone = false;
            var app13Parts = new List<byte[]>(); // Photoshop IRB may be split across APP13 segments

            foreach (var seg in segs)
            {
                if (seg.Marker == 0xE1 && JpegSegmentScanner.PayloadStartsWith(data, seg, ExifSig) && !exifDone)
                {
                    ExifReader.ParseExifPayload(data, seg.PayloadStart, seg.PayloadLength, meta.Exif);
                    exifDone = true;
                }
                else if (seg.Marker == 0xE1 && JpegSegmentScanner.PayloadStartsWith(data, seg, XmpReader.Signature) && !xmpDone)
                {
                    meta.Xmp = XmpReader.ParseJpegSegment(data, seg.PayloadStart, seg.PayloadLength);
                    xmpDone = true;
                }
                else if (seg.Marker == 0xED && JpegSegmentScanner.PayloadStartsWith(data, seg, PhotoshopSig))
                {
                    int from = seg.PayloadStart + PhotoshopSig.Length; // strip "Photoshop 3.0\0"
                    int len = seg.PayloadLength - PhotoshopSig.Length;
                    if (len > 0) app13Parts.Add(data.Slice(from, len).ToArray());
                }
            }

            if (app13Parts.Count > 0)
                meta.Iptc = IptcReader.ParseIrb(Concat(app13Parts));

            meta.AddWarnings(warnings);
        }

        private static void ReadPng(ReadOnlySpan<byte> data, AssetMetadata meta)
        {
            var warnings = new List<string>();
            PngReader.Read(data, meta.Exif, out var xmp, warnings);
            meta.Xmp = xmp;
            meta.AddWarnings(warnings);
        }

        private static void ReadWebp(ReadOnlySpan<byte> data, AssetMetadata meta)
        {
            var warnings = new List<string>();
            WebpReader.Read(data, meta.Exif, out var xmp, warnings);
            meta.Xmp = xmp;
            meta.AddWarnings(warnings);
        }

        private static byte[] Concat(List<byte[]> parts)
        {
            int total = parts.Sum(p => p.Length);
            var buf = new byte[total];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, buf, o, p.Length); o += p.Length; }
            return buf;
        }
    }
}
