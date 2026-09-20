namespace BingWallpaperUpdater.Core.Cache;

/// <summary>
/// Header-only JPEG dimension reader: walks the marker segments from SOI to the first SOFn and reads the
/// frame height/width. No decoding, no drawing APIs — a 3840x2160 image costs a few hundred bytes of I/O
/// instead of a 33 MB bitmap (NFR-03; RESEARCH "Don't decode the JPEG to validate").
/// </summary>
public static class JpegHeader
{
    /// <summary>Returns (Width, Height) or null when the stream is not a JPEG with a readable SOF segment.</summary>
    public static (int Width, int Height)? ReadDimensions(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // SOI
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
        {
            return null;
        }

        Span<byte> buf = stackalloc byte[5];
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                return null;
            }

            if (b != 0xFF)
            {
                continue; // tolerate stray bytes between segments
            }

            int marker;
            do
            {
                marker = stream.ReadByte();
            }
            while (marker == 0xFF); // fill bytes

            if (marker < 0)
            {
                return null;
            }

            // Standalone markers without a length field.
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8))
            {
                continue;
            }

            // EOI or start of scan: no SOF found before the image data.
            if (marker == 0xD9 || marker == 0xDA)
            {
                return null;
            }

            int lenHi = stream.ReadByte();
            int lenLo = stream.ReadByte();
            if (lenHi < 0 || lenLo < 0)
            {
                return null;
            }

            int length = (lenHi << 8) | lenLo;
            if (length < 2)
            {
                return null;
            }

            if (IsStartOfFrame(marker))
            {
                if (length < 7 || !ReadExactly(stream, buf))
                {
                    return null;
                }

                int height = (buf[1] << 8) | buf[2];
                int width = (buf[3] << 8) | buf[4];
                return width > 0 && height > 0 ? (width, height) : null;
            }

            if (!Skip(stream, length - 2))
            {
                return null;
            }
        }
    }

    private static bool IsStartOfFrame(int marker) => marker switch
    {
        0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF => true,
        _ => false,
    };

    private static bool ReadExactly(Stream stream, Span<byte> buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = stream.Read(buf[total..]);
            if (n <= 0)
            {
                return false;
            }

            total += n;
        }

        return true;
    }

    private static bool Skip(Stream stream, int count)
    {
        if (stream.CanSeek)
        {
            if (stream.Position + count > stream.Length)
            {
                return false;
            }

            stream.Seek(count, SeekOrigin.Current);
            return true;
        }

        Span<byte> scratch = stackalloc byte[256];
        while (count > 0)
        {
            int n = stream.Read(scratch[..Math.Min(count, scratch.Length)]);
            if (n <= 0)
            {
                return false;
            }

            count -= n;
        }

        return true;
    }
}
