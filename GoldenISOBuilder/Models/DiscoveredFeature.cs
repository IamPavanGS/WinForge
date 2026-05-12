namespace GoldenISOBuilder.Models;

/// <summary>
/// A Windows optional feature discovered by DISM /Get-Features from the ISO's
/// install.wim — used for dynamic feature enable/disable selection in Step 3.
/// </summary>
public class DiscoveredFeature
{
    public string FeatureName { get; set; } = "";

    /// <summary>As returned by DISM: "Enabled", "Disabled", "EnablePending", etc.</summary>
    public string State       { get; set; } = "";

    public bool IsEnabled => State.Equals("Enabled",        StringComparison.OrdinalIgnoreCase)
                          || State.Equals("EnablePending",  StringComparison.OrdinalIgnoreCase);
}
