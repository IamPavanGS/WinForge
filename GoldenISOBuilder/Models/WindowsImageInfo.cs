namespace GoldenISOBuilder.Models;

public class WindowsImageInfo
{
    public int    Index      { get; set; }
    public string Name       { get; set; } = "";
    public string EditionKey { get; set; } = "";   // "Home" | "Pro" | "Enterprise" | "Education" | "Other"
    public long   SizeBytes  { get; set; }

    public string SizeDisplay => SizeBytes > 0
        ? $"{SizeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB (uncompressed)"
        : "";
}
