using PGAssetTool.Core.Settings;

namespace PGAssetTool.Core.Tests;

public class ToolSettingsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("pgassettool-settings").FullName;

    [Fact]
    public void NothingSavedYetMeansTheDefaults()
    {
        var settings = ToolSettings.Load(_home);

        Assert.Equal("l_en-gb", settings.Language);
        Assert.True(settings.ReplaceableOnly);
    }

    [Fact]
    public void WhatIsSavedComesBack()
    {
        new ToolSettings { Language = "l_ja", ReplaceableOnly = false }.Save(_home);

        var settings = ToolSettings.Load(_home);
        Assert.Equal("l_ja", settings.Language);
        Assert.False(settings.ReplaceableOnly);
    }

    [Fact]
    public void SettingsSitBesideTheToolRatherThanInTheUserProfile()
    {
        // Copying the folder has to copy the setup with it, and nothing may be left behind in the
        // registry or under the profile when the tool is carried elsewhere.
        new ToolSettings().Save(_home);

        Assert.True(File.Exists(Path.Combine(_home, ToolSettings.FileName)));
        Assert.StartsWith(_home, ToolSettings.PathIn(_home), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatCannotBeReadFallsBackRatherThanStoppingTheTool()
    {
        // These are preferences. None of them is worth refusing to start over.
        File.WriteAllText(Path.Combine(_home, ToolSettings.FileName), "{ this is not json");

        Assert.Equal("l_en-gb", ToolSettings.Load(_home).Language);
    }

    [Fact]
    public void AFieldMissingFromAnOlderFileKeepsItsDefault()
    {
        File.WriteAllText(Path.Combine(_home, ToolSettings.FileName), """{ "language": "l_ko" }""");

        var settings = ToolSettings.Load(_home);
        Assert.Equal("l_ko", settings.Language);
        Assert.True(settings.ReplaceableOnly);
    }

    public void Dispose() => Directory.Delete(_home, recursive: true);
}
