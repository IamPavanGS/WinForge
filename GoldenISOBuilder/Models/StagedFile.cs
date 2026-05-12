namespace GoldenISOBuilder.Models;

public class StagedFile
{
    public string SourcePath { get; set; } = "";

    /// <summary>
    /// Destination folder INSIDE the mounted image, relative to the image root.
    /// Examples: "Users\Public\Desktop", "Windows\System32", "ProgramData\MyCompany"
    /// Do NOT include a leading backslash.
    /// </summary>
    public string DestinationFolder { get; set; } = @"Users\Public\Desktop";
}
