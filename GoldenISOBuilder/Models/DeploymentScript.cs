namespace GoldenISOBuilder.Models;

public class DeploymentScript
{
    /// <summary>Full host-side path to the script file.</summary>
    public string Path    { get; set; } = "";

    /// <summary>
    /// How the script is wired up inside the image.
    /// Use the <see cref="DeploymentTrigger"/> constants.
    /// </summary>
    public string Trigger { get; set; } = DeploymentTrigger.DeployOnly;
}

/// <summary>
/// Well-known trigger identifiers for <see cref="DeploymentScript.Trigger"/>.
/// Add new entries here to extend the UI dropdown automatically.
/// </summary>
public static class DeploymentTrigger
{
    /// <summary>Just copies the file to Public\Documents — no auto-run.</summary>
    public const string DeployOnly = "DeployOnly";

    /// <summary>
    /// Copies to Public\Documents and places the script (or a PS1 launcher)
    /// in C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup so it
    /// runs for every user on every login.
    /// </summary>
    public const string EveryLogin = "EveryLogin";

    /// <summary>
    /// All supported triggers in display order.
    /// Each entry is (serialised value, human-readable label).
    /// </summary>
    public static readonly (string Value, string Label)[] All =
    [
        (DeployOnly, "Deploy Only"),
        (EveryLogin, "Every Login"),
    ];
}
