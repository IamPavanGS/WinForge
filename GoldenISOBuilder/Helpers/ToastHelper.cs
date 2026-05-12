using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace GoldenISOBuilder.Helpers;

/// <summary>
/// Shows Windows Action-Center toast notifications from an unpackaged WPF app.
///
/// How it works:
///   Windows requires every toast to come from a known App User Model ID (AUMID).
///   Packaged apps get one automatically; unpackaged apps must register in the registry.
///   <see cref="Initialize"/> does that registration once per session, then every
///   <see cref="Show"/> call fires a real Windows notification — it appears in the
///   bottom-right corner and lands in Notification Center even when the app is
///   minimised or the user has switched to another window.
/// </summary>
public static class ToastHelper
{
    private const string AumId = "ALE.GoldenISOBuilder";
    private static bool _ready;

    // ── One-time setup — call from App.OnStartup ──────────────────────────────

    public static void Initialize()
    {
        try
        {
            // Register the AUMID so Windows knows the display name and icon to use
            // when the toast appears in the Action Center.
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"SOFTWARE\Classes\AppUserModelId\{AumId}");
            if (key != null)
            {
                key.SetValue("DisplayName",   "ALE Image Forge");
                // Point the icon at our own EXE so the notification badge looks right
                string exe = System.Diagnostics.Process.GetCurrentProcess()
                                 .MainModule?.FileName ?? "";
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue("IconUri", exe);
            }
            _ready = true;
        }
        catch { /* notifications gracefully silent if registry write fails */ }
    }

    // ── Show a toast ──────────────────────────────────────────────────────────

    /// <summary>Shows a Windows toast with a title, body line, and optional icon glyph.</summary>
    public static void Show(
        string title,
        string body,
        ToastKind kind = ToastKind.Info)
    {
        if (!_ready) return;
        try
        {
            // The attribution line (small grey text at the bottom) names the app.
            // The scenario="reminder" keeps the toast visible a bit longer.
            string xml = $@"
<toast scenario=""default"">
  <visual>
    <binding template=""ToastGeneric"">
      <text>{Esc(title)}</text>
      <text>{Esc(body)}</text>
      <text placement=""attribution"">ALE Image Forge</text>
    </binding>
  </visual>
  <audio src=""ms-winsoundevent:Notification.Default"" silent=""{(kind == ToastKind.Failure ? "false" : "true")}""/>
</toast>";

            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var notifier = ToastNotificationManager.CreateToastNotifier(AumId);

            // Respect the user's "Do Not Disturb" / notification-blocked state —
            // if the notifier reports it can't show, skip silently.
            if (notifier.Setting != NotificationSetting.Enabled) return;

            notifier.Show(new ToastNotification(doc));
        }
        catch { /* toast failures are non-critical */ }
    }

    private static string Esc(string s) =>
        System.Security.SecurityElement.Escape(s ?? "") ?? "";
}

public enum ToastKind { Info, Success, Failure }
