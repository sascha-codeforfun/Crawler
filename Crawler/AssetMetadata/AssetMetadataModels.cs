// SPDX-License-Identifier: MIT
//
// Clean-room implementation — written solely from public specifications, with
// no third-party source consulted. Type/result model for the metadata reader.
// Field semantics derive from:
//   - EXIF: CIPA DC-008 (Exif) and Adobe TIFF 6.0 (tag numbers, IFD layout).
//   - IPTC: IPTC-IIM specification (record/dataset numbering).
//   - XMP : ISO 16684-1 and the Adobe XMP specification (dc:/xmpRights:/photoshop: names).
//
// Part of Crawler.AssetMetadata: a descriptive reader for embedded image
// metadata. It reports what is present and its value; it renders NO judgement
// about whether any value constitutes a problem. Silence means "not found /
// could not read here", never an assertion that a field is absent.

using System.Globalization;
using System.Text;

namespace Crawler.AssetMetadata
{
    /// <summary>Container format the bytes were recognised as.</summary>
    public enum ImageFormat
    {
        Unknown,
        Jpeg,
        Png,
        Webp,
        Gif,
    }

    /// <summary>TIFF/EXIF field data types (TIFF 6.0, table 2).</summary>
    public enum ExifType : ushort
    {
        Byte = 1, Ascii = 2, Short = 3, Long = 4, Rational = 5,
        SByte = 6, Undefined = 7, SShort = 8, SLong = 9, SRational = 10,
        Float = 11, Double = 12,
    }

    /// <summary>Which image-file directory a tag was found in.</summary>
    public enum ExifIfd { Primary, Exif, Gps, Interop, Thumbnail }

    /// <summary>Unsigned TIFF rational (numerator / denominator).</summary>
    public readonly record struct Rational(uint Numerator, uint Denominator)
    {
        public double ToDouble() => Denominator == 0 ? double.NaN : (double)Numerator / Denominator;
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");
    }

    /// <summary>Signed TIFF rational.</summary>
    public readonly record struct SRational(int Numerator, int Denominator)
    {
        public double ToDouble() => Denominator == 0 ? double.NaN : (double)Numerator / Denominator;
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");
    }

    /// <summary>A single decoded EXIF field.</summary>
    public sealed class ExifTag
    {
        public ExifIfd Ifd { get; init; }
        public ushort Id { get; init; }
        public string Name { get; init; } = "";
        public ExifType Type { get; init; }
        public int Count { get; init; }

        /// <summary>ASCII→string; integer types→long[]; Rational→Rational[];
        /// SRational→SRational[]; Float/Double→double[]; Undefined→byte[].</summary>
        public object? Value { get; init; }
        public string Display { get; init; } = "";

        public string? AsString() => Value as string;

        public long? AsInt()
        {
            if (Value is long[] { Length: > 0 } a) return a[0];
            if (Value is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            return null;
        }

        public override string ToString() => $"[{Ifd}] {Name} (0x{Id:X4}) = {Display}";
    }

    /// <summary>A GPS position decoded to signed decimal degrees.</summary>
    public sealed record GpsLocation(double Latitude, double Longitude, double? AltitudeMeters)
    {
        public override string ToString()
        {
            var alt = AltitudeMeters is { } a ? $", {a.ToString("0.#", CultureInfo.InvariantCulture)} m" : "";
            return string.Format(CultureInfo.InvariantCulture, "{0:0.######}, {1:0.######}{2}", Latitude, Longitude, alt);
        }
    }

    /// <summary>One flat (name, value) pair for the human-facing detail columns.</summary>
    public readonly record struct MetaItem(string Name, string Value);

    /// <summary>
    /// EXIF result. Always returned, never throws; malformed regions degrade to
    /// warnings so a crawler keeps scanning.
    /// </summary>
    public sealed class ExifResult
    {
        private readonly List<ExifTag> _tags = new();
        private readonly Dictionary<(ExifIfd, ushort), ExifTag> _index = new();
        private readonly List<string> _warnings = new();

        public bool HasExif { get; internal set; }
        public IReadOnlyList<ExifTag> Tags => _tags;
        public IReadOnlyList<string> Warnings => _warnings;

        internal void AddTag(ExifTag tag) { _tags.Add(tag); _index[(tag.Ifd, tag.Id)] = tag; }
        internal void AddWarning(string m) => _warnings.Add(m);

        public ExifTag? Find(ExifIfd ifd, ushort id) => _index.TryGetValue((ifd, id), out var t) ? t : null;

        // Curated accessors. Tag numbers are EXIF/TIFF spec constants.
        public string? Make => Find(ExifIfd.Primary, 0x010F)?.AsString();
        public string? Model => Find(ExifIfd.Primary, 0x0110)?.AsString();
        public string? Software => Find(ExifIfd.Primary, 0x0131)?.AsString();
        public string? Artist => Find(ExifIfd.Primary, 0x013B)?.AsString();
        public string? Copyright => Find(ExifIfd.Primary, 0x8298)?.AsString();
        public string? DateTimeOriginal => Find(ExifIfd.Exif, 0x9003)?.AsString();
        public string? CameraOwnerName => Find(ExifIfd.Exif, 0xA430)?.AsString();
        public string? BodySerialNumber => Find(ExifIfd.Exif, 0xA431)?.AsString();
        public string? LensSerialNumber => Find(ExifIfd.Exif, 0xA435)?.AsString();

        private GpsLocation? _gps;
        private bool _gpsResolved;
        public GpsLocation? Location
        {
            get { if (!_gpsResolved) { _gpsResolved = true; _gps = DecodeGps(); } return _gps; }
        }

        private GpsLocation? DecodeGps()
        {
            // GPS IFD tags per EXIF spec: 1/2 latitude(ref,value), 3/4 longitude, 5/6 altitude.
            var lat = ToDegrees(Find(ExifIfd.Gps, 0x0002), Find(ExifIfd.Gps, 0x0001)?.AsString());
            var lon = ToDegrees(Find(ExifIfd.Gps, 0x0004), Find(ExifIfd.Gps, 0x0003)?.AsString());
            if (lat is null || lon is null) return null;

            double? alt = null;
            if (Find(ExifIfd.Gps, 0x0006)?.Value is Rational[] { Length: > 0 } ar)
            {
                alt = ar[0].ToDouble();
                if (Find(ExifIfd.Gps, 0x0005)?.AsInt() == 1) alt = -alt; // AltitudeRef 1 = below sea level
            }
            return new GpsLocation(lat.Value, lon.Value, alt);
        }

        private static double? ToDegrees(ExifTag? coord, string? hemisphere)
        {
            // Latitude/Longitude are three RATIONALs: degrees, minutes, seconds.
            if (coord?.Value is not Rational[] r || r.Length < 3) return null;
            double deg = r[0].ToDouble() + r[1].ToDouble() / 60.0 + r[2].ToDouble() / 3600.0;
            if (double.IsNaN(deg)) return null;
            var h = hemisphere?.Trim().ToUpperInvariant();
            if (h is "S" or "W") deg = -deg; // ref gives the sign
            return deg;
        }
    }

    /// <summary>One decoded IPTC-IIM dataset.</summary>
    public sealed record IptcField(int Record, int Dataset, string Name, string Value);

    /// <summary>IPTC-IIM metadata (APP13 / Photoshop 8BIM resource 0x0404).</summary>
    public sealed class IptcResult
    {
        private readonly List<IptcField> _fields = new();
        private readonly List<string> _warnings = new();

        public bool Present { get; internal set; }
        public IReadOnlyList<IptcField> Fields => _fields;
        public IReadOnlyList<string> Warnings => _warnings;

        internal void Add(IptcField f) => _fields.Add(f);
        internal void AddWarning(string m) => _warnings.Add(m);

        private string? First(int r, int d) => _fields.FirstOrDefault(f => f.Record == r && f.Dataset == d)?.Value;

        // Record 2 (Application Record) dataset numbers per IPTC-IIM.
        public string? CopyrightNotice => First(2, 116);
        public string? Byline => First(2, 80);
        public string? Credit => First(2, 110);
        public string? Source => First(2, 115);
        public string? Caption => First(2, 120);
        public string? Headline => First(2, 105);
        public string? ObjectName => First(2, 5);
    }

    /// <summary>XMP rights/attribution fields (ISO 16684 / RDF-XML).</summary>
    public sealed class XmpResult
    {
        private readonly List<string> _warnings = new();
        public bool Present { get; internal set; }
        public IReadOnlyList<string> Warnings => _warnings;
        internal void AddWarning(string m) => _warnings.Add(m);

        public IReadOnlyList<string> Rights { get; internal set; } = Array.Empty<string>();       // dc:rights
        public IReadOnlyList<string> Creators { get; internal set; } = Array.Empty<string>();     // dc:creator
        public IReadOnlyList<string> Titles { get; internal set; } = Array.Empty<string>();       // dc:title
        public IReadOnlyList<string> Descriptions { get; internal set; } = Array.Empty<string>(); // dc:description
        public IReadOnlyList<string> Keywords { get; internal set; } = Array.Empty<string>();     // dc:subject
        public IReadOnlyList<string> UsageTerms { get; internal set; } = Array.Empty<string>();   // xmpRights:UsageTerms
        public string? Marked { get; internal set; }        // xmpRights:Marked
        public string? WebStatement { get; internal set; }  // xmpRights:WebStatement
        public string? Credit { get; internal set; }        // photoshop:Credit
        public string? Source { get; internal set; }        // photoshop:Source
        public string? Headline { get; internal set; }      // photoshop:Headline
    }
}
