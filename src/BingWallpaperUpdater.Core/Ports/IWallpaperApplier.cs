namespace BingWallpaperUpdater.Core.Ports;

/// <summary>
/// Port implemented by the Windows adapter (<c>DesktopWallpaperApplier</c>). Core never touches Win32.
/// </summary>
public interface IWallpaperApplier
{
    /// <summary>Sets <paramref name="absolutePath"/> on all monitors with Fill and reads the result back.</summary>
    ApplyResult Apply(string absolutePath);
}

/// <param name="Ok">True when the API accepted the path (a read-back mismatch is logged, not a failure).</param>
/// <param name="Method">"com" or "spi".</param>
/// <param name="ReadBackPath">What the shell reports after the apply, or null.</param>
/// <param name="Position">e.g. "DWPOS_FILL" for COM; null for SPI.</param>
public sealed record ApplyResult(bool Ok, string Method, string? ReadBackPath, string? Position, string? Error);
