namespace BingWallpaperUpdater.Core.Tests;

/// <summary>Synthesises a header-only JPEG: SOI, APP0, SOF0 with the given dimensions, EOI.</summary>
internal static class JpegBytes
{
    public static byte[] Sof0(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        // APP0 (JFIF) so the header walker has a non-SOF segment to skip first.
        bytes.AddRange(new byte[] { 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 });
        bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });
        bytes.Add((byte)(height >> 8));
        bytes.Add((byte)(height & 0xFF));
        bytes.Add((byte)(width >> 8));
        bytes.Add((byte)(width & 0xFF));
        bytes.Add(0x03);
        bytes.AddRange(new byte[] { 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 });
        bytes.AddRange(new byte[] { 0xFF, 0xD9 });
        return bytes.ToArray();
    }
}
