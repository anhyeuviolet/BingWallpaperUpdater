using System.Text;
using BingWallpaperUpdater.Core.Cache;

namespace BingWallpaperUpdater.Core.Tests;

/// <summary>
/// SRC-08 / NFR-03: the SOF-header dimension reader on baseline, progressive, EXIF-prefixed, padded, truncated
/// and garbage inputs — and proof that it reads only the header, never the image data.
/// </summary>
public sealed class JpegHeaderTests
{
    private const int W = 3840;
    private const int H = 2160;

    /// <summary>Byte builder for synthetic JPEG headers.</summary>
    private static class Jpeg
    {
        public static byte[] Soi => [0xFF, 0xD8];

        public static byte[] Eoi => [0xFF, 0xD9];

        public static byte[] Fill(int count) => Enumerable.Repeat((byte)0xFF, count).ToArray();

        /// <summary>FF marker, 2-byte big-endian length (payload + 2), payload.</summary>
        public static byte[] Segment(byte marker, byte[] payload)
        {
            int length = payload.Length + 2;
            var bytes = new List<byte> { 0xFF, marker, (byte)(length >> 8), (byte)(length & 0xFF) };
            bytes.AddRange(payload);
            return bytes.ToArray();
        }

        /// <summary>SOFn: precision 8, height, width, 3 components (4:2:0 luma, two chroma).</summary>
        public static byte[] Sof(byte marker, int width, int height) => Segment(marker,
        [
            0x08,
            (byte)(height >> 8), (byte)(height & 0xFF),
            (byte)(width >> 8), (byte)(width & 0xFF),
            0x03,
            0x01, 0x22, 0x00,
            0x02, 0x11, 0x01,
            0x03, 0x11, 0x01,
        ]);

        public static byte[] App0 => Segment(0xE0, [0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);

        /// <summary>APP1 "Exif\0\0" + <paramref name="payloadBytes"/> bytes of filler (contains stray FF bytes on purpose).</summary>
        public static byte[] App1(int payloadBytes)
        {
            var payload = new List<byte>(Encoding.ASCII.GetBytes("Exif\0\0"));
            for (int i = 0; i < payloadBytes; i++)
            {
                payload.Add((byte)(i % 7 == 0 ? 0xFF : (i % 5 == 0 ? 0xC0 : 0x42)));
            }

            return Segment(0xE1, payload.ToArray());
        }

        public static byte[] Dqt => Segment(0xDB, Enumerable.Repeat((byte)0x10, 65).ToArray());

        public static byte[] Dht => Segment(0xC4, Enumerable.Repeat((byte)0x01, 20).ToArray());

        /// <summary>SOS header followed by a few bytes of entropy-coded data.</summary>
        public static byte[] Sos => Segment(0xDA, [0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]).Concat(new byte[] { 0x12, 0x34, 0x56 }).ToArray();

        public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    }

    /// <summary>Non-seekable wrapper that counts every byte handed to the reader.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;

        public CountingStream(Stream inner) => _inner = inner;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = _inner.Read(buffer);
            BytesRead += n;
            return n;
        }

        public override int ReadByte()
        {
            int b = _inner.ReadByte();
            if (b >= 0)
            {
                BytesRead++;
            }

            return b;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static (int Width, int Height)? Read(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return JpegHeader.ReadDimensions(ms);
    }

    private static (int Width, int Height)? ReadNonSeekable(byte[] bytes)
    {
        using var counting = new CountingStream(new MemoryStream(bytes));
        return JpegHeader.ReadDimensions(counting);
    }

    // ---- accepted header shapes ---------------------------------------------------------------------

    [Fact]
    public void Sof0_Minimal_SoiSofEoi_ReturnsDimensions()
    {
        byte[] body = Jpeg.Concat(Jpeg.Soi, Jpeg.Sof(0xC0, W, H), Jpeg.Eoi);

        Assert.Equal((W, H), Read(body));
        Assert.Equal((W, H), ReadNonSeekable(body));
    }

    [Fact]
    public void Sof0_AfterApp0App1ExifAndDqt_ReturnsTheSofDimensions()
    {
        byte[] body = Jpeg.Concat(Jpeg.Soi, Jpeg.App0, Jpeg.App1(1024), Jpeg.Dqt, Jpeg.Sof(0xC0, W, H), Jpeg.Sos, Jpeg.Eoi);

        Assert.Equal((W, H), Read(body));
        Assert.Equal((W, H), ReadNonSeekable(body));
    }

    [Fact]
    public void Sof2_Progressive_ReturnsDimensions()
    {
        byte[] body = Jpeg.Concat(Jpeg.Soi, Jpeg.App0, Jpeg.Sof(0xC2, W, H), Jpeg.Eoi);

        Assert.Equal((W, H), Read(body));
    }

    [Fact]
    public void Sof1_AfterDht_ReturnsDimensions()
    {
        byte[] body = Jpeg.Concat(Jpeg.Soi, Jpeg.Dht, Jpeg.Sof(0xC1, W, H), Jpeg.Eoi);

        Assert.Equal((W, H), Read(body));
    }

    [Theory]
    [InlineData(0xC0)]
    [InlineData(0xC1)]
    [InlineData(0xC2)]
    [InlineData(0xC3)]
    [InlineData(0xC5)]
    [InlineData(0xC6)]
    [InlineData(0xC7)]
    [InlineData(0xC9)]
    [InlineData(0xCA)]
    [InlineData(0xCB)]
    [InlineData(0xCD)]
    [InlineData(0xCE)]
    [InlineData(0xCF)]
    public void EverySofMarker_ReturnsDimensions(int marker)
    {
        byte[] body = Jpeg.Concat(Jpeg.Soi, Jpeg.App0, Jpeg.Sof((byte)marker, 384, 216), Jpeg.Eoi);

        Assert.Equal((384, 216), Read(body));
        Assert.Equal((384, 216), ReadNonSeekable(body));
    }

    [Fact]
    public void FillBytesBeforeTheSofMarker_AreSkipped()
    {
        // FF FF FF C0 ... : padding FFs before the marker byte.
        byte[] body = Jpeg.Concat(Jpeg.Soi, Jpeg.App0, Jpeg.Fill(3), Jpeg.Sof(0xC0, W, H)[1..], Jpeg.Eoi);

        Assert.Equal((W, H), Read(body));
        Assert.Equal((W, H), ReadNonSeekable(body));
    }

    // ---- rejected inputs ----------------------------------------------------------------------------

    [Fact]
    public void TruncatedInsideTheSofSegment_ReturnsNull()
    {
        byte[] full = Jpeg.Concat(Jpeg.Soi, Jpeg.App0, Jpeg.Sof(0xC0, W, H));
        // Cut after the SOF length + precision + first height byte (dimensions incomplete).
        byte[] cut = full[..(Jpeg.Soi.Length + Jpeg.App0.Length + 6)];

        Assert.Null(Read(cut));
        Assert.Null(ReadNonSeekable(cut));
    }

    [Fact]
    public void TruncatedInsideTheApp1Segment_ReturnsNull()
    {
        byte[] full = Jpeg.Concat(Jpeg.Soi, Jpeg.App1(1024));
        byte[] cut = full[..(full.Length - 100)];

        Assert.Null(Read(cut));
        Assert.Null(ReadNonSeekable(cut));
    }

    [Fact]
    public void CutAfterSoi_ReturnsNull()
    {
        Assert.Null(Read(Jpeg.Soi));
        Assert.Null(ReadNonSeekable(Jpeg.Soi));
    }

    [Fact]
    public void SoiThenEoi_ReturnsNull()
    {
        Assert.Null(Read(Jpeg.Concat(Jpeg.Soi, Jpeg.Eoi)));
    }

    [Fact]
    public void TwoThousandZeroBytes_ReturnsNull()
    {
        Assert.Null(Read(new byte[2000]));
    }

    [Fact]
    public void EmptyStream_ReturnsNull()
    {
        Assert.Null(Read([]));
    }

    [Fact]
    public void Gif89a_ReturnsNull()
    {
        byte[] gif = Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[] { 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00 }).ToArray();

        Assert.Null(Read(gif));
    }

    [Fact]
    public void SosBeforeAnySof_ReturnsNull()
    {
        byte[] body = Jpeg.Concat(Jpeg.Soi, Jpeg.App0, Jpeg.Dqt, Jpeg.Sos, Jpeg.Sof(0xC0, W, H), Jpeg.Eoi);

        Assert.Null(Read(body));
        Assert.Null(ReadNonSeekable(body));
    }

    [Fact]
    public void ZeroWidthOrHeight_ReturnsNull()
    {
        Assert.Null(Read(Jpeg.Concat(Jpeg.Soi, Jpeg.Sof(0xC0, 0, H), Jpeg.Eoi)));
        Assert.Null(Read(Jpeg.Concat(Jpeg.Soi, Jpeg.Sof(0xC0, W, 0), Jpeg.Eoi)));
    }

    // ---- header-only guarantee (NFR-03, T-01-16) ----------------------------------------------------

    [Fact]
    public void ReadsAtMostTheHeader_On50MbBody_FewerThan64KbRead()
    {
        byte[] header = Jpeg.Concat(Jpeg.Soi, Jpeg.App0, Jpeg.Sof(0xC0, W, H), Jpeg.Sos);
        var body = new byte[50 * 1024 * 1024];
        header.CopyTo(body, 0);
        using var counting = new CountingStream(new MemoryStream(body));

        (int Width, int Height)? dims = JpegHeader.ReadDimensions(counting);

        Assert.Equal((W, H), dims);
        Assert.True(counting.BytesRead < 65536, $"read {counting.BytesRead} bytes");
    }

    [Fact]
    public void Placeholder404Fixture_HasReadableDimensions_ButIsRejectedByStatusNotByHeader()
    {
        // The CDN's 404 body is a real JPEG (Plan 02 fixture); the header reader must still parse it.
        byte[] body = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "placeholder-404.jpg"));

        (int Width, int Height)? dims = Read(body);

        Assert.NotNull(dims);
        Assert.True(dims!.Value.Width > 0 && dims.Value.Height > 0);
    }
}
