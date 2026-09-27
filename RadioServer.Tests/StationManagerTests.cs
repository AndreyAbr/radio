using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RadioServer.Stations;
using Xunit;

namespace RadioServer.Tests;

public class StationManagerTests : IDisposable
{
    private readonly string _testWebRoot;

    public StationManagerTests()
    {
        _testWebRoot = Path.Combine(Path.GetTempPath(), "StationManagerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_testWebRoot, "audio"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testWebRoot))
            {
                Directory.Delete(_testWebRoot, recursive: true);
            }
        }
        catch { }
    }

    private class TestEnv : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "RadioServer";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
    }

    private StationManager CreateManager()
    {
        var env = new TestEnv { WebRootPath = _testWebRoot, ContentRootPath = _testWebRoot };
        var logger = NullLogger<StationManager>.Instance;
        return new StationManager(env, logger);
    }

    [Fact]
    public void StationManager_Initializes_Predefined_And_Synthwave_Stations()
    {
        var manager = CreateManager();
        var stations = manager.GetStationsInfo().ToList();

        Assert.True(stations.Count >= 4);
        Assert.Contains(stations, s => s.Id == "rock");
        Assert.Contains(stations, s => s.Id == "jazz");
        Assert.Contains(stations, s => s.Id == "pop");
        Assert.Contains(stations, s => s.Id == "synthwave");
    }

    [Fact]
    public void AddStation_Validates_Input_And_Prevents_Invalid_Characters()
    {
        var manager = CreateManager();

        var (success1, _) = manager.AddStation("", "Valid Name");
        Assert.False(success1);

        var (success2, _) = manager.AddStation("valid-id", "");
        Assert.False(success2);

        // Path traversal / спецсимволы
        var (success3, _) = manager.AddStation("../invalid", "Invalid Path");
        Assert.False(success3);

        var (success4, _) = manager.AddStation("test station", "Spaces Not Allowed in ID");
        Assert.False(success4);

        var (success5, _) = manager.AddStation("valid_station-1", "Valid Station 1");
        Assert.True(success5);

        // Повторное добавление существующего ID
        var (success6, _) = manager.AddStation("valid_station-1", "Duplicate Station");
        Assert.False(success6);
    }

    [Fact]
    public void GetStation_Is_Case_Insensitive()
    {
        var manager = CreateManager();
        var station = manager.GetStation("ROCK");

        Assert.NotNull(station);
        Assert.Equal("rock", station.Id);
    }

    [Fact]
    public void RecordActivity_Maintains_Max_100_Entries()
    {
        var manager = CreateManager();

        for (int i = 0; i < 150; i++)
        {
            manager.RecordActivity($"127.0.0.{i}", "rock", $"Action {i}");
        }

        var stats = manager.GetAdminStats();
        Assert.NotNull(stats.RecentLogs);
        Assert.True(stats.RecentLogs.Count() <= 100);
    }

    [Fact]
    public void AddStreamedBytes_Accumulates_Correctly()
    {
        var manager = CreateManager();

        manager.AddStreamedBytes(1024);
        manager.AddStreamedBytes(2048);

        var stats = manager.GetAdminStats();
        Assert.Equal(3072, stats.TotalBytesSent);
    }
}
