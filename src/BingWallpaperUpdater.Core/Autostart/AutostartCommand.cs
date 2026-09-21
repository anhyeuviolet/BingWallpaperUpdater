namespace BingWallpaperUpdater.Core.Autostart;

/// <summary>
/// The single producer of the HKCU Run command line (INST-02, D-12): the absolute exe path wrapped in double quotes,
/// a space and <see cref="Flag"/>. Quoting closes the unquoted-path hijack (T-03-12); a relative path or a path
/// containing a double quote is rejected rather than "fixed". Phase 4's Inno Setup <c>[Registry]</c> entry must emit
/// the byte-identical string. No I/O, no logging.
/// </summary>
public static class AutostartCommand
{
    /// <summary>The flag <c>Program.Main</c> parses (case-insensitively) to delay the first tick 30-60 s after logon (D-12).</summary>
    public const string Flag = "--startup";

    /// <summary><c>"&lt;exePath&gt;" --startup</c>.</summary>
    /// <exception cref="ArgumentException">Null / whitespace, a relative path, or a path containing a double quote.</exception>
    public static string For(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (!Path.IsPathRooted(exePath))
        {
            throw new ArgumentException("The exe path must be absolute.", nameof(exePath));
        }

        if (exePath.Contains('"'))
        {
            throw new ArgumentException("The exe path must not contain a double quote.", nameof(exePath));
        }

        return "\"" + exePath + "\" " + Flag;
    }
}
