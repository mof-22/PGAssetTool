using PGAssetTool.Core.Settings;

namespace PGAssetTool.Core.Tests;

// The log is one static home for the whole process, so these do not run beside each other.
[Collection(nameof(ErrorLogTests))]
public class ErrorLogTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("pgassettool-log").FullName;
    private readonly string? _was = ErrorLog.Home;

    public ErrorLogTests() => ErrorLog.Home = _home;

    public void Dispose()
    {
        ErrorLog.Home = _was;
        Directory.Delete(_home, recursive: true);
    }

    private static Exception Thrown()
    {
        try { throw new InvalidOperationException("the bundle was not there"); }
        catch (Exception e) { return e; }
    }

    [Fact]
    public void AReportedErrorKeepsItsStackAndSaysTheSameLine()
    {
        var said = ErrorLog.Said(Thrown(), "extracting the weapon");

        Assert.Equal("the bundle was not there  (while extracting the weapon)", said);
        var log = File.ReadAllText(Path.Combine(ErrorLog.Folder, ErrorLog.ErrorsFileName));
        Assert.Contains("while extracting the weapon", log);
        Assert.Contains(nameof(Thrown), log);
    }

    [Fact]
    public void TheLogLivesBesideEverythingElseTheToolKeeps()
        => Assert.Equal(Path.Combine(_home, "logs"), ErrorLog.Folder);

    [Fact]
    public void AnOverlongLogIsStartedAgainKeepingThePreviousOne()
    {
        Directory.CreateDirectory(ErrorLog.Folder);
        var path = Path.Combine(ErrorLog.Folder, ErrorLog.ErrorsFileName);
        File.WriteAllText(path, new string('x', 1024 * 1024 + 1));

        ErrorLog.Record(Thrown(), "anything");

        Assert.True(new FileInfo(path).Length < 10_000);
        Assert.True(File.Exists(Path.Combine(ErrorLog.Folder, "errors.old.log")));
    }

    [Fact]
    public void ACrashIsToldAboutOnceOnly()
    {
        Assert.Null(ErrorLog.UntoldCrash());

        var report = ErrorLog.Crash(Thrown(), "testing");
        Assert.NotNull(report);
        Assert.Contains("the bundle was not there", File.ReadAllText(report));

        Assert.Equal(report, ErrorLog.UntoldCrash());
        Assert.Null(ErrorLog.UntoldCrash());
    }
}
