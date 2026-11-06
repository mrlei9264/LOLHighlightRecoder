using LolHighlight.Recording.Infrastructure;
using LolHighlight.Recording.Services;

namespace LolHighlight.Desktop.Tests;

public sealed class PortableAndColorLutTests
{
    [Fact]
    public void PortablePaths_DefaultToProgramDirectory_AndKeepCustomRecordingFolders()
    {
        using var directory = new TestDirectory();
        var paths = new AppPaths(directory.Path);
        Assert.Equal(Path.Combine(directory.Path, "config.json"), paths.ConfigPath);
        Assert.Equal(Path.Combine(directory.Path, "recordings"), paths.ResolveOutputDirectory("recordings"));
        Assert.Equal(Path.Combine(directory.Path, ".runtime"), paths.ResolveRuntimeDirectory(".runtime"));
        var config = new AppConfig { OutputDir = "../outside", RuntimeDir = Path.GetTempPath() };
        paths.NormalizeDataDirectories(config);
        Assert.Equal("../outside", config.OutputDir);
        Assert.Equal(".runtime", config.RuntimeDir);
        var settings = new SettingsService(paths.ConfigPath, config, paths: paths);
        settings.Save(config);
        Assert.Equal("../outside", settings.Load().OutputDir);
        Assert.Equal(Path.GetFullPath("../outside", directory.Path), paths.ResolveOutputDirectory(settings.Load().OutputDir));

        using var external = new TestDirectory();
        var absolute = new AppConfig { OutputDir = external.Path };
        paths.NormalizeDataDirectories(absolute);
        Assert.Equal(external.Path, absolute.OutputDir);
        settings.Save(absolute);
        Assert.Equal(external.Path, settings.Load().OutputDir);
        Assert.Equal(external.Path, paths.ResolveOutputDirectory(settings.Load().OutputDir));
        Assert.Throws<ArgumentException>(() => settings.Save(new AppConfig { RuntimeDir = external.Path }));
    }

    [Fact]
    public void SharedLookupTable_UsesCorrectAxisOrder_AndZeroStrengthKeepsOriginalColor()
    {
        var tone = ColorTones.Get("cool");
        var original = ColorGradeLut.Transform(tone, 0, .25, .5, .75);
        Assert.Equal(.25, original.R, 8); Assert.Equal(.5, original.G, 8); Assert.Equal(.75, original.B, 8);
        var data = ColorGradeLut.CreateRgb(tone, 0);
        var rows = ColorGradeLut.ToCube(data).Split('\n').Skip(4).Where(l => l.Length > 0).ToArray();
        Assert.Equal(ColorGradeLut.Size * ColorGradeLut.Size * ColorGradeLut.Size, rows.Length);
        Assert.Equal("0 0 0", rows[0]);
        Assert.Equal("1 0 0", rows[ColorGradeLut.Size - 1]);
        Assert.Equal("0 0 1", rows[ColorGradeLut.Size * ColorGradeLut.Size * (ColorGradeLut.Size - 1)]);
        var gray = ColorGradeLut.Transform(ColorTones.Get("mono"), 1, .8, .4, .1);
        Assert.Equal(gray.R, gray.G, 8); Assert.Equal(gray.G, gray.B, 8);
    }
}
