using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using GoldenISOBuilder.Models;

namespace GoldenISOBuilder.Services;

public class IsoAnalysisResult
{
    public bool                   Success       { get; set; }
    public string?                Error         { get; set; }
    public List<WindowsImageInfo> Images        { get; set; } = [];
    public List<string>           Architectures { get; set; } = [];
    public string                 WindowsVersion{ get; set; } = "";
    public string                 MountedDrive  { get; set; } = "";
    /// <summary>Primary BCP-47 boot language read from sources\lang.ini (e.g. "en-GB").
    /// Empty string when detection failed.</summary>
    public string                 BootLanguage  { get; set; } = "";
    /// <summary>All BCP-47 language codes listed under [Available UI Languages] in lang.ini.
    /// These are the ONLY valid choices for the windowsPE UILanguage field.</summary>
    public List<string>           AvailableBootLanguages { get; set; } = [];
}

public class IsoAnalyzer
{
    // ── Public API ───────────────────────────────────────────────────────────

    public async Task<IsoAnalysisResult> AnalyzeAsync(string isoPath, Action<string>? status = null)
    {
        string mountedDrive = "";
        try
        {
            status?.Invoke("Mounting ISO…");
            mountedDrive = await MountIsoAsync(isoPath);

            status?.Invoke("Locating image file…");
            var wimPath = FindWimPath(mountedDrive);

            status?.Invoke("Reading Windows editions…");
            var images = await GetWimImagesAsync(wimPath, status);

            status?.Invoke("Detecting architecture…");
            var archs = DetectArchitectures(mountedDrive);

            status?.Invoke("Detecting boot language…");
            var bootLangs = ReadBootLanguagesFromLangIni(mountedDrive);

            return new IsoAnalysisResult
            {
                Success               = true,
                Images                = images,
                Architectures         = archs,
                WindowsVersion        = DetectWindowsVersion(images),
                MountedDrive          = mountedDrive,
                BootLanguage          = bootLangs.FirstOrDefault() ?? "",
                AvailableBootLanguages = bootLangs
            };
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(mountedDrive))
                await TryDismountByDrive(mountedDrive);

            return new IsoAnalysisResult { Success = false, Error = ex.Message };
        }
    }

    public static async Task DismountAsync(string isoPath)
    {
        await RunSilentAsync("powershell.exe",
            $"-NonInteractive -Command \"Dismount-DiskImage -ImagePath '{EscapePs(isoPath)}'\"");
    }

    // ── Mount / Dismount ─────────────────────────────────────────────────────

    private static async Task<string> MountIsoAsync(string isoPath)
    {
        var before = ReadyDriveLetters();

        var (_, err, exit) = await RunCapturedAsync("powershell.exe",
            $"-NonInteractive -Command \"Mount-DiskImage -ImagePath '{EscapePs(isoPath)}'\"");

        if (exit != 0 && !string.IsNullOrWhiteSpace(err))
            throw new Exception($"Mount-DiskImage failed: {err.Trim()}");

        // Wait up to 15 s for a new drive letter
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            var after = ReadyDriveLetters();
            char newDrive = after.Except(before).FirstOrDefault();
            if (newDrive != default)
                return $"{newDrive}:";
        }

        throw new TimeoutException("ISO was mounted but no new drive letter appeared after 15 s.");
    }

    private static async Task TryDismountByDrive(string drive)
    {
        try
        {
            await RunSilentAsync("powershell.exe",
                $"-NonInteractive -Command \"" +
                $"$img = Get-DiskImage -DevicePath (Get-Volume -DriveLetter '{drive[0]}').UniqueId; " +
                $"Dismount-DiskImage -ImagePath $img.ImagePath\"");
        }
        catch { /* best-effort */ }
    }

    private static HashSet<char> ReadyDriveLetters() =>
        DriveInfo.GetDrives()
                 .Where(d => d.IsReady)
                 .Select(d => d.Name[0])
                 .ToHashSet();

    // ── WIM parsing ──────────────────────────────────────────────────────────

    private static string FindWimPath(string drive)
    {
        var sources = Path.Combine(drive + Path.DirectorySeparatorChar.ToString(), "sources");
        foreach (var name in new[] { "install.wim", "install.esd" })
        {
            var path = Path.Combine(sources, name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(
            "Cannot find sources\\install.wim or sources\\install.esd. " +
            "Ensure this is a genuine Windows 10/11 installation ISO.");
    }

    private static async Task<List<WindowsImageInfo>> GetWimImagesAsync(
        string wimPath, Action<string>? status)
    {
        var dismExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");

        if (!File.Exists(dismExe))
            throw new FileNotFoundException("dism.exe not found in System32.");

        status?.Invoke("Running DISM (this may take a moment)…");

        var (output, err, exit) = await RunCapturedAsync(
            dismExe, $"/Get-WimInfo /WimFile:\"{wimPath}\"");

        // DISM exits 0 on success; non-zero but with output can still be parseable
        if (string.IsNullOrWhiteSpace(output))
            throw new Exception(
                $"DISM returned no output (exit {exit}).\n{err?.Trim()}\n\n" +
                "Try running the application as Administrator.");

        var images = ParseDismOutput(output);
        if (images.Count == 0)
            throw new Exception(
                $"DISM ran but no image entries were found.\n\nDISM output:\n{output.Trim()}");

        return images;
    }

    private static List<WindowsImageInfo> ParseDismOutput(string output)
    {
        var images = new List<WindowsImageInfo>();

        // Directly match every "Index : N … Name : <text>" pair.
        // This is robust to blank lines, header text, and CRLF vs LF differences.
        var indexMatches = Regex.Matches(output, @"Index\s*:\s*(\d+)");

        foreach (Match idxM in indexMatches)
        {
            int idx = int.Parse(idxM.Groups[1].Value);

            // Grab the text that follows this Index line up to the next Index line (or end)
            int start = idxM.Index + idxM.Length;
            int nextIdx = output.IndexOf("\nIndex", start, StringComparison.Ordinal);
            string block = nextIdx >= 0 ? output[start..nextIdx] : output[start..];

            var nameM = Regex.Match(block, @"Name\s*:\s*(.+)");
            var sizeM = Regex.Match(block, @"Size\s*:\s*([\d,]+)\s*bytes");

            var name = nameM.Success ? nameM.Groups[1].Value.Trim() : $"Image {idx}";
            long.TryParse(
                sizeM.Success ? sizeM.Groups[1].Value.Replace(",", "") : "0",
                out long size);

            images.Add(new WindowsImageInfo
            {
                Index      = idx,
                Name       = name,
                EditionKey = ClassifyEdition(name),
                SizeBytes  = size,
            });
        }

        return images.OrderBy(i => i.Index).ToList();
    }

    private static string ClassifyEdition(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("enterprise")) return "Enterprise";
        if (n.Contains("education"))  return "Education";
        if (n.Contains("pro"))        return "Pro";
        if (n.Contains("home"))       return "Home";
        return "Other";
    }

    // ── Architecture detection ───────────────────────────────────────────────

    private static List<string> DetectArchitectures(string drive)
    {
        var archs = new List<string>();
        var efi   = Path.Combine(drive + Path.DirectorySeparatorChar.ToString(), "efi", "boot");

        if (Directory.Exists(efi))
        {
            if (File.Exists(Path.Combine(efi, "bootx64.efi")))   archs.Add("x64");
            if (File.Exists(Path.Combine(efi, "bootia32.efi")))  archs.Add("x86");
            if (File.Exists(Path.Combine(efi, "bootaa64.efi")))  archs.Add("ARM64");
        }

        // Legacy fallback: look for setup.exe bitness via sources folder name heuristic
        if (archs.Count == 0)
        {
            var sources = Path.Combine(drive + Path.DirectorySeparatorChar.ToString(), "sources");
            if (Directory.Exists(sources)) archs.Add("x64"); // overwhelmingly most common
        }

        return archs.Count > 0 ? archs : ["x64"];
    }

    private static string DetectWindowsVersion(List<WindowsImageInfo> images)
    {
        var first = images.FirstOrDefault()?.Name ?? "";
        if (first.Contains("11")) return "Windows 11";
        if (first.Contains("10")) return "Windows 10";
        return "Windows";
    }

    // ── Boot language detection ──────────────────────────────────────────────

    /// <summary>
    /// Reads sources\lang.ini from the already-mounted ISO drive and returns ALL
    /// language codes listed under [Available UI Languages] (e.g. ["en-GB"]).
    /// Most ISOs have exactly one language; multi-language ISOs may have several.
    /// Returns an empty list if the file is missing or unparseable.
    /// </summary>
    private static List<string> ReadBootLanguagesFromLangIni(string drive)
    {
        var result = new List<string>();
        try
        {
            // Always use a rooted path — "E:" alone is drive-relative in .NET,
            // Path.Combine("E:", "sources") → "E:sources" not "E:\sources".
            var root    = drive.TrimEnd('\\', '/') + "\\";
            var langIni = Path.Combine(root, "sources", "lang.ini");
            if (!File.Exists(langIni)) return result;

            // File format:
            //   [Available UI Languages]
            //   en-GB = 3
            //   [Fallback Languages]
            //   en-US = en-GB
            bool inSection = false;
            foreach (var raw in File.ReadAllLines(langIni))
            {
                var line = raw.Trim();
                if (line.Equals("[Available UI Languages]", StringComparison.OrdinalIgnoreCase))
                    { inSection = true; continue; }
                if (line.StartsWith("["))
                    { if (inSection) break; continue; }
                if (!inSection || !line.Contains('=')) continue;

                var code = line.Split('=')[0].Trim();
                if (!string.IsNullOrEmpty(code)) result.Add(code);
            }
        }
        catch { /* best-effort — never crash analysis over a missing lang.ini */ }
        return result;
    }

    // ── Process helpers ──────────────────────────────────────────────────────

    private static async Task<(string stdout, string stderr, int exit)> RunCapturedAsync(
        string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi) ?? throw new Exception($"Failed to start {exe}");
        // Read both streams concurrently to prevent pipe-buffer deadlock
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (stdout, stderr, proc.ExitCode);
    }

    private static async Task RunSilentAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false };
        using var proc = Process.Start(psi);
        if (proc != null) await proc.WaitForExitAsync();
    }

    private static string EscapePs(string path) => path.Replace("'", "''");
}
