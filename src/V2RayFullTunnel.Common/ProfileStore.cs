using System.Text.Json;

namespace V2RayFullTunnel.Common;

/// <summary>ذخیره و بازیابی لیست پروفایل‌ها در %APPDATA%\V2RayFullTunnel\profiles.json</summary>
public sealed class ProfileStore
{
    public string FilePath { get; }
    public string DefaultPath { get; }
    public IReadOnlyList<SavedProfile> Profiles => _items;
    public string DefaultId { get; private set; } = "";

    private List<SavedProfile> _items = new();

    public ProfileStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "V2RayFullTunnel");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "profiles.json");
        DefaultPath = Path.Combine(dir, "default.json");
        Load();
        LoadDefault();
        Refresh();
    }

    /// <summary>نمایش با نشان پیش‌فرض بودن را تازه می‌کند.</summary>
    public void Refresh()
    {
        foreach (var p in _items)
        {
            p.Details = Describe(p.Link);
            p.IsDefault = p.Id == DefaultId;
            p.Display = p.IsDefault ? p.Name + "  (default)" : p.Name;
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _items = JsonSerializer.Deserialize<List<SavedProfile>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { _items = new(); }
    }

    private void LoadDefault()
    {
        try
        {
            if (File.Exists(DefaultPath)) DefaultId = JsonSerializer.Deserialize<string>(File.ReadAllText(DefaultPath)) ?? "";
        }
        catch { DefaultId = ""; }
        if (_items.All(p => p.Id != DefaultId)) DefaultId = "";
    }

    private void SaveDefault()
    {
        try { File.WriteAllText(DefaultPath, JsonSerializer.Serialize(DefaultId)); } catch { }
    }

    private void Save()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, options));
        File.Move(tmp, FilePath, overwrite: true);
        Refresh();
    }

    public static string Describe(string link)
    {
        try
        {
            var p = LinkParser.Parse(link);
            return $"{p.Protocol.ToString().ToUpperInvariant()} • {p.Address}:{p.Port} • {p.Remark}".Trim();
        }
        catch { return "لینک نامعتبر"; }
    }

    public void Add(string name, string link)
    {
        string clean = (link ?? "").Trim();
        if (clean.Length == 0) throw new ArgumentException("لینک خالی است.");
        _items.RemoveAll(p => p.Link == clean); // بدون تکرار
        var profile = new SavedProfile
        {
            Name = (name ?? "").Trim(),
            Link = clean,
            Details = Describe(clean)
        };
        if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = profile.Details;
        _items.Add(profile);
        if (DefaultId == "") DefaultId = profile.Id;
        Save();
        SaveDefault();
    }

    public void SetDefault(string id)
    {
        if (_items.All(p => p.Id != id)) return;
        DefaultId = DefaultId == id ? "" : id;
        SaveDefault();
        Save();
    }

    public void Remove(string id)
    {
        if (_items.RemoveAll(p => p.Id == id) > 0) Save();
    }

    public void Delete(string id) => Remove(id);

    public void Update(string id, string name, string link)
    {
        var p = _items.FirstOrDefault(x => x.Id == id)
            ?? throw new InvalidOperationException("Profile not found.");
        string clean = (link ?? "").Trim();
        if (clean.Length == 0) throw new ArgumentException("Link is empty.");
        p.Link = clean;
        p.Details = Describe(clean);
        p.Name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(p.Name)) p.Name = p.Details;
        Save();
    }

    public bool Rename(string id, string name)
    {
        var p = _items.FirstOrDefault(x => x.Id == id);
        if (p == null) return false;
        p.Name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(p.Name)) p.Name = p.Details;
        Save();
        return true;
    }
}