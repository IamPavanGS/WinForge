using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GoldenISOBuilder.Models;
using Microsoft.Win32;

namespace GoldenISOBuilder.Views;

public partial class StepRegistryPage : UserControl
{
    public event Action<string, int>? NavigateRequested;

    private readonly List<RegistryEntry> _entries = [];
    private int _editingIndex = -1; // -1 = adding new, ≥0 = editing existing

    public StepRegistryPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            foreach (var e in BuildSession.Current.CustomRegistryEntries)
                _entries.Add(e);
            RefreshList();
        };
    }

    // ── Add / Edit form ───────────────────────────────────────────────────────

    private void AddEntry_Click(object sender, RoutedEventArgs e)
    {
        _editingIndex = -1;
        ClearForm();
        FormTitle.Text       = "Add Registry Entry";
        EntryForm.Visibility = Visibility.Visible;
    }

    private void EditEntry(int index)
    {
        _editingIndex = index;
        var entry = _entries[index];
        FormHive.SelectedIndex      = entry.Hive == "HKCU" ? 1 : 0;
        FormKeyPath.Text            = entry.KeyPath;
        FormValueName.Text          = entry.ValueName;
        FormData.Text               = entry.Data;
        FormOperation.SelectedIndex = entry.Operation == "DELETE" ? 1 : 0;
        SetFormType(entry.Type);
        FormTitle.Text       = "Edit Registry Entry";
        EntryForm.Visibility = Visibility.Visible;
    }

    private void FormSave_Click(object sender, RoutedEventArgs e)
    {
        string keyPath = FormKeyPath.Text.Trim();
        if (string.IsNullOrEmpty(keyPath))
        {
            FormKeyPath.BorderBrush = (Brush)Application.Current.Resources["ErrBrush"];
            return;
        }
        FormKeyPath.ClearValue(TextBox.BorderBrushProperty);

        var entry = new RegistryEntry
        {
            Hive      = (FormHive.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "HKLM",
            KeyPath   = keyPath,
            ValueName = FormValueName.Text.Trim(),
            Type      = (FormType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "REG_SZ",
            Data      = FormData.Text.Trim(),
            Operation = (FormOperation.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "SET"
        };

        if (_editingIndex >= 0 && _editingIndex < _entries.Count)
            _entries[_editingIndex] = entry;
        else
            _entries.Add(entry);

        SaveToSession();
        EntryForm.Visibility = Visibility.Collapsed;
        RefreshList();
    }

    private void FormCancel_Click(object sender, RoutedEventArgs e)
    {
        EntryForm.Visibility = Visibility.Collapsed;
        ClearForm();
    }

    private void ClearForm()
    {
        FormHive.SelectedIndex      = 0;
        FormKeyPath.Text            = "";
        FormValueName.Text          = "";
        FormType.SelectedIndex      = 0;
        FormData.Text               = "";
        FormOperation.SelectedIndex = 0;
        FormKeyPath.ClearValue(TextBox.BorderBrushProperty);
    }

    private void SetFormType(string type)
    {
        var types = new[] { "REG_SZ", "REG_DWORD", "REG_QWORD", "REG_BINARY", "REG_MULTI_SZ", "REG_EXPAND_SZ" };
        FormType.SelectedIndex = Math.Max(0, Array.IndexOf(types, type));
    }

    // ── Dynamic list ──────────────────────────────────────────────────────────

    private void RefreshList()
    {
        CustomEntriesPanel.Children.Clear();
        NoEntriesHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _entries.Count; i++)
            CustomEntriesPanel.Children.Add(BuildEntryRow(_entries[i], i));
    }

    private UIElement BuildEntryRow(RegistryEntry entry, int index)
    {
        var res      = Application.Current.Resources;
        var gold1    = (Color)res["Gold1Color"];
        var errColor = (Color)res["ErrColor"];
        var okColor  = (Color)res["OkColor"];

        var row = new Border
        {
            Background      = (Brush)res["BG2Brush"],
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush     = (Brush)res["LineBrush"],
            Padding         = new Thickness(12, 8, 10, 8),
            Margin          = new Thickness(0, 0, 0, 4)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        // Hive badge
        var hiveBorder = new Border
        {
            Background    = entry.Hive == "HKCU"
                ? new SolidColorBrush(Color.FromArgb(40, gold1.R, gold1.G, gold1.B))
                : (Brush)res["BG3Brush"],
            CornerRadius  = new CornerRadius(3),
            Padding       = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        hiveBorder.Child = new TextBlock
        {
            Text       = entry.Hive,
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = entry.Hive == "HKCU" ? (Brush)res["Gold1Brush"] : (Brush)res["FG2Brush"]
        };
        Grid.SetColumn(hiveBorder, 0);

        // Key path
        var keyTb = new TextBlock
        {
            Text             = entry.KeyPath,
            FontFamily       = new FontFamily("Consolas"),
            FontSize         = 10,
            Foreground       = (Brush)res["FG0Brush"],
            TextTrimming     = TextTrimming.CharacterEllipsis,
            VerticalAlignment= VerticalAlignment.Center,
            Margin           = new Thickness(0, 0, 8, 0),
            ToolTip          = entry.KeyPath
        };
        Grid.SetColumn(keyTb, 1);

        // Value name
        var nameTb = new TextBlock
        {
            Text             = entry.ValueName,
            FontFamily       = new FontFamily("Consolas"),
            FontSize         = 11,
            Foreground       = (Brush)res["FG2Brush"],
            VerticalAlignment= VerticalAlignment.Center,
            TextTrimming     = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(nameTb, 2);

        // Type
        var typeTb = new TextBlock
        {
            Text             = entry.Type,
            FontFamily       = new FontFamily("Consolas"),
            FontSize         = 10,
            Foreground       = (Brush)res["FG2Brush"],
            VerticalAlignment= VerticalAlignment.Center
        };
        Grid.SetColumn(typeTb, 3);

        // Data
        var dataTb = new TextBlock
        {
            Text             = entry.Data,
            FontFamily       = new FontFamily("Consolas"),
            FontSize         = 11,
            Foreground       = (Brush)res["FG0Brush"],
            TextTrimming     = TextTrimming.CharacterEllipsis,
            VerticalAlignment= VerticalAlignment.Center,
            ToolTip          = entry.Data
        };
        Grid.SetColumn(dataTb, 4);

        // Op + action buttons
        var actionPanel = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var opBorder = new Border
        {
            Background   = entry.Operation == "DELETE"
                ? new SolidColorBrush(Color.FromArgb(40, errColor.R, errColor.G, errColor.B))
                : new SolidColorBrush(Color.FromArgb(40, okColor.R, okColor.G, okColor.B)),
            CornerRadius = new CornerRadius(3),
            Padding      = new Thickness(5, 2, 5, 2),
            Margin       = new Thickness(0, 0, 6, 0)
        };
        opBorder.Child = new TextBlock
        {
            Text       = entry.Operation,
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = entry.Operation == "DELETE" ? (Brush)res["ErrBrush"] : (Brush)res["OkBrush"]
        };

        var editBtn = new Button
        {
            Content   = "✎",
            Style     = (Style?)Application.Current.Resources["GhostButtonStyle"],
            FontSize  = 13,
            Padding   = new Thickness(4, 1, 4, 1),
            ToolTip   = "Edit entry",
            Margin    = new Thickness(0, 0, 2, 0)
        };
        var capturedIndex = index;
        editBtn.Click += (_, _) => EditEntry(capturedIndex);

        var delBtn = new Button
        {
            Content    = "✕",
            Style      = (Style?)Application.Current.Resources["GhostButtonStyle"],
            FontSize   = 11,
            Foreground = (Brush)res["ErrBrush"],
            Padding    = new Thickness(4, 1, 4, 1),
            ToolTip    = "Delete entry"
        };
        delBtn.Click += (_, _) =>
        {
            _entries.RemoveAt(capturedIndex);
            SaveToSession();
            RefreshList();
        };

        actionPanel.Children.Add(opBorder);
        actionPanel.Children.Add(editBtn);
        actionPanel.Children.Add(delBtn);
        Grid.SetColumn(actionPanel, 5);

        grid.Children.Add(hiveBorder);
        grid.Children.Add(keyTb);
        grid.Children.Add(nameTb);
        grid.Children.Add(typeTb);
        grid.Children.Add(dataTb);
        grid.Children.Add(actionPanel);
        row.Child = grid;
        return row;
    }

    private void SaveToSession()
        => BuildSession.Current.CustomRegistryEntries = [.. _entries];

    // ── Import .reg ───────────────────────────────────────────────────────────

    private void ImportReg_Click(object sender, MouseButtonEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Import Registry File",
            Filter = "Registry files|*.reg|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var lines = File.ReadAllLines(dlg.FileName);
            ParseRegFile(lines);
            SaveToSession();
            RefreshList();
        }
        catch (Exception ex)
        {
            AppDialog.Alert(this, $"Failed to parse .reg file:\n{ex.Message}", "Import Error",
                AppDialogIcon.Warning);
        }
    }

    private void ParseRegFile(string[] rawLines)
    {
        // Join continuation lines (lines ending with backslash)
        var lines = new List<string>();
        var buf = new System.Text.StringBuilder();
        foreach (var raw in rawLines)
        {
            var line = raw.TrimEnd();
            if (line.EndsWith('\\'))
            {
                buf.Append(line[..^1]);   // strip trailing backslash, accumulate
            }
            else
            {
                buf.Append(line);
                lines.Add(buf.ToString());
                buf.Clear();
            }
        }
        if (buf.Length > 0) lines.Add(buf.ToString()); // trailing line without newline

        string currentKey  = "";
        string currentHive = "HKLM";

        foreach (var line in lines)
        {
            var l = line.Trim();
            if (l.StartsWith("[HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
            {
                currentHive = "HKLM";
                currentKey  = l.TrimStart('[').TrimEnd(']')
                               .Replace("HKEY_LOCAL_MACHINE\\", "", StringComparison.OrdinalIgnoreCase);
            }
            else if (l.StartsWith("[HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
            {
                currentHive = "HKCU";
                currentKey  = l.TrimStart('[').TrimEnd(']')
                               .Replace("HKEY_CURRENT_USER\\", "", StringComparison.OrdinalIgnoreCase);
            }
            else if (l.StartsWith("\"") && l.Contains('=') && !string.IsNullOrEmpty(currentKey))
            {
                int eq       = l.IndexOf('=');
                string vn    = l[1..(eq - 1)];
                string vdata = l[(eq + 1)..];

                string type = "REG_SZ", data = "";
                if (vdata.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
                {
                    type = "REG_DWORD";
                    // .reg files store dword as 8-digit hex; reg.exe /d expects decimal
                    string hexStr = vdata[6..].Trim();
                    data = uint.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber,
                                         System.Globalization.CultureInfo.InvariantCulture, out uint dval)
                           ? dval.ToString()
                           : hexStr;   // fallback: pass as-is
                }
                else if (vdata.StartsWith("qword:", StringComparison.OrdinalIgnoreCase))
                {
                    type = "REG_QWORD";
                    string hexStr = vdata[6..].Trim();
                    data = ulong.TryParse(hexStr, System.Globalization.NumberStyles.HexNumber,
                                          System.Globalization.CultureInfo.InvariantCulture, out ulong qval)
                           ? qval.ToString()
                           : hexStr;
                }
                else if (vdata.StartsWith("hex(b):", StringComparison.OrdinalIgnoreCase))
                {
                    // REG_QWORD stored as little-endian hex bytes
                    type = "REG_QWORD";
                    // Reconstruct the 8-byte LE value → ulong → decimal string for reg.exe
                    var bytes = vdata[7..].Split(',')
                                          .Select(h => h.Trim())
                                          .Where(h => h.Length > 0)
                                          .Select(h => Convert.ToByte(h, 16))
                                          .ToArray();
                    if (bytes.Length >= 8)
                        data = BitConverter.ToUInt64(bytes, 0).ToString();
                    else
                        data = vdata[7..];
                }
                else if (vdata.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
                { type = "REG_BINARY"; data = vdata[4..].Replace(",", ""); }
                else if (vdata.StartsWith("hex(7):", StringComparison.OrdinalIgnoreCase))
                { type = "REG_MULTI_SZ"; data = vdata[7..]; }
                else if (vdata.StartsWith("hex(2):", StringComparison.OrdinalIgnoreCase))
                { type = "REG_EXPAND_SZ"; data = vdata[7..]; }
                else if (vdata.StartsWith("\""))
                { data = vdata.Trim('"'); }

                _entries.Add(new RegistryEntry
                {
                    Hive = currentHive, KeyPath = currentKey,
                    ValueName = vn, Type = type, Data = data, Operation = "SET"
                });
            }
        }
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        SaveToSession();
        NavigateRequested?.Invoke("wizard", 3);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SaveToSession();
        NavigateRequested?.Invoke("wizard", 5);
    }
}
