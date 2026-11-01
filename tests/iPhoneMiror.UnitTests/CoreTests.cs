using iPhoneMiror.Core.Compatibility;
using iPhoneMiror.Core.Data;
using iPhoneMiror.Core.Models;
using iPhoneMiror.Core.Settings;
using iPhoneMiror.Core.Util;
using Microsoft.Extensions.Logging.Abstractions;

namespace iPhoneMiror.UnitTests;

public class CoreTests
{
    [Theory]
    [InlineData("26.0", 26, 0, 0)]
    [InlineData("26.0.1", 26, 0, 1)]
    [InlineData("27", 27, 0, 0)]
    public void IosVersion_Parses(string text, int major, int minor, int patch)
    {
        Assert.True(IosVersion.TryParse(text, out var v));
        Assert.Equal(new IosVersion(major, minor, patch), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("26.x")]
    [InlineData("1.2.3.4")]
    public void IosVersion_RejectsInvalid(string text) => Assert.False(IosVersion.TryParse(text, out _));

    [Fact]
    public void IosVersion_Compares()
    {
        Assert.True(new IosVersion(26, 1, 0) > new IosVersion(26, 0, 9));
        Assert.True(new IosVersion(27, 0, 0) > new IosVersion(26, 9, 9));
        Assert.Equal("26.0.1", new IosVersion(26, 0, 1).ToString());
        Assert.Equal("26.2", new IosVersion(26, 2, 0).ToString());
    }

    [Fact]
    public void Catalog_KnownAndUnknown()
    {
        Assert.Equal("iPhone 16 Pro", DeviceModelCatalog.GetMarketingName("iPhone17,1"));
        Assert.Equal("iPhone (iPhone99,9)", DeviceModelCatalog.GetMarketingName("iPhone99,9"));
        Assert.Equal("iPhone", DeviceModelCatalog.GetMarketingName(null));
    }

    [Fact]
    public void Compatibility_NothingVerifiedByDefault()
    {
        var eval = new CompatibilityEvaluator();
        var r26 = eval.Evaluate(new IosVersion(26, 1, 0));
        Assert.True(r26.IsTargetVersion);
        Assert.Contains("NOT been verified", r26.Summary);
        Assert.DoesNotContain(r26.Features, f => f.Support == FeatureSupport.Verified);
        Assert.Contains(r26.Features, f => f.Feature.StartsWith("Apple iPhone Mirroring") && f.Support == FeatureSupport.Unavailable);

        var r27 = eval.Evaluate(new IosVersion(27, 0, 0));
        Assert.True(r27.IsTargetVersion);
        Assert.DoesNotContain(r27.Features, f => f.Support == FeatureSupport.Verified);

        var old = eval.Evaluate(new IosVersion(18, 5, 0));
        Assert.False(old.IsTargetVersion);
        Assert.Contains("older", old.Summary);

        Assert.Contains("Trust", eval.Evaluate((IosVersion?)null).Summary);
    }

    [Fact]
    public void Compatibility_VerifiedListMarksVersion()
    {
        var eval = new CompatibilityEvaluator([new IosVersion(26, 1, 0)]);
        var r = eval.Evaluate(new IosVersion(26, 1, 2));
        Assert.Contains(r.Features, f => f.Support == FeatureSupport.Verified);
        Assert.Contains("verified on real hardware", r.Summary);
    }

    [Fact]
    public void ReconnectPolicy_GrowsAndCaps()
    {
        var p = new ReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), new Random(1));
        var d0 = p.GetDelay(0);
        var d3 = p.GetDelay(3);
        var d20 = p.GetDelay(20);
        Assert.InRange(d0.TotalMilliseconds, 1000, 1200);
        Assert.InRange(d3.TotalMilliseconds, 8000, 9600);
        Assert.Equal(10000, d20.TotalMilliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task SqliteRepository_UpsertsDevicesAndEvents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iphonemiror-test-{Guid.NewGuid():N}.db");
        try
        {
            var repo = SqliteDeviceRepository.ForFile(path);
            await repo.InitializeAsync();
            var dev = new DeviceInfo { Udid = "U1", Name = "Test iPhone", ProductType = "iPhone17,1", ProductVersion = "26.0", ConnectionType = ConnectionType.Usb };
            await repo.UpsertAsync(dev);
            await repo.UpsertAsync(dev with { Name = null, ProductVersion = "26.1", ConnectionType = ConnectionType.Network, LastSeen = DateTimeOffset.UtcNow.AddMinutes(1) });

            var rec = await repo.GetAsync("U1");
            Assert.NotNull(rec);
            Assert.Equal("Test iPhone", rec!.Name); // null does not overwrite
            Assert.Equal("26.1", rec.ProductVersion);
            Assert.Equal(ConnectionType.Network, rec.LastConnectionType);
            Assert.True(rec.AutoReconnect);

            await repo.SetAutoReconnectAsync("U1", false);
            Assert.False((await repo.GetAsync("U1"))!.AutoReconnect);

            await repo.AddEventAsync("U1", "attached", "USB");
            await repo.AddEventAsync("U1", "connected", null);
            var events = await repo.GetEventsAsync("U1");
            Assert.Equal(["connected", "attached"], events.Select(e => e.Kind));
            Assert.Single(await repo.GetRecentAsync());

            await repo.RemoveAsync("U1");
            Assert.Null(await repo.GetAsync("U1"));
            Assert.Empty(await repo.GetEventsAsync("U1"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task SettingsStore_PersistsChanges()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"iphonemiror-settings-{Guid.NewGuid():N}");
        var file = Path.Combine(dir, "settings.json");
        try
        {
            var store = new JsonSettingsStore(file, NullLogger<JsonSettingsStore>.Instance);
            await store.LoadAsync();
            Assert.Equal(27015, store.Current.Connection.UsbmuxPort);
            AppSettings? notified = null;
            store.Changed += (_, s) => notified = s;
            await store.UpdateAsync(s => { s.Performance.FrameRate = 45; s.Appearance.Theme = AppTheme.Light; });
            Assert.NotNull(notified);

            var again = new JsonSettingsStore(file, NullLogger<JsonSettingsStore>.Instance);
            await again.LoadAsync();
            Assert.Equal(45, again.Current.Performance.FrameRate);
            Assert.Equal(AppTheme.Light, again.Current.Appearance.Theme);
            Assert.Contains("\"Light\"", await File.ReadAllTextAsync(file));

            await File.WriteAllTextAsync(file, "{ broken");
            await again.LoadAsync();
            Assert.Equal(30, again.Current.Performance.FrameRate);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DeviceInfo_DisplayNameFallsBackToModel()
    {
        var d = new DeviceInfo { Udid = "x", ProductType = "iPhone16,2" };
        Assert.Equal("iPhone 15 Pro Max", d.DisplayName);
        Assert.Equal("Bob", (d with { Name = "Bob" }).DisplayName);
        Assert.Equal(new IosVersion(26, 0, 1), (d with { ProductVersion = "26.0.1" }).IosVersion);
    }
}
