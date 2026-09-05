using System.Text.Json;
using System.Text.Json.Serialization;
using PGAssetTool.Core.Mods;

namespace PGAssetTool.Core.Settings;

/// What the tool remembers between runs.
///
/// Kept beside the executable rather than in the registry or under the user profile, so a copy of
/// the folder is a copy of the whole setup and carrying the tool on a stick carries its settings
/// with it. Nothing here is per-installation; anything that varies by game belongs in that
/// installation's own directory.
public sealed record ToolSettings
{
    public const string FileName = "settings.json";

    /// The localization bundle weapon names are read from. Not a UI language — this is the game's
    /// own translation table, and it is what decides whether the list reads "Hitman Pistol" or
    /// something a Japanese player would recognise.
    public string Language { get; init; } = "l_en-gb";

    /// Where extracted weapons and assets are written. Empty means beside the tool, under
    /// PGAssetTool-data, which is the only place that works whatever directory the exe is launched
    /// from — the CLI can default to the current one because a shell has a meaningful one.
    public string WorkspaceRoot { get; init; } = "";

    /// Recorded in the manifest of anything extracted as a workspace.
    public string Author { get; init; } = "";

    /// Whether the asset tree starts filtered to what can be written back.
    public bool ReplaceableOnly { get; init; } = true;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathIn(string home) => Path.Combine(home, FileName);

    public string WorkspaceIn(string home)
        => WorkspaceRoot.Length > 0 ? WorkspaceRoot : Path.Combine(home, "workspace");

    /// A settings file that cannot be read is replaced by the defaults rather than stopping the
    /// tool: it holds preferences, and none of them is worth refusing to start over.
    public static ToolSettings Load(string? home = null)
    {
        var path = PathIn(home ?? ModStore.DefaultHome());
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ToolSettings>(File.ReadAllText(path), Json) ?? new ToolSettings()
                : new ToolSettings();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return new ToolSettings();
        }
    }

    public void Save(string? home = null)
    {
        var directory = home ?? ModStore.DefaultHome();
        Directory.CreateDirectory(directory);
        File.WriteAllText(PathIn(directory), JsonSerializer.Serialize(this, Json));
    }
}
