using System.IO;
using System.Text.Json;

namespace GoldenISOBuilder.Services;

public class BuildRecord
{
    public DateTime CompletedAt     { get; set; }
    public bool     Success         { get; set; }
    public double   DurationSeconds { get; set; }
    public string   EditionName     { get; set; } = "";
    public string   IsoPath         { get; set; } = "";
}

/// <summary>
/// Persists a rolling list of build records to
/// %LOCALAPPDATA%\GoldenISOBuilder\build_history.json.
/// All methods are safe to call from any thread and swallow I/O errors
/// so a disk issue never crashes the build pipeline.
/// </summary>
public static class BuildHistoryStore
{
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "GoldenISOBuilder");

    private static readonly string FilePath =
        Path.Combine(DataDir, "build_history.json");

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true };

    /// <summary>Returns all stored records, newest last. Empty list on any error.</summary>
    public static List<BuildRecord> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<BuildRecord>>(json) ?? [];
        }
        catch { return []; }
    }

    /// <summary>Deletes all stored build records.</summary>
    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { /* non-fatal */ }
    }

    /// <summary>Appends one record and rewrites the file. Keeps the last 200 entries.</summary>
    public static void Append(BuildRecord record)
    {
        try
        {
            var records = Load();
            records.Add(record);
            if (records.Count > 200)
                records = records.Skip(records.Count - 200).ToList();
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(records, JsonOpts));
        }
        catch { /* non-fatal */ }
    }
}
