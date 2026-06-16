// SPDX-License-Identifier: MIT
//
// Clean-room implementation — written solely from public specifications, with
// no third-party source consulted. Tag-number → name maps derived from:
//   - EXIF/TIFF: CIPA DC-008 and Adobe TIFF 6.0.
//   - GPS sub-IFD and IPTC-IIM dataset numbers: the EXIF and IPTC-IIM specs.
//
// Not exhaustive: covers the fields a human reviewing image metadata cares
// about. Unknown tags are still parsed and returned with a generic name.

namespace Crawler.AssetMetadata
{
    internal static class ExifTagNames
    {
        // IFD0 / IFD1 / Exif private IFD share one TIFF tag space.
        private static readonly Dictionary<ushort, string> General = new()
        {
            [0x0100] = "ImageWidth", [0x0101] = "ImageLength", [0x0102] = "BitsPerSample",
            [0x0103] = "Compression", [0x0106] = "PhotometricInterpretation",
            [0x010E] = "ImageDescription", [0x010F] = "Make", [0x0110] = "Model",
            [0x0112] = "Orientation", [0x011A] = "XResolution", [0x011B] = "YResolution",
            [0x0128] = "ResolutionUnit", [0x0131] = "Software", [0x0132] = "DateTime",
            [0x013B] = "Artist", [0x0213] = "YCbCrPositioning", [0x8298] = "Copyright",
            [0x829A] = "ExposureTime", [0x829D] = "FNumber", [0x8769] = "ExifIFDPointer",
            [0x8822] = "ExposureProgram", [0x8825] = "GpsIFDPointer", [0x8827] = "ISOSpeedRatings",
            [0x9000] = "ExifVersion", [0x9003] = "DateTimeOriginal", [0x9004] = "DateTimeDigitized",
            [0x9207] = "MeteringMode", [0x9209] = "Flash", [0x920A] = "FocalLength",
            [0x927C] = "MakerNote", [0x9286] = "UserComment", [0xA000] = "FlashpixVersion",
            [0xA001] = "ColorSpace", [0xA002] = "PixelXDimension", [0xA003] = "PixelYDimension",
            [0xA005] = "InteropIFDPointer", [0xA402] = "ExposureMode", [0xA403] = "WhiteBalance",
            [0xA405] = "FocalLengthIn35mmFilm", [0xA406] = "SceneCaptureType",
            [0xA430] = "CameraOwnerName", [0xA431] = "BodySerialNumber", [0xA432] = "LensSpecification",
            [0xA433] = "LensMake", [0xA434] = "LensModel", [0xA435] = "LensSerialNumber",
        };

        private static readonly Dictionary<ushort, string> Gps = new()
        {
            [0x0000] = "GPSVersionID", [0x0001] = "GPSLatitudeRef", [0x0002] = "GPSLatitude",
            [0x0003] = "GPSLongitudeRef", [0x0004] = "GPSLongitude", [0x0005] = "GPSAltitudeRef",
            [0x0006] = "GPSAltitude", [0x0007] = "GPSTimeStamp", [0x0012] = "GPSMapDatum",
            [0x001D] = "GPSDateStamp",
        };

        public static string Resolve(ExifIfd ifd, ushort id)
        {
            var table = ifd == ExifIfd.Gps ? Gps : General;
            return table.TryGetValue(id, out var name) ? name : $"Tag_0x{id:X4}";
        }
    }
}
