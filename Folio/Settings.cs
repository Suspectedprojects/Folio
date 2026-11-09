using System.Text.Json;

namespace Folio;

public sealed class Settings
{
    public double ThumbSize { get; set; } = 256;
    public bool Details { get; set; }
    public bool ShowHidden { get; set; }
    public bool ShowExtensions { get; set; } = true;
    public string? LastFolder { get; set; }
    public string SortBy { get; set; } = "Name";
    public bool SortDesc { get; set; }
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 780;
    public bool Maximized { get; set; }
    public double TreeWidth { get; set; } = 240;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Folio", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
