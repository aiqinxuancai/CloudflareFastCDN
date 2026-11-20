using System.Net;
using CloudflareFastCDN.Utils;
using Xunit;

namespace CloudflareFastCDN.Tests;

public sealed class SubnetCacheTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"CloudflareFastCDN.SubnetTests-{Guid.NewGuid():N}");
    private readonly string _ipFile;
    private readonly string _cacheFile;

    public SubnetCacheTests()
    {
        Directory.CreateDirectory(_testDirectory);
        _ipFile = Path.Combine(_testDirectory, "ip.txt");
        _cacheFile = Path.Combine(_testDirectory, "subnet_cache.json");
        File.WriteAllLines(_ipFile, new[] { "104.16.0.0/13", "172.64.0.0/13", "198.41.128.0/25" });
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private SubnetCache CreateCache()
    {
        var cache = new SubnetCache(_ipFile, _cacheFile);
        cache.Load();
        return cache;
    }

    [Theory]
    [InlineData("104.16.1.0/24", "104.16.1.0/24")]
    [InlineData("104.16.1.77", "104.16.1.0/24")]
    [InlineData(" 104.16.1.77 / 24 ", "104.16.1.0/24")]
    public void NormalizesIpOrSubnetTo24(string input, string expected)
    {
        Assert.True(SubnetCache.TryNormalizeSubnet24(input, out var subnet, out _));
        Assert.Equal(expected, subnet);
    }

    [Theory]
    [InlineData("")]
    [InlineData("104.16.0.0/16")]
    [InlineData("104.16.1.0/32")]
    [InlineData("104.16.1")]
    [InlineData("256.1.1.1")]
    [InlineData("::1")]
    [InlineData("0x68.16.1.1")]
    [InlineData("cdn.example.com")]
    public void RejectsInvalidSubnets(string input)
    {
        Assert.False(SubnetCache.TryNormalizeSubnet24(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ManualSubnetMustBeCoveredByIpListWithFull24()
    {
        var cache = CreateCache();

        Assert.False(cache.AddManual("8.8.8.0/24").Ok);
        Assert.False(cache.AddManual("198.41.128.0/24").Ok); // ip.txt only covers a /25
        Assert.True(cache.AddManual("104.17.3.9").Ok);
        Assert.Equal("104.17.3.0/24", Assert.Single(cache.Snapshot()).Subnet);
    }

    [Fact]
    public void ManualSubnetsComeFirstAndPersist()
    {
        var cache = CreateCache();
        cache.AddOrUpdate("104.16.1.0/24", 80);
        cache.AddOrUpdate("104.16.2.0/24", 60);
        var versionBefore = cache.Version;

        Assert.True(cache.AddManual("172.64.5.0/24").Ok);
        Assert.False(cache.AddManual("172.64.5.200").Ok); // duplicate after normalization
        Assert.True(cache.Version > versionBefore);

        var reloaded = CreateCache().Snapshot();
        Assert.Equal(new[] { "172.64.5.0/24", "104.16.2.0/24", "104.16.1.0/24" }, reloaded.Select(item => item.Subnet));
        Assert.True(reloaded[0].Manual);
        Assert.Equal(0, reloaded[0].DelayMs);
        Assert.NotNull(reloaded[0].AddedAt);
        Assert.False(reloaded[1].Manual);

        var sampled = cache.RandomSampleIPs(1);
        Assert.Equal("172.64.5.0/24", Assert.Single(sampled).Subnet);
    }

    [Fact]
    public void ManualSubnetKeepsPriorityAfterMeasurement()
    {
        var cache = CreateCache();
        cache.AddOrUpdate("104.16.1.0/24", 50);
        cache.AddManual("104.16.9.0/24");

        cache.AddOrUpdate("104.16.9.0/24", 120);

        var snapshot = cache.Snapshot();
        Assert.Equal("104.16.9.0/24", snapshot[0].Subnet);
        Assert.Equal(120, snapshot[0].DelayMs);
        Assert.True(snapshot[0].Manual);
    }

    [Fact]
    public void AddingManualSubnetToFullCacheEvictsSlowestAutomaticSubnet()
    {
        var cache = CreateCache();
        for (var i = 0; i < SubnetCache.MaxSubnets; i++)
        {
            cache.AddOrUpdate($"104.16.{i}.0/24", 10 + i);
        }

        var result = cache.AddManual("104.20.1.0/24");

        Assert.True(result.Ok);
        Assert.Contains("104.16.9.0/24", result.Message);
        var snapshot = cache.Snapshot();
        Assert.Equal(SubnetCache.MaxSubnets, snapshot.Count);
        Assert.Equal("104.20.1.0/24", snapshot[0].Subnet);
        Assert.DoesNotContain(snapshot, item => item.Subnet == "104.16.9.0/24");
    }

    [Fact]
    public void ManualSubnetsAreNeverPushedOutByAutomaticOnes()
    {
        var cache = CreateCache();
        for (var i = 0; i < SubnetCache.MaxSubnets; i++)
        {
            Assert.True(cache.AddManual($"104.16.{i}.0/24").Ok);
        }

        Assert.False(cache.AddManual("104.20.1.0/24").Ok);

        cache.AddOrUpdate("104.20.2.0/24", 1);
        var snapshot = cache.Snapshot();
        Assert.Equal(SubnetCache.MaxSubnets, snapshot.Count);
        Assert.All(snapshot, item => Assert.True(item.Manual));
    }

    [Fact]
    public void RemoveDeletesAnySubnetAndPersists()
    {
        var cache = CreateCache();
        cache.AddOrUpdate("104.16.1.0/24", 80);
        cache.AddManual("104.16.2.0/24");

        Assert.True(cache.Remove("104.16.1.0/24").Ok);
        Assert.True(cache.Remove("104.16.2.5").Ok);
        Assert.False(cache.Remove("104.16.3.0/24").Ok);

        Assert.Empty(CreateCache().Snapshot());
    }

    [Fact]
    public void TcpPingOutcomesIgnoreSubnetsRemovedDuringTheRun()
    {
        var cache = CreateCache();
        cache.AddOrUpdate("104.16.1.0/24", 80);
        cache.AddOrUpdate("104.16.2.0/24", 90);
        var sampled = cache.RandomSampleIPs(2);

        cache.Remove("104.16.1.0/24");
        var evicted = cache.RecordTcpPingOutcomes(sampled, Array.Empty<IPAddress>(), evictThreshold: 1);

        Assert.True(evicted);
        Assert.Empty(cache.Snapshot());
    }

    [Fact]
    public void LoadsLegacyCacheFile()
    {
        File.WriteAllText(_cacheFile, """
            [
              { "Subnet": "104.16.2.0/24", "DelayMs": 90.5, "ConsecutiveFailures": 1 },
              { "Subnet": "104.16.1.0/24", "DelayMs": 70.25, "ConsecutiveFailures": 0 }
            ]
            """);

        var snapshot = CreateCache().Snapshot();

        Assert.Equal(new[] { "104.16.1.0/24", "104.16.2.0/24" }, snapshot.Select(item => item.Subnet));
        Assert.Equal(1, snapshot[1].ConsecutiveFailures);
        Assert.All(snapshot, item =>
        {
            Assert.False(item.Manual);
            Assert.Null(item.AddedAt);
        });
    }
}
