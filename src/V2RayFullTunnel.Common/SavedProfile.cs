using System.Text.Json.Serialization;

namespace V2RayFullTunnel.Common;

/// <summary>یک کانفیگ ذخیره‌شده که بین اجراها باقی می‌ماند.</summary>
public sealed class SavedProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Link { get; set; } = "";

    [JsonIgnore]
    public string Details { get; set; } = "";

    [JsonIgnore]
    public bool IsDefault { get; set; }

    [JsonIgnore]
    public string Display { get; set; } = "";
}