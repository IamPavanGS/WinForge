namespace GoldenISOBuilder.Models;

/// <summary>
/// A custom font file (.ttf / .otf / .ttc) staged into <c>\Windows\Fonts</c>
/// inside the WIM and registered under
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts</c> so Windows
/// picks it up at next boot. Registry value name format Windows expects:
///   <c>"&lt;Display Name&gt; (TrueType)"</c> → <c>"&lt;filename&gt;.ttf"</c>
/// </summary>
public sealed class FontEntry
{
    /// <summary>Absolute path on the build host to the font file.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Friendly font name as it appears in the registry value name.
    /// Auto-derived from the filename when an entry is created; the user
    /// can override before save (some pro fonts have funny filenames but
    /// expect a specific display name e.g. <c>"Hyundai Sans Head"</c>).</summary>
    public string DisplayName { get; set; } = "";
}
