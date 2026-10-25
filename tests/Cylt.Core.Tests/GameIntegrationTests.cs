using Cylt.Core;
using Xunit.Abstractions;

namespace Cylt.Core.Tests;

public sealed class GameIntegrationTests(ITestOutputHelper output)
{
    // Opt-in integration check: downloads game files and starts one offline test client.
    [GameIntegrationFact]
    public async Task InstallAndStartGameWhenRequested()
    {
        string? directory = Environment.GetEnvironmentVariable("CYLT_INTEGRATION_GAME_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var settings = new LauncherSettings { GameDirectory = Path.GetFullPath(directory), MemoryMb = 2048 };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        var game = new GameService(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var versions = await game.GetVersionsAsync(settings, timeout.Token);
        string version = Environment.GetEnvironmentVariable("CYLT_INTEGRATION_VERSION") ?? versions.First(v => v.Type == "release").Id;
        var progress = new Progress<WorkProgress>(p => { if (p.Fraction is null) output.WriteLine(p.Message); });
        await game.InstallAsync(settings, version, progress, timeout.Token);
        using var process = await game.BuildProcessAsync(settings, version, LauncherAccount.Offline("CyltTest"), false, progress, timeout.Token);
        Assert.True(process.Start());
        await Task.Delay(TimeSpan.FromSeconds(20), timeout.Token);
        Assert.False(process.HasExited, "Game exited before reaching the main menu.");
        File.WriteAllText(Path.Combine(settings.GameDirectory, "cylt-test-process.txt"), process.Id.ToString());
        output.WriteLine($"Started {version}; PID {process.Id}");
    }
}

public sealed class GameIntegrationFactAttribute : FactAttribute
{
    public GameIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CYLT_INTEGRATION_GAME_DIR")))
            Skip = "Set CYLT_INTEGRATION_GAME_DIR to opt in to downloading and starting Minecraft.";
    }
}
