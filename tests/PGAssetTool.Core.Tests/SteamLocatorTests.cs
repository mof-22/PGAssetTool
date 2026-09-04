using PGAssetTool.Core.Game;

namespace PGAssetTool.Core.Tests;

public class SteamLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pgassettool-test").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void EnumerateLibraryFolders_ReadsPathsFromVdf()
    {
        var extra = Path.Combine(_root, "Library2");
        Directory.CreateDirectory(Path.Combine(_root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(extra, "steamapps"));

        File.WriteAllText(Path.Combine(_root, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path"		"{{_root.Replace(@"\", @"\\")}}"
                }
                "1"
                {
                    "path"		"{{extra.Replace(@"\", @"\\")}}"
                }
            }
            """);

        var folders = SteamLocator.EnumerateLibraryFolders(_root).ToList();

        Assert.Contains(Path.Combine(extra, "steamapps"), folders);
    }

    [Fact]
    public void EnumerateLibraryFolders_SkipsLibrariesThatAreNotOnDisk()
    {
        Directory.CreateDirectory(Path.Combine(_root, "steamapps"));
        File.WriteAllText(Path.Combine(_root, "steamapps", "libraryfolders.vdf"), """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"Z:\\NoSuchLibrary"
                }
            }
            """);

        var folders = SteamLocator.EnumerateLibraryFolders(_root).ToList();

        Assert.Equal([Path.Combine(_root, "steamapps")], folders);
    }

    [Fact]
    public void EnumerateLibraryFolders_YieldsPrimaryLibraryWithoutVdf()
    {
        Assert.Equal([Path.Combine(_root, "steamapps")], SteamLocator.EnumerateLibraryFolders(_root).ToList());
    }
}
