using System.IO;
using System.Xml.Linq;
using GoldenISOBuilder.Models;

namespace GoldenISOBuilder.Services;

/// <summary>
/// Parses all *.admx files from C:\Windows\PolicyDefinitions\ and resolves
/// display strings from the matching en-US *.adml files.
///
/// Each ADMX file's strings are loaded from its own ADML into a LOCAL dictionary,
/// so IDs from different files never collide (e.g. "DisplayName" in Windows.adml
/// and "DisplayName" in WindowsUpdate.adml are kept separate).
///
/// Results are statically cached — subsequent dialog opens are instant.
/// </summary>
public class AdmxParser
{
    public const string DefaultPath = @"C:\Windows\PolicyDefinitions";

    // ── Static cache — one entry per unique path ──────────────────────────────
    private static readonly Dictionary<string, AdmxParser> _cache
        = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim _loadLock = new(1, 1);

    public static async Task<AdmxParser> GetOrLoadAsync(
        string path = DefaultPath,
        IProgress<string>? progress = null)
    {
        if (_cache.TryGetValue(path, out var hit)) return hit;

        await _loadLock.WaitAsync();
        try
        {
            if (_cache.TryGetValue(path, out hit)) return hit;  // double-check after lock
            var parser = new AdmxParser(path);
            await Task.Run(() => parser.Load(progress));
            _cache[path] = parser;
            return parser;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    // ── Instance state ────────────────────────────────────────────────────────
    private readonly string _policyDefsPath;
    private readonly Dictionary<string, AdmxCategory> _catMap      = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AdmxCategory>               _roots       = [];
    private readonly List<AdmxPolicy>                 _allPolicies = [];

    private AdmxParser(string path) { _policyDefsPath = path; }

    public IReadOnlyList<AdmxCategory> RootCategories => _roots;
    public IReadOnlyList<AdmxPolicy>   AllPolicies     => _allPolicies;

    // ── Top-level loading ─────────────────────────────────────────────────────

    private void Load(IProgress<string>? progress)
    {
        if (!Directory.Exists(_policyDefsPath)) return;

        var admxFiles = Directory.GetFiles(_policyDefsPath, "*.admx", SearchOption.TopDirectoryOnly);
        progress?.Report($"Loading {admxFiles.Length} ADMX files…");

        // Determine ADML directory (en-US is the standard, fall back to the root)
        var admlDir = Path.Combine(_policyDefsPath, "en-US");
        if (!Directory.Exists(admlDir))
            admlDir = _policyDefsPath;

        int done = 0;
        foreach (var admx in admxFiles)
        {
            done++;
            if (done % 20 == 0)
                progress?.Report($"Parsing {done}/{admxFiles.Length} ADMX files…");
            try { ParseAdmx(admx, admlDir); }
            catch { /* skip malformed files */ }
        }

        BuildTree();
        progress?.Report($"Done — {_allPolicies.Count} policies in {_catMap.Count} categories.");
    }

    // ── Per-file ADML loading (no global string dict — avoids cross-file ID collisions) ──

    private static void LoadAdmlInto(string admlPath, Dictionary<string, string> target)
    {
        try
        {
            var doc         = XDocument.Load(admlPath);
            var stringTable = doc.Descendants()
                                 .FirstOrDefault(e => e.Name.LocalName == "stringTable");
            if (stringTable == null) return;

            foreach (var s in stringTable.Elements())
            {
                var id = s.Attribute("id")?.Value;
                if (!string.IsNullOrEmpty(id))
                    target[id] = s.Value.Trim();
            }
        }
        catch { /* skip malformed */ }
    }

    /// <summary>
    /// Resolves a $(string.xxx) token using the per-file local string table.
    /// Returns the raw token if the ID is not found (better than returning empty).
    /// </summary>
    private static string Resolve(string? raw, IReadOnlyDictionary<string, string> strings)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.StartsWith("$(string.", StringComparison.Ordinal) && raw.EndsWith(')'))
        {
            var id = raw[9..^1];
            return strings.TryGetValue(id, out var v) ? v : "";   // empty = skip unresolved
        }
        return raw;
    }

    // ── ADMX parsing (categories + policies) ─────────────────────────────────

    private void ParseAdmx(string admxPath, string admlDir)
    {
        // Each ADMX file gets its own local string dict from its matching ADML
        var localStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var admlPath = Path.Combine(admlDir, Path.GetFileNameWithoutExtension(admxPath) + ".adml");
        if (File.Exists(admlPath))
            LoadAdmlInto(admlPath, localStrings);

        var doc  = XDocument.Load(admxPath);
        var root = doc.Root;
        if (root == null) return;

        ParseCategories(root, localStrings);
        ParsePolicies(root, localStrings);
    }

    private void ParseCategories(XElement root, IReadOnlyDictionary<string, string> strings)
    {
        var categoriesEl = root.Descendants()
                               .FirstOrDefault(e => e.Name.LocalName == "categories");
        if (categoriesEl == null) return;

        foreach (var el in categoriesEl.Elements())
        {
            if (!el.Name.LocalName.Equals("category", StringComparison.OrdinalIgnoreCase)) continue;

            var name      = el.Attribute("name")?.Value ?? "";
            var dispRaw   = el.Attribute("displayName")?.Value;
            var parentRef = el.Descendants()
                              .FirstOrDefault(e => e.Name.LocalName == "parentCategory")
                              ?.Attribute("ref")?.Value ?? "";

            if (string.IsNullOrEmpty(name)) continue;

            if (!_catMap.TryGetValue(name, out var cat))
            {
                cat = new AdmxCategory { Name = name };
                _catMap[name] = cat;
            }

            if (string.IsNullOrEmpty(cat.DisplayName))
            {
                var resolved = Resolve(dispRaw, strings);
                if (!string.IsNullOrEmpty(resolved))
                    cat.DisplayName = resolved;
            }
            if (string.IsNullOrEmpty(cat.ParentName) && !string.IsNullOrEmpty(parentRef))
                cat.ParentName = parentRef;
        }
    }

    private void ParsePolicies(XElement root, IReadOnlyDictionary<string, string> strings)
    {
        var policiesEl = root.Descendants()
                             .FirstOrDefault(e => e.Name.LocalName == "policies");
        if (policiesEl == null) return;

        foreach (var el in policiesEl.Elements())
        {
            if (!el.Name.LocalName.Equals("policy", StringComparison.OrdinalIgnoreCase)) continue;

            var name    = el.Attribute("name")?.Value ?? "";
            var display = Resolve(el.Attribute("displayName")?.Value, strings);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(display)) continue;

            var policy = new AdmxPolicy
            {
                Name        = name,
                DisplayName = display,
                ExplainText = Resolve(el.Attribute("explainText")?.Value, strings),
                PolicyClass = el.Attribute("class")?.Value ?? "Machine",
                RegistryKey = el.Attribute("key")?.Value ?? "",
                ValueName   = el.Attribute("valueName")?.Value ?? "",
            };

            policy.CategoryName = el.Descendants()
                                    .FirstOrDefault(e => e.Name.LocalName == "parentCategory")
                                    ?.Attribute("ref")?.Value ?? "";

            var enabledEl  = el.Descendants().FirstOrDefault(e => e.Name.LocalName == "enabledValue");
            var disabledEl = el.Descendants().FirstOrDefault(e => e.Name.LocalName == "disabledValue");

            policy.EnabledValue  = ExtractSimpleValue(enabledEl,  "1");
            policy.DisabledValue = ExtractSimpleValue(disabledEl, "0");
            policy.ValueType     = DetectValueType(enabledEl ?? disabledEl);

            var elementsEl = el.Descendants().FirstOrDefault(e => e.Name.LocalName == "elements");
            if (elementsEl != null)
            {
                foreach (var elem in elementsEl.Elements())
                    policy.Elements.Add(ParseElement(elem, strings));
            }

            _allPolicies.Add(policy);
        }
    }

    private static string ExtractSimpleValue(XElement? container, string fallback)
    {
        if (container == null) return fallback;
        var child = container.Elements().FirstOrDefault();
        return child?.Attribute("value")?.Value ?? fallback;
    }

    private static string DetectValueType(XElement? container)
    {
        if (container == null) return "REG_DWORD";
        var child = container.Elements().FirstOrDefault();
        return child?.Name.LocalName switch
        {
            "string"      => "REG_SZ",
            "longDecimal" => "REG_QWORD",
            _             => "REG_DWORD",
        };
    }

    private static AdmxElement ParseElement(XElement el, IReadOnlyDictionary<string, string> strings)
    {
        var elem = new AdmxElement
        {
            Id           = el.Attribute("id")?.Value ?? "",
            ElementType  = el.Name.LocalName,
            ValueName    = el.Attribute("valueName")?.Value ?? "",
            Label        = Resolve(el.Attribute("displayName")?.Value, strings),
            DefaultValue = el.Attribute("defaultValue")?.Value ?? "",
            Required     = el.Attribute("required")?.Value == "true",
        };

        if (el.Name.LocalName == "enum")
        {
            foreach (var item in el.Elements())
            {
                if (!item.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase)) continue;
                var itemLabel = Resolve(item.Attribute("displayName")?.Value, strings);
                var valEl     = item.Descendants().FirstOrDefault(e => e.Name.LocalName == "decimal")
                             ?? item.Descendants().FirstOrDefault(e => e.Name.LocalName == "string");
                var itemVal   = valEl?.Attribute("value")?.Value ?? "";
                if (!string.IsNullOrEmpty(itemLabel))
                    elem.EnumItems.Add((itemVal, itemLabel));
            }
        }

        return elem;
    }

    // ── Category tree assembly ────────────────────────────────────────────────

    private void BuildTree()
    {
        foreach (var pol in _allPolicies)
        {
            if (string.IsNullOrEmpty(pol.CategoryName)) continue;
            if (!_catMap.TryGetValue(pol.CategoryName, out var cat))
            {
                cat = new AdmxCategory { Name = pol.CategoryName, DisplayName = pol.CategoryName };
                _catMap[pol.CategoryName] = cat;
            }
            cat.Policies.Add(pol);
        }

        foreach (var cat in _catMap.Values)
        {
            if (string.IsNullOrEmpty(cat.DisplayName))
                cat.DisplayName = cat.Name;

            if (string.IsNullOrEmpty(cat.ParentName))
            {
                _roots.Add(cat);
            }
            else if (_catMap.TryGetValue(cat.ParentName, out var parent))
            {
                parent.Children.Add(cat);
            }
            else
            {
                _roots.Add(cat); // orphaned parent ref — show at tree root
            }
        }

        _roots.Sort(CatCompare);
        foreach (var root in _roots)
            ComputePaths(root, "");
    }

    private void ComputePaths(AdmxCategory cat, string prefix)
    {
        cat.FullPath = string.IsNullOrEmpty(prefix) ? cat.DisplayName : $"{prefix}\\{cat.DisplayName}";

        foreach (var pol in cat.Policies)
            pol.CategoryPath = cat.FullPath;

        cat.Children.Sort(CatCompare);
        foreach (var child in cat.Children)
            ComputePaths(child, cat.FullPath);
    }

    private static int CatCompare(AdmxCategory a, AdmxCategory b)
        => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
}
