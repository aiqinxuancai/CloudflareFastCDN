using System.Text.Json;
using CloudflareFastCDN.Services;
using CloudflareFastCDN.Settings;
using CloudflareFastCDN.Utils;
using Xunit;

namespace CloudflareFastCDN.Tests;

public sealed class SettingsTests : IDisposable
{
    private const string SettingsFile = "settings.json";
    private readonly string _originalDirectory = Directory.GetCurrentDirectory();
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"CloudflareFastCDN.SettingsTests-{Guid.NewGuid():N}");

    public SettingsTests()
    {
        Directory.CreateDirectory(_testDirectory);
        Directory.SetCurrentDirectory(_testDirectory);
    }

    [Fact]
    public void EnvironmentVariablesAreMigratedOnceIntoSettingsFile()
    {
        var environment = new Dictionary<string, string>
        {
            ["CLOUDFLARE_KEY"] = "cf-token-1234",
            ["DOMAINS"] = "a.example.com, B.example.com.",
            ["CLOUDFLARE_DOMAINS2"] = "c.example.com",
            ["CLOUDFLARE_DOMAINS3"] = "a.example.com",
            ["TENCENTCLOUD_SECRET_ID"] = "tc-id",
            ["TENCENTCLOUD_SECRET_KEY"] = "tc-key",
            ["TENCENTCLOUD_DOMAINS3"] = "t.example.cn",
            ["MAX_IPS"] = "123",
            ["BANDWIDTH_PRIORITY"] = "true",
            ["HTTP_PROBE_HEADERS"] = "X-Probe:one;X-Token:two",
            ["WEB_PORT"] = "8080",
            ["PING_THREADS"] = "not-a-number"
        };

        SettingsManager.Initialize(new[] { "--RUN_MINUTES=15" }, isDocker: false, environment.GetValueOrDefault);

        var settings = SettingsManager.Current;
        Assert.True(File.Exists(SettingsFile));
        Assert.Equal(AppSettings.CurrentVersion, JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement.GetProperty("version").GetInt32());
        Assert.Equal("cf-token-1234", settings.Dns.Cloudflare.ApiToken);
        Assert.Equal(new[] { ("a.example.com", (int?)1), ("b.example.com", 1), ("c.example.com", 2) },
            settings.Dns.Cloudflare.Domains.Select(domain => (domain.Name, domain.Rank)));
        Assert.Equal(("t.example.cn", (int?)3), Assert.Single(settings.Dns.TencentCloud.Domains.Select(domain => (domain.Name, domain.Rank))));
        Assert.Equal(123, settings.Selection.MaxIps);
        Assert.Equal(8, settings.Selection.PingThreads);
        Assert.True(settings.Selection.BandwidthPriority);
        Assert.Equal("two", settings.Http.ProbeHeaders["X-Token"]);
        Assert.Equal(15, settings.Schedule.RunMinutes);
        Assert.Equal(8080, AppConfig.WebPort);

        // 迁移只进行一次：之后环境变量不再生效
        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false,
            new Dictionary<string, string> { ["CLOUDFLARE_KEY"] = "other", ["MAX_IPS"] = "999" }.GetValueOrDefault);
        Assert.Equal("cf-token-1234", SettingsManager.Current.Dns.Cloudflare.ApiToken);
        Assert.Equal(123, SettingsManager.Current.Selection.MaxIps);
    }

    [Fact]
    public void DockerIgnoresCommandLineArguments()
    {
        SettingsManager.Initialize(new[] { "--RUN_MINUTES=15" }, isDocker: true, _ => null);

        Assert.Equal(60, SettingsManager.Current.Schedule.RunMinutes);
        Assert.Equal("0.0.0.0", AppConfig.WebHost);
    }

    [Fact]
    public void LegacySettingsFileIsMigratedWithEnvironmentTakingPrecedence()
    {
        File.WriteAllText(SettingsFile, """
            {
              "values": {
                "CLOUDFLARE_KEY": "file-token",
                "CLOUDFLARE_DOMAINS": "a.example.com",
                "MAX_IPS": "200",
                "RUN_MINUTES": "30"
              }
            }
            """);

        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false,
            new Dictionary<string, string> { ["MAX_IPS"] = "300" }.GetValueOrDefault);

        var settings = SettingsManager.Current;
        Assert.Equal(300, settings.Selection.MaxIps);
        Assert.Equal(30, settings.Schedule.RunMinutes);
        Assert.Equal("file-token", settings.Dns.Cloudflare.ApiToken);
        Assert.Equal(1, Assert.Single(settings.Dns.Cloudflare.Domains).Rank);
        Assert.True(File.Exists(SettingsFile + ".v1.bak"));
        Assert.Contains("\"version\"", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void HandEditedSettingsFileIsSanitized()
    {
        File.WriteAllText(SettingsFile, """
            {
              // 手工编辑的配置文件
              "version": 2,
              "selection": { "maxIps": 0, "pingThreads": "16", },
              "http": { "probeUrl": "ftp://example.com" },
              "dns": {
                "cloudflare": {
                  "apiToken": "token",
                  "domains": [
                    { "name": "Bad Domain", "rank": 1 },
                    { "name": "ok.example.com", "rank": 99 },
                    { "name": "OK.example.com.", "rank": 2 },
                    { "name": "top2.example.com", "rank": 2 }
                  ]
                }
              }
            }
            """);

        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false, _ => null);

        var settings = SettingsManager.Current;
        Assert.Equal(400, settings.Selection.MaxIps);
        Assert.Equal(16, settings.Selection.PingThreads);
        Assert.Equal("https://www.visa.cn/", settings.Http.ProbeUrl);
        Assert.Equal("admin", settings.Web.Username);
        Assert.Equal(new[] { ("ok.example.com", (int?)null), ("top2.example.com", 2) },
            settings.Dns.Cloudflare.Domains.Select(domain => (domain.Name, domain.Rank)));
        // 读取时不会重写用户的文件
        Assert.Contains("手工编辑", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void InvalidJsonIsBackedUpAndReplaced()
    {
        File.WriteAllText(SettingsFile, "{ not json");

        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false,
            new Dictionary<string, string> { ["RUN_MINUTES"] = "5" }.GetValueOrDefault);

        Assert.Equal("{ not json", File.ReadAllText(SettingsFile + ".invalid"));
        Assert.Equal(5, SettingsManager.Current.Schedule.RunMinutes);
        Assert.Contains("\"version\"", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void OnlyRankedDomainChangesTriggerReselection()
    {
        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false, _ => null);

        var added = Update(domains: new[] { Domain("Cloudflare", "a.example.com", null) });
        Assert.True(added.Ok);
        Assert.False(added.DnsChanged);
        Assert.Equal("a.example.com", Assert.Single(SettingsManager.Current.Dns.Cloudflare.Domains).Name);
        Assert.Empty(SettingsManager.GetDnsTargets());

        var ranked = Update(domains: new[] { Domain("Cloudflare", "a.example.com", 5) });
        Assert.True(ranked.DnsChanged);
        Assert.Equal(new DnsTarget("Cloudflare", "a.example.com", 5), Assert.Single(SettingsManager.GetDnsTargets()));

        Assert.True(Update(new() { ["dns.cloudflare.apiToken"] = "token" }).DnsChanged);
        // 没有已排名域名的服务商，凭证变化不影响当前的DNS更新
        Assert.False(Update(new() { ["dns.tencentCloud.secretId"] = "id" }).DnsChanged);
        Assert.False(Update(new() { ["selection.maxIps"] = "100" }).DnsChanged);

        var unchanged = Update(domains: new[] { Domain("Cloudflare", "A.EXAMPLE.COM", 5) });
        Assert.Equal("设置没有变化", unchanged.Message);

        var saved = JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement;
        var domain = saved.GetProperty("dns").GetProperty("cloudflare").GetProperty("domains")[0];
        Assert.Equal("a.example.com", domain.GetProperty("name").GetString());
        Assert.Equal(5, domain.GetProperty("rank").GetInt32());
    }

    [Theory]
    [InlineData("Cloudflare", "a.example.com", 0)]
    [InlineData("Cloudflare", "a.example.com", SettingDefinitions.MaxRank + 1)]
    [InlineData("Cloudflare", "not a domain", 1)]
    [InlineData("UnknownCloud", "a.example.com", 1)]
    public void InvalidDomainsAreRejectedWithoutSaving(string provider, string name, int rank)
    {
        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false, _ => null);
        var before = File.ReadAllText(SettingsFile);

        var response = Update(new() { ["selection.maxIps"] = "100" }, new[] { Domain(provider, name, rank) });

        Assert.False(response.Ok);
        Assert.Contains("domains", response.Errors.Keys);
        Assert.Equal(400, SettingsManager.Current.Selection.MaxIps);
        Assert.Equal(before, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public void DuplicateDomainsInSameProviderAreRejected()
    {
        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false, _ => null);

        var response = Update(domains: new[] { Domain("Cloudflare", "a.example.com", 1), Domain("Cloudflare", "A.example.com", 2) });
        Assert.False(response.Ok);

        // 不同服务商可以各自维护同名域名
        Assert.True(Update(domains: new[] { Domain("Cloudflare", "a.example.com", 1), Domain("TencentCloud", "a.example.com", 2) }).Ok);
    }

    [Fact]
    public void CandidateCountsAreSavedAndValidatedTogether()
    {
        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false, _ => null);
        Assert.Equal(10, AppConfig.HttpCandidateCount);
        Assert.Equal(3, AppConfig.CachedCandidateCount);

        Assert.True(Update(new() { ["selection.httpCandidateCount"] = "6", ["selection.cachedCandidateCount"] = "0" }).Ok);
        Assert.Equal(6, AppConfig.HttpCandidateCount);
        Assert.Equal(0, AppConfig.CachedCandidateCount);

        // 子网缓存名额不能超过候选总数
        var cachedTooLarge = Update(new() { ["selection.cachedCandidateCount"] = "7" });
        Assert.False(cachedTooLarge.Ok);
        Assert.Contains("selection.cachedCandidateCount", cachedTooLarge.Errors.Keys);
        var candidateTooSmall = Update(new() { ["selection.httpCandidateCount"] = "2", ["selection.cachedCandidateCount"] = "0" });
        Assert.True(candidateTooSmall.Ok);
        var shrinkBelowCache = Update(new() { ["selection.cachedCandidateCount"] = "2" });
        Assert.True(shrinkBelowCache.Ok);
        var shrinkCandidates = Update(new() { ["selection.httpCandidateCount"] = "1" });
        Assert.False(shrinkCandidates.Ok);
        Assert.Contains("selection.httpCandidateCount", shrinkCandidates.Errors.Keys);
    }

    [Fact]
    public void RanksCannotExceedCandidateCount()
    {
        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false, _ => null);
        Assert.True(Update(new() { ["selection.httpCandidateCount"] = "4", ["selection.cachedCandidateCount"] = "1" }).Ok);

        // 改排名时错误挂在域名上
        var rankTooHigh = Update(domains: new[] { Domain("Cloudflare", "a.example.com", 5) });
        Assert.False(rankTooHigh.Ok);
        Assert.Contains("Top5", rankTooHigh.Errors["domains"]);

        Assert.True(Update(domains: new[] { Domain("Cloudflare", "a.example.com", 4) }).Ok);

        // 调小候选数时错误挂在该设置项上
        var candidatesTooFew = Update(new() { ["selection.httpCandidateCount"] = "3" });
        Assert.False(candidatesTooFew.Ok);
        Assert.Contains("Top4", candidatesTooFew.Errors["selection.httpCandidateCount"]);
        Assert.Equal(4, SettingsManager.Current.Selection.HttpCandidateCount);

        // 同时调大候选数和名次可以一起保存
        Assert.True(Update(new() { ["selection.httpCandidateCount"] = "8" }, new[] { Domain("Cloudflare", "a.example.com", 8) }).Ok);
    }

    [Fact]
    public void InconsistentCandidateCountsInFileAreClamped()
    {
        File.WriteAllText(SettingsFile, """{ "version": 2, "selection": { "httpCandidateCount": 4, "cachedCandidateCount": 9 } }""");

        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false, _ => null);

        Assert.Equal(4, SettingsManager.Current.Selection.HttpCandidateCount);
        Assert.Equal(4, SettingsManager.Current.Selection.CachedCandidateCount);
    }

    [Fact]
    public void IpListIsFoundRegardlessOfWorkingDirectory()
    {
        // 测试的工作目录是一个空的临时目录，旧版本在这里读取 ip.txt 会直接抛出异常
        Assert.False(File.Exists(AppPaths.IpListFileName));
        Assert.Equal(AppPaths.BundledFile(AppPaths.IpListFileName), AppPaths.IpListFile);
        Assert.NotEmpty(new IPProcessor().LoadIPRanges());

        // 数据目录中的副本（更新下载或自定义的列表）优先
        File.WriteAllText(AppPaths.IpListFileName, "198.51.100.0/24\n");
        Assert.Equal(AppPaths.IpListFileName, AppPaths.IpListFile);
        Assert.All(new IPProcessor().LoadIPRanges(), ip => Assert.StartsWith("198.51.100.", ip.ToString()));
    }

    [Fact]
    public void MissingIpListYieldsNoIpsInsteadOfCrashing()
    {
        Assert.Empty(new IPProcessor { IPFile = "missing-ip.txt" }.LoadIPRanges());
    }

    [Fact]
    public void DnsSetupRequiresRankedDomainsAndCredentials()
    {
        var settings = new AppSettings();
        Assert.False(DnsSetup.TryCreate(settings, out _, out var error));
        Assert.Contains("尚未添加域名", error);

        settings.Dns.Cloudflare.Domains.Add(new DomainEntry { Name = "a.example.com" });
        settings.Dns.TencentCloud.Domains.Add(new DomainEntry { Name = "b.example.cn" });
        Assert.False(DnsSetup.TryCreate(settings, out _, out error));
        Assert.Contains("还没有为任何域名设置更新排名", error);

        settings.Dns.Cloudflare.Domains[0].Rank = 2;
        Assert.False(DnsSetup.TryCreate(settings, out _, out error));
        Assert.Contains("API Token", error);

        // 未排名域名所属的服务商不需要凭证
        settings.Dns.Cloudflare.ApiToken = "token";
        Assert.True(DnsSetup.TryCreate(settings, out var setup, out _));
        Assert.Equal("Cloudflare", Assert.Single(setup.Providers).ProviderName);
        Assert.Equal(new DnsTarget("Cloudflare", "a.example.com", 2), Assert.Single(setup.Targets));
    }

    [Fact]
    public void TargetsAreOrderedByRankThenProvider()
    {
        var settings = new AppSettings();
        settings.Dns.AlibabaCloud.Domains.Add(new DomainEntry { Name = "ali.example.com", Rank = 1 });
        settings.Dns.Cloudflare.Domains.Add(new DomainEntry { Name = "cf-top3.example.com", Rank = 3 });
        settings.Dns.Cloudflare.Domains.Add(new DomainEntry { Name = "cf-top1.example.com", Rank = 1 });
        settings.Dns.Cloudflare.Domains.Add(new DomainEntry { Name = "cf-unranked.example.com" });

        Assert.Equal(new[]
        {
            new DnsTarget("Cloudflare", "cf-top1.example.com", 1),
            new DnsTarget("AlibabaCloud", "ali.example.com", 1),
            new DnsTarget("Cloudflare", "cf-top3.example.com", 3)
        }, DnsProviders.GetTargets(settings));
    }

    [Fact]
    public void SecretsAreNotReturnedBySettingsApi()
    {
        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false,
            new Dictionary<string, string> { ["CLOUDFLARE_KEY"] = "super-secret-token", ["WEB_PASSWORD"] = "hunter2" }.GetValueOrDefault);

        var json = JsonSerializer.Serialize(SettingsManager.GetSettings(), SettingsJsonContext.Default.SettingsResponse);
        Assert.DoesNotContain("super-secret-token", json);
        Assert.DoesNotContain("hunter2", json);
        Assert.Contains("supe***", json);
    }

    private static SettingsUpdateResponse Update(Dictionary<string, string?>? values = null, DomainItem[]? domains = null) =>
        SettingsManager.Update(new SettingsUpdateRequest { Values = values ?? new(), Domains = domains?.ToList() });

    private static DomainItem Domain(string provider, string name, int? rank) => new() { Provider = provider, Name = name, Rank = rank };

    public void Dispose()
    {
        // 恢复默认设置，避免影响其他测试
        File.Delete(SettingsFile);
        SettingsManager.Initialize(Array.Empty<string>(), isDocker: false, _ => null);
        Directory.SetCurrentDirectory(_originalDirectory);
        Directory.Delete(_testDirectory, recursive: true);
    }
}
