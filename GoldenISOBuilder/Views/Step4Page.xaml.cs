using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GoldenISOBuilder.Models;

namespace GoldenISOBuilder.Views;

public partial class Step4Page : UserControl
{
    public event Action<string, int>? NavigateRequested;

    private bool _showingPassword = false;
    private TextBox? _clearTextBox;

    // Strength colours
    private static readonly SolidColorBrush ColWeak   = new(Color.FromRgb(0xF0, 0x52, 0x60)); // red
    private static readonly SolidColorBrush ColFair   = new(Color.FromRgb(0xF5, 0x9E, 0x0B)); // amber
    private static readonly SolidColorBrush ColGood   = new(Color.FromRgb(0x38, 0xBD, 0xF8)); // sky blue
    private static readonly SolidColorBrush ColStrong = new(Color.FromRgb(0x27, 0xC4, 0x8A)); // teal-green
    private static readonly SolidColorBrush ColEmpty  = new(Color.FromRgb(0x17, 0x2B, 0x46)); // BG3

    public Step4Page()
    {
        InitializeComponent();
        Loaded           += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    // Re-check wallpaper every time the user navigates back to this page.
    // Loaded only fires once; IsVisibleChanged fires on every visit.
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && IsLoaded)
            RefreshLoginPreviewWallpaper();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Restore saved values
        var s = BuildSession.Current;
        if (!string.IsNullOrEmpty(s.AdminUsername)) UsernameBox.Text = s.AdminUsername;
        if (!string.IsNullOrEmpty(s.AdminPassword))
        {
            PasswordBox.Password        = s.AdminPassword;
            ConfirmPasswordBox.Password = s.AdminPassword;
        }
        AutoLoginToggle.IsChecked = s.AutoLoginEnabled;
        PasswordNeverExpiresToggle.IsChecked = s.PasswordNeverExpires;

        AutoLoginToggle.Checked   += (_, _) => RefreshPreview();
        AutoLoginToggle.Unchecked += (_, _) => RefreshPreview();
        PasswordNeverExpiresToggle.Checked   += (_, _) => RefreshPreview();
        PasswordNeverExpiresToggle.Unchecked += (_, _) => RefreshPreview();

        PreviewUsername.Text = string.IsNullOrWhiteSpace(UsernameBox.Text) ? "Administrator" : UsernameBox.Text;
        UpdateStrength(PasswordBox.Password);
        RefreshPreview();
        RefreshLoginPreviewWallpaper();
    }

    // ── Login preview wallpaper (cosmetic only) ───────────────────────────────

    private void RefreshLoginPreviewWallpaper()
    {
        var path = BuildSession.Current.WallpaperPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource    = new Uri(path, UriKind.Absolute);
                bi.CacheOption  = BitmapCacheOption.OnLoad;
                bi.EndInit();

                LoginBg.Background      = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
                LoginOverlay.Visibility = Visibility.Visible;
            }
            catch
            {
                // Image failed to load (bad format, locked file, etc.)
                // Restore solid dark background so the preview doesn't look broken.
                LoginBg.Background      = new SolidColorBrush(Color.FromRgb(0x07, 0x0E, 0x1C));
                LoginOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ── Password strength ─────────────────────────────────────────────────────

    private void PasswordBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateStrength(PasswordBox.Password);
        UpdateConfirmMismatch();
        RefreshPreview();
    }

    private void ConfirmPassword_Changed(object sender, RoutedEventArgs e)
        => UpdateConfirmMismatch();

    private void Username_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PreviewUsername != null)
            PreviewUsername.Text = string.IsNullOrWhiteSpace(UsernameBox.Text) ? "Administrator" : UsernameBox.Text;
        RefreshPreview();
    }

    private void UpdateConfirmMismatch()
    {
        if (PasswordMismatch == null) return;
        bool mismatch = !string.IsNullOrEmpty(ConfirmPasswordBox.Password)
                        && PasswordBox.Password != ConfirmPasswordBox.Password;
        PasswordMismatch.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStrength(string pwd)
    {
        int score = CalcStrength(pwd);
        var bars  = new[] { StrBar1, StrBar2, StrBar3, StrBar4 };
        SolidColorBrush col;
        string label;

        switch (score)
        {
            case 0:
                col = ColEmpty; label = "";
                break;
            case 1:
                col = ColWeak; label = "Very Weak";
                break;
            case 2:
                col = ColFair; label = "Weak";
                break;
            case 3:
                col = ColGood; label = "Good";
                break;
            default:
                col = ColStrong; label = "Strong";
                break;
        }

        for (int i = 0; i < bars.Length; i++)
            bars[i].Background = i < score ? col : ColEmpty;

        StrengthLabel.Text       = label;
        StrengthLabel.Foreground = score == 0 ? ColEmpty : col;
    }

    private static int CalcStrength(string pwd)
    {
        if (string.IsNullOrEmpty(pwd)) return 0;
        int score = 0;
        if (pwd.Length >= 4)  score++;
        if (pwd.Length >= 8)  score++;
        if (pwd.Length >= 12) score++;
        if (pwd.Any(char.IsUpper) && pwd.Any(char.IsLower)) score++;
        if (pwd.Any(char.IsDigit)) score++;
        if (pwd.Any(c => !char.IsLetterOrDigit(c))) score++;
        return Math.Clamp(score / 2 + (score >= 5 ? 1 : 0), 0, 4);
    }

    // ── Generate password ─────────────────────────────────────────────────────

    private void GeneratePassword_Click(object sender, RoutedEventArgs e)
    {
        const string chars = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789!@#$%^&*";
        var bytes = RandomNumberGenerator.GetBytes(16);
        var sb    = new StringBuilder(16);
        foreach (var b in bytes)
            sb.Append(chars[b % chars.Length]);
        string generated = sb.ToString();

        PasswordBox.Password = generated;
        ConfirmPasswordBox.Password = generated;

        if (_showingPassword && _clearTextBox != null)
            _clearTextBox.Text = generated;

        UpdateStrength(generated);
    }

    // ── Show / hide password ──────────────────────────────────────────────────

    private void TogglePasswordVisibility_Click(object sender, RoutedEventArgs e)
    {
        _showingPassword = !_showingPassword;

        if (_showingPassword)
        {
            // Create overlay textbox showing the password in plain text
            _clearTextBox ??= new TextBox
            {
                Style    = (Style?)Application.Current.Resources["TextInputStyle"],
                FontFamily = new FontFamily("Consolas"),
                IsReadOnly = true
            };
            _clearTextBox.Text = PasswordBox.Password;

            // Swap PasswordBox for TextBox in same parent Grid
            var grid = (Grid)PasswordBox.Parent;
            grid.Children.Remove(PasswordBox);
            grid.Children.Insert(0, _clearTextBox);
        }
        else
        {
            if (_clearTextBox != null)
            {
                var grid = (Grid)_clearTextBox.Parent;
                grid.Children.Remove(_clearTextBox);
                grid.Children.Insert(0, PasswordBox);
            }
        }
    }

    // ── Copy XML ──────────────────────────────────────────────────────────────

    private void CopyXml_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(BuildUnattendXml()); }
        catch { /* clipboard not available */ }
    }

    private string BuildUnattendXml()
    {
        string user = UsernameBox.Text.Trim().Length > 0 ? UsernameBox.Text.Trim() : "Administrator";
        return $"""
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend">
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-Shell-Setup"
               processorArchitecture="amd64"
               publicKeyToken="31bf3856ad364e35"
               language="neutral" versionScope="nonSxS">
      <UserAccounts>
        <AdministratorPassword>
          <Value>[REDACTED]</Value>
          <PlainText>true</PlainText>
        </AdministratorPassword>
        <LocalAccounts>
          <LocalAccount wcm:action="add">
            <Password><Value>[REDACTED]</Value><PlainText>true</PlainText></Password>
            <Name>{user}</Name>
            <Group>Administrators</Group>
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <SkipMachineOOBE>true</SkipMachineOOBE>
        <SkipUserOOBE>true</SkipUserOOBE>
      </OOBE>
    </component>
  </settings>
</unattend>
""";
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void SaveToSession()
    {
        var s = BuildSession.Current;
        s.AdminUsername    = UsernameBox.Text.Trim().Length > 0 ? UsernameBox.Text.Trim() : "Administrator";
        s.AdminPassword    = _showingPassword && _clearTextBox != null
                              ? _clearTextBox.Text
                              : PasswordBox.Password;
        s.AutoLoginEnabled = AutoLoginToggle.IsChecked == true;
        s.PasswordNeverExpires = PasswordNeverExpiresToggle.IsChecked == true;
    }

    private void RefreshPreview()
    {
        if (UnattendPreview == null) return;
        UnattendPreview.Text = BuildPreviewXml();
    }

    private string BuildPreviewXml()
    {
        string user  = string.IsNullOrWhiteSpace(UsernameBox.Text) ? "Administrator" : UsernameBox.Text.Trim();
        string pwdMask = string.IsNullOrEmpty(PasswordBox.Password) ? "[NOT SET]" : "[REDACTED]";
        bool   auto  = AutoLoginToggle.IsChecked == true;
        bool   noExp = PasswordNeverExpiresToggle.IsChecked == true;

        return
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<unattend xmlns=""urn:schemas-microsoft-com:unattend"">
  <settings pass=""oobeSystem"">
    <component name=""Microsoft-Windows-Shell-Setup"">
      <UserAccounts>
        <AdministratorPassword>
          <Value>{pwdMask}</Value>
          <PlainText>true</PlainText>
        </AdministratorPassword>
        <LocalAccounts>
          <LocalAccount wcm:action=""add"">
            <Name>{user}</Name>
            <Group>Administrators</Group>
            <Password><Value>{pwdMask}</Value><PlainText>true</PlainText></Password>
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
{(auto ? $@"      <AutoLogon>
        <Username>{user}</Username>
        <Enabled>true</Enabled>
        <LogonCount>1</LogonCount>
      </AutoLogon>" : "")}
      <OOBE>
        <SkipMachineOOBE>true</SkipMachineOOBE>
        <SkipUserOOBE>true</SkipUserOOBE>
        <HideEULAPage>true</HideEULAPage>
      </OOBE>
    </component>
  </settings>
</unattend>
{(noExp ? "<!-- net accounts /maxpwage:unlimited applied via SetupComplete.cmd -->" : "")}";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SaveToSession();
        NavigateRequested?.Invoke("wizard", 2);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        // Validate password match
        if (!string.IsNullOrEmpty(PasswordBox.Password) &&
            PasswordBox.Password != ConfirmPasswordBox.Password)
        {
            AppDialog.Alert(this,
                "The two passwords don't match. Please correct before continuing.",
                "Password mismatch", AppDialogIcon.Warning);
            ConfirmPasswordBox.Focus();
            return;
        }

        // Auto-logon requires a non-empty password — Windows silently disables
        // auto-logon if DefaultPassword is blank, which is a common foot-gun.
        // Block here with a clear message so the user knows why.
        bool autoLoginOn = AutoLoginToggle.IsChecked == true;
        if (autoLoginOn && string.IsNullOrEmpty(PasswordBox.Password))
        {
            AppDialog.Alert(this,
                "Auto-logon is enabled but no admin password is set.\n\n" +
                "Windows requires a password for auto-logon to work — without one, " +
                "the deployed machine will show the lock screen at first boot " +
                "instead of logging in automatically.\n\n" +
                "Either set a password below, or disable auto-logon.",
                "Auto-logon needs a password", AppDialogIcon.Warning);
            PasswordBox.Focus();
            return;
        }

        SaveToSession();
        NavigateRequested?.Invoke("wizard", 4);
    }
}
