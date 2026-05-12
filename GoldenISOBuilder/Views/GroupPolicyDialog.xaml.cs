using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GoldenISOBuilder.Models;
using GoldenISOBuilder.Services;

namespace GoldenISOBuilder.Views;

public partial class GroupPolicyDialog : Window
{
    // ── State ─────────────────────────────────────────────────────────────────
    private AdmxParser?  _parser;
    private AdmxPolicy?  _selectedPolicy;
    private string       _selectedState = "NotConfigured";

    /// <summary>
    /// Entries produced by this dialog. May contain more than one record when
    /// the policy has extra element inputs (each element is its own registry value).
    /// </summary>
    public List<GroupPolicyEntry> Results { get; private set; } = [];

    // Element input controls keyed by element Id
    private readonly Dictionary<string, FrameworkElement> _elementInputs = new();

    // ── Source routing ────────────────────────────────────────────────────────
    private readonly string       _admxPath;
    private readonly AdmxParser?  _preloadedParser;

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <param name="admxPath">
    ///   Path to the PolicyDefinitions folder to parse.
    ///   Defaults to C:\Windows\PolicyDefinitions (this machine).
    /// </param>
    /// <param name="preloaded">
    ///   If the caller already has a parsed <see cref="AdmxParser"/> (e.g. from
    ///   an ISO WIM mount), pass it here to skip the loading phase entirely.
    /// </param>
    public GroupPolicyDialog(string? admxPath = null, AdmxParser? preloaded = null)
    {
        _admxPath        = admxPath ?? AdmxParser.DefaultPath;
        _preloadedParser = preloaded;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Apply theme-aware TreeView selection colours at runtime so they pick up
        // the current theme rather than the dark-only hardcoded XAML values.
        var res = Application.Current.Resources;
        var gold1c = (Color)res["Gold1Color"];
        CategoryTree.Resources[SystemColors.HighlightBrushKey] =
            new SolidColorBrush(Color.FromArgb(0x40, gold1c.R, gold1c.G, gold1c.B));
        CategoryTree.Resources[SystemColors.HighlightTextBrushKey] =
            (Brush)res["FG0Brush"];
        CategoryTree.Resources[SystemColors.InactiveSelectionHighlightBrushKey] =
            (Brush)res["BG3Brush"];
        CategoryTree.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] =
            (Brush)res["FG2Brush"];

        var progress = new Progress<string>(msg => LoadingText.Text = msg);
        try
        {
            if (_preloadedParser != null)
            {
                // Caller pre-loaded the parser (e.g. from an ISO WIM mount);
                // skip the loading progress phase and use it directly.
                _parser = _preloadedParser;
            }
            else
            {
                _parser = await AdmxParser.GetOrLoadAsync(_admxPath, progress);
            }

            PopulateTree(_parser.RootCategories);

            LoadProgressBar.Visibility    = Visibility.Collapsed;
            LoadingText.Text              = "";
            LoadingPanel.Visibility       = Visibility.Collapsed;
            SelectCategoryHint.Visibility = Visibility.Visible;
            SearchBox.IsEnabled           = true;
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"Failed to load ADMX files: {ex.Message}";
        }
    }

    // ── Tree population ───────────────────────────────────────────────────────

    private void PopulateTree(IReadOnlyList<AdmxCategory> roots)
    {
        CategoryTree.Items.Clear();
        foreach (var cat in roots)
            CategoryTree.Items.Add(BuildTreeItem(cat));
    }

    private static TreeViewItem BuildTreeItem(AdmxCategory cat)
    {
        var item = new TreeViewItem { Header = cat.DisplayName, Tag = cat };
        foreach (var child in cat.Children)
            item.Items.Add(BuildTreeItem(child));
        return item;
    }

    // ── Tree selection ────────────────────────────────────────────────────────

    private void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem tvi || tvi.Tag is not AdmxCategory cat) return;

        // Suppress Search_Changed while we clear the box
        SearchBox.TextChanged -= Search_Changed;
        SearchBox.Text = "";
        SearchBox.TextChanged += Search_Changed;

        ShowPoliciesForCategory(cat);
    }

    private void ShowPoliciesForCategory(AdmxCategory cat)
    {
        var policies = new List<AdmxPolicy>();
        CollectPolicies(cat, policies);
        policies.Sort((a, b) =>
            string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        RebuildPolicyList(policies);
    }

    private static void CollectPolicies(AdmxCategory cat, List<AdmxPolicy> list)
    {
        list.AddRange(cat.Policies);
        foreach (var child in cat.Children)
            CollectPolicies(child, list);
    }

    // ── Policy list ───────────────────────────────────────────────────────────

    private void RebuildPolicyList(IReadOnlyList<AdmxPolicy> policies)
    {
        PolicyListPanel.Children.Clear();
        SelectCategoryHint.Visibility = Visibility.Collapsed;
        PolicyListScroller.Visibility = Visibility.Visible;

        if (policies.Count == 0)
        {
            PolicyListPanel.Children.Add(new TextBlock
            {
                Text                = "No policies in this category.",
                Foreground          = (Brush)Application.Current.Resources["FG3Brush"],
                FontSize            = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 24, 0, 0),
            });
            return;
        }

        foreach (var pol in policies)
            PolicyListPanel.Children.Add(BuildPolicyRow(pol));
    }

    private UIElement BuildPolicyRow(AdmxPolicy pol)
    {
        bool isSelected = _selectedPolicy == pol;

        var bgNormal   = (Brush)Application.Current.Resources["BG0Brush"];
        var bgHover    = (Brush)Application.Current.Resources["RowHoverBgBrush"];
        var bgSelected = (Brush)Application.Current.Resources["BG3Brush"];
        var fg0        = (Brush)Application.Current.Resources["FG0Brush"];
        var fg1        = (Brush)Application.Current.Resources["FG1Brush"];

        var row = new Border
        {
            Background      = isSelected ? bgSelected : bgNormal,
            BorderThickness = isSelected ? new Thickness(3, 0, 0, 0) : new Thickness(0),
            BorderBrush     = (Brush)Application.Current.Resources["Gold1Brush"],
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(10, 8, 10, 8),
            Margin          = new Thickness(0, 0, 0, 2),
            Cursor          = Cursors.Hand,
            Tag             = pol,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Policy name TextBlock — FIX #8: store reference so selection can update it
        var nameTb = new TextBlock
        {
            Text         = pol.DisplayName,
            Foreground   = isSelected ? fg0 : fg1,
            FontSize     = 13,
            FontWeight   = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(nameTb);
        info.Children.Add(new TextBlock
        {
            Text         = pol.CategoryPath,
            Foreground   = (Brush)Application.Current.Resources["FG3Brush"],
            FontSize     = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(info, 0);

        bool isMachine = pol.PolicyClass != "User";
        var bdGold1c = (Color)Application.Current.Resources["Gold1Color"];
        var bdOkc    = (Color)Application.Current.Resources["OkColor"];
        var badge = new Border
        {
            CornerRadius      = new CornerRadius(4),
            Padding           = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 0, 0),
            Background        = new SolidColorBrush(isMachine
                ? Color.FromArgb(0x30, bdGold1c.R, bdGold1c.G, bdGold1c.B)
                : Color.FromArgb(0x30, bdOkc.R,    bdOkc.G,    bdOkc.B)),
        };
        badge.Child = new TextBlock
        {
            Text       = isMachine ? "Machine" : "User",
            Foreground = isMachine
                ? (Brush)Application.Current.Resources["Gold1Brush"]
                : (Brush)Application.Current.Resources["OkBrush"],
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
        };
        Grid.SetColumn(badge, 1);

        grid.Children.Add(info);
        grid.Children.Add(badge);
        row.Child = grid;

        // Hover & click
        row.MouseEnter += (_, _) =>
        {
            if (_selectedPolicy != pol) row.Background = bgHover;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = _selectedPolicy == pol ? bgSelected : bgNormal;
        };
        row.MouseLeftButtonUp += (_, ev) =>
        {
            SelectPolicy(pol);
            ev.Handled = true;
        };

        return row;
    }

    // ── Policy selection ──────────────────────────────────────────────────────

    private void SelectPolicy(AdmxPolicy pol)
    {
        _selectedPolicy = pol;
        _selectedState  = "NotConfigured";  // always reset state on new selection

        // Update all row visuals — FIX #8: also update TextBlock foreground/weight
        var fg0 = (Brush)Application.Current.Resources["FG0Brush"];
        var fg1 = (Brush)Application.Current.Resources["FG1Brush"];
        var gold = (Brush)Application.Current.Resources["Gold1Brush"];

        foreach (var rowBorder in PolicyListPanel.Children.OfType<Border>())
        {
            if (rowBorder.Tag is not AdmxPolicy rowPol) continue;
            bool sel = rowPol == pol;

            rowBorder.Background     = sel
                ? (Brush)Application.Current.Resources["BG3Brush"]
                : (Brush)Application.Current.Resources["BG0Brush"];
            rowBorder.BorderThickness = sel ? new Thickness(3, 0, 0, 0) : new Thickness(0);
            rowBorder.BorderBrush     = gold;

            // Update the name TextBlock (first child of the first StackPanel in the grid)
            if (rowBorder.Child is Grid g
                && g.Children.Count > 0
                && g.Children[0] is StackPanel sp
                && sp.Children.Count > 0
                && sp.Children[0] is TextBlock tb)
            {
                tb.Foreground = sel ? fg0 : fg1;
                tb.FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        // Populate detail panel
        DetailPolicyName.Text   = pol.DisplayName;
        DetailCategoryPath.Text = pol.CategoryPath;
        DetailDescription.Text  = pol.ExplainText;
        DescriptionPanel.Visibility = string.IsNullOrWhiteSpace(pol.ExplainText)
            ? Visibility.Collapsed : Visibility.Visible;

        string policyRoot = pol.PolicyClass == "User" ? "HKCU" : "HKLM";
        DetailRegPath.Text = string.IsNullOrEmpty(pol.ValueName)
            ? $"{policyRoot}\\{pol.RegistryKey}"
            : $"{policyRoot}\\{pol.RegistryKey}  →  {pol.ValueName}";

        UpdateStatePills();
        BuildElementsPanel(pol);
        DetailPanel.Visibility = Visibility.Visible;
        UpdateAddButton();
    }

    // ── State pills ───────────────────────────────────────────────────────────

    private void StatePill_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string state)
        {
            _selectedState = state;
            UpdateStatePills();
            UpdateElementsVisibility();   // FIX #1: this now always sets visibility correctly
            UpdateAddButton();
        }
    }

    private void UpdateStatePills()
    {
        var activeBg    = (Brush)Application.Current.Resources["Gold1Brush"];
        var inactiveBg  = (Brush)Application.Current.Resources["BG1Brush"];
        var activeText  = (Brush)Application.Current.Resources["BG0Brush"];
        var inactiveText = (Brush)Application.Current.Resources["FG1Brush"];
        var goldBorder  = (Brush)Application.Current.Resources["Gold1Brush"];
        var quietBorder = (Brush)Application.Current.Resources["LineBrush"];

        foreach (var (pill, txt, state) in new[]
        {
            (PillNotConfigured, PillNotConfiguredText, "NotConfigured"),
            (PillEnabled,       PillEnabledText,       "Enabled"),
            (PillDisabled,      PillDisabledText,      "Disabled"),
        })
        {
            bool active = _selectedState == state;
            pill.Background      = active ? activeBg   : inactiveBg;
            pill.BorderBrush     = active ? goldBorder  : quietBorder;
            pill.BorderThickness = new Thickness(1);
            txt.Foreground       = active ? activeText  : inactiveText;
            txt.FontWeight       = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>FIX #1: Guard removed — always reflects current state correctly.</summary>
    private void UpdateElementsVisibility()
    {
        // Only show the elements panel if:
        //   a) state is Enabled, AND
        //   b) the panel actually has inputs (i.e. BuildElementsPanel populated it)
        ElementsPanel.Visibility = _selectedState == "Enabled" && ElementsContainer.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── Element inputs ────────────────────────────────────────────────────────

    private void BuildElementsPanel(AdmxPolicy pol)
    {
        _elementInputs.Clear();
        ElementsContainer.Children.Clear();

        // FIX #6: Removed "|| string.IsNullOrEmpty(pol.ValueName)" — some policies
        // define all their values through elements without a top-level valueName.
        if (pol.Elements.Count == 0)
        {
            ElementsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var displayable = pol.Elements
            .Where(e => e.ElementType is "decimal" or "longDecimal" or "text" or "enum" or "boolean")
            .ToList();

        if (displayable.Count == 0)
        {
            ElementsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var elem in displayable)
        {
            var label = new TextBlock
            {
                Text       = string.IsNullOrEmpty(elem.Label) ? elem.Id : elem.Label,
                Foreground = (Brush)Application.Current.Resources["FG2Brush"],
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 0, 0, 5),
            };

            FrameworkElement input;
            if (elem.ElementType == "enum" && elem.EnumItems.Count > 0)
            {
                var cb = new ComboBox
                {
                    Style  = (Style)Application.Current.Resources["ComboBoxStyle"],
                    Margin = new Thickness(0, 0, 0, 10),
                };
                foreach (var (val, lbl) in elem.EnumItems)
                    cb.Items.Add(new ComboBoxItem { Content = lbl, Tag = val });
                if (cb.Items.Count > 0) cb.SelectedIndex = 0;
                input = cb;
            }
            else if (elem.ElementType == "boolean")
            {
                var cb = new ComboBox
                {
                    Style  = (Style)Application.Current.Resources["ComboBoxStyle"],
                    Margin = new Thickness(0, 0, 0, 10),
                };
                cb.Items.Add(new ComboBoxItem { Content = "Enabled",  Tag = "1" });
                cb.Items.Add(new ComboBoxItem { Content = "Disabled", Tag = "0" });
                cb.SelectedIndex = 0;
                input = cb;
            }
            else
            {
                input = new TextBox
                {
                    Style  = (Style)Application.Current.Resources["MonoTextInputStyle"],
                    Text   = elem.DefaultValue,
                    Margin = new Thickness(0, 0, 0, 10),
                };
            }

            _elementInputs[elem.Id] = input;

            var wrapper = new StackPanel();
            wrapper.Children.Add(label);
            wrapper.Children.Add(input);
            ElementsContainer.Children.Add(wrapper);
        }

        // Initial visibility: hidden until user chooses "Enabled"
        ElementsPanel.Visibility = Visibility.Collapsed;
    }

    // ── Add-Policy button ─────────────────────────────────────────────────────

    private void UpdateAddButton()
    {
        AddPolicyBtn.IsEnabled = _selectedPolicy != null && _selectedState != "NotConfigured";
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_parser == null) return;
        var q = SearchBox.Text.Trim();

        if (string.IsNullOrEmpty(q))
        {
            // FIX #2: Reset selection state and hide detail panel when search is cleared
            _selectedPolicy = null;
            _selectedState  = "NotConfigured";
            DetailPanel.Visibility        = Visibility.Collapsed;
            PolicyListScroller.Visibility = Visibility.Collapsed;
            SelectCategoryHint.Visibility = Visibility.Visible;
            UpdateAddButton();
            return;
        }

        var matches = _parser.AllPolicies
            .Where(p => p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || p.CategoryPath.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || p.ExplainText .Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RebuildPolicyList(matches);
    }

    // ── OK / Cancel ───────────────────────────────────────────────────────────

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPolicy == null || _selectedState == "NotConfigured") return;

        Results = BuildEntries(_selectedPolicy, _selectedState);
        DialogResult = true;
    }

    private List<GroupPolicyEntry> BuildEntries(AdmxPolicy pol, string state)
    {
        var entries = new List<GroupPolicyEntry>();

        // Main policy entry (written when policy has a top-level valueName)
        if (!string.IsNullOrEmpty(pol.ValueName))
        {
            entries.Add(new GroupPolicyEntry
            {
                DisplayName  = pol.DisplayName,
                CategoryPath = pol.CategoryPath,
                PolicyClass  = pol.PolicyClass == "Both" ? "Machine" : pol.PolicyClass,
                RegistryKey  = pol.RegistryKey,
                ValueName    = pol.ValueName,
                State        = state,
                ValueType    = pol.ValueType,
                Value        = state == "Enabled" ? pol.EnabledValue : pol.DisabledValue,
            });
        }

        // Element entries — each element is a separate registry value in the same key
        if (state == "Enabled")
        {
            foreach (var elem in pol.Elements)
            {
                if (!_elementInputs.TryGetValue(elem.Id, out var inputCtrl)) continue;
                if (string.IsNullOrEmpty(elem.ValueName)) continue;

                string rawValue = inputCtrl switch
                {
                    ComboBox cb => (cb.SelectedItem as ComboBoxItem)?.Tag as string ?? "0",
                    TextBox  tb => tb.Text.Trim(),
                    _           => "",
                };
                if (string.IsNullOrEmpty(rawValue)) continue;

                string vt = elem.ElementType switch
                {
                    "text"        => "REG_SZ",
                    "longDecimal" => "REG_QWORD",
                    _             => "REG_DWORD",
                };

                entries.Add(new GroupPolicyEntry
                {
                    DisplayName  = $"{pol.DisplayName} — {(string.IsNullOrEmpty(elem.Label) ? elem.Id : elem.Label)}",
                    CategoryPath = pol.CategoryPath,
                    PolicyClass  = pol.PolicyClass == "Both" ? "Machine" : pol.PolicyClass,
                    RegistryKey  = pol.RegistryKey,
                    ValueName    = elem.ValueName,
                    State        = "Enabled",
                    ValueType    = vt,
                    Value        = rawValue,
                });
            }
        }

        // Fallback: policy defines no valueName and no elements (or all elements filtered out)
        if (entries.Count == 0)
        {
            entries.Add(new GroupPolicyEntry
            {
                DisplayName  = pol.DisplayName,
                CategoryPath = pol.CategoryPath,
                PolicyClass  = pol.PolicyClass == "Both" ? "Machine" : pol.PolicyClass,
                RegistryKey  = pol.RegistryKey,
                ValueName    = "",
                State        = state,
                ValueType    = pol.ValueType,
                Value        = state == "Enabled" ? pol.EnabledValue : pol.DisabledValue,
            });
        }

        return entries;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
