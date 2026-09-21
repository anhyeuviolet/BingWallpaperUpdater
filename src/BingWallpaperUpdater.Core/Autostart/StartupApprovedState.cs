using System.Buffers.Binary;

namespace BingWallpaperUpdater.Core.Autostart;

/// <summary>
/// Pure decoder / encoder of Task Manager's 12-byte <c>HKCU\...\Explorer\StartupApproved\Run</c> value (INST-02).
/// Observed byte patterns (RESEARCH Pattern 7, probed on this machine): an enabled entry is
/// <c>02 00 00 00</c> + 8 zero bytes; a Task-Manager-disabled entry is <c>03 00 00 00</c> + the FILETIME of the
/// disable, little-endian (e.g. <c>03 00 00 00 24 C8 FB 3D 2E 8F DB 01</c>); <c>06</c> / <c>07</c> behave like
/// <c>02</c> / <c>03</c>. One undocumented entry (<c>01</c> + zero timestamp) exists on this machine and is treated
/// as enabled (Assumption A1) — the app's own entry only ever holds <c>02</c> or <c>03</c>. No I/O, no logging,
/// no clock: the caller passes <c>now</c>.
/// </summary>
public static class StartupApprovedState
{
    /// <summary>The length of every value this type writes.</summary>
    public const int Length = 12;

    /// <summary>
    /// <see langword="true"/> unless the value says "disabled": the low bit of the first little-endian DWORD is set
    /// AND a non-zero 8-byte timestamp follows at offset 4. <see langword="null"/> or fewer than 4 bytes (no entry —
    /// Task Manager never touched it) is enabled. Tolerant by design: never throws.
    /// </summary>
    public static bool IsEnabled(byte[]? data)
    {
        if (data is null || data.Length < 4)
        {
            return true;
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data);
        bool hasTimestamp = data.Length >= Length && BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(4)) != 0;
        return (flags & 1) == 0 || !hasTimestamp;
    }

    /// <summary>
    /// A fresh 12-byte value: enabled -> <c>02</c> + zeros; disabled -> <c>03</c> + <paramref name="now"/> as a
    /// little-endian FILETIME at offset 4 (the layout Task Manager itself writes).
    /// </summary>
    public static byte[] Encode(bool enabled, DateTimeOffset now)
    {
        var data = new byte[Length];
        if (enabled)
        {
            data[0] = 0x02;
            return data;
        }

        data[0] = 0x03;
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4), now.UtcDateTime.ToFileTimeUtc());
        return data;
    }
}
