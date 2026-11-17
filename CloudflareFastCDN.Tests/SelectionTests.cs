using System.Net;
using System.Text.Json;
using CloudflareFastCDN.Services;
using CloudflareFastCDN.Utils;
using CloudflareFastCDN.Web;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CloudflareFastCDN.Tests;

public sealed class SelectionTests : IDisposable
{
    private readonly string _originalDirectory = Directory.GetCurrentDirectory();
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"CloudflareFastCDN.Tests-{Guid.NewGuid():N}");

    public SelectionTests()
    {
        Directory.CreateDirectory(_testDirectory);
        Directory.SetCurrentDirectory(_testDirectory);
        RuntimeControl.ClearPending();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevalidationChecksEveryIpAndCollectsAllFailures(bool manual)
    {
        var selection = CreateSelection();
        var checkedIps = new List<IPAddress>();
        DashboardState.SetAssignedIps(selection.AssignedIps.Select(item => (item.Rank, item.IP.ToString(), 100d)));

        var failures = await Program.ValidateAssignedIps(selection.AssignedIps, manual, ip =>
        {
            checkedIps.Add(ip);
            var result = ip.Equals(selection.AssignedIps[0].IP)
                ? (2, TimeSpan.FromMilliseconds(100), "HTTP失败")
                : ip.Equals(selection.AssignedIps[1].IP)
                    ? (3, TimeSpan.FromMilliseconds(240), string.Empty)
                    : (3, TimeSpan.FromMilliseconds(900), string.Empty);
            return Task.FromResult(result);
        }, _ => Task.CompletedTask);

        Assert.Equal(selection.AssignedIps.Select(item => item.IP), checkedIps);
        Assert.True(failures.SetEquals(new[] { selection.AssignedIps[0].IP, selection.AssignedIps[2].IP }));
        Assert.Equal(TimeSpan.FromMilliseconds(100), selection.AssignedIps[0].BaselineDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(80), selection.AssignedIps[1].BaselineDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(100), selection.AssignedIps[2].BaselineDelay);
        var status = JsonSerializer.Deserialize(DashboardState.GetStatusJson(), WebJsonContext.Default.StatusResponse)!;
        Assert.False(status.AssignedIps[0].LastCheckOk);
        Assert.True(status.AssignedIps[1].LastCheckOk);
        Assert.False(status.AssignedIps[2].LastCheckOk);
    }

    [Theory]
    [InlineData(100, 299, false)]
    [InlineData(100, 300, true)]
    [InlineData(400, 799, false)]
    [InlineData(400, 800, true)]
    public async Task RevalidationKeepsExistingDelayThresholds(double baseline, double current, bool invalid)
    {
        var assigned = CreateAssigned(1);
        assigned.BaselineDelay = TimeSpan.FromMilliseconds(baseline);
        var failures = await Program.ValidateAssignedIps(new[] { assigned }, false,
            _ => Task.FromResult((3, TimeSpan.FromMilliseconds(current * 3), string.Empty)),
            _ => Task.CompletedTask);

        Assert.Equal(invalid, failures.Contains(assigned.IP));
    }

    [Fact]
    public void HealthyAssignmentsNeedNoDnsUpdates()
    {
        var selection = CreateSelection();
        Assert.Empty(selection.GetTargetsToUpdate(GetTargets(selection), new HashSet<IPAddress>()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OnlyFailedRankIsUpdatedAndHealthyAssignmentsAreKept(int failedRank)
    {
        var selection = CreateSelection();
        var failed = selection.AssignedIps[failedRank - 1];
        var targets = selection.GetTargetsToUpdate(GetTargets(selection), new HashSet<IPAddress> { failed.IP });
        var manager = new FakeDnsManager();
        var replacement = Candidate(10);

        var updates = await Program.UpdateDnsAssignments(Providers(manager), targets, new[] { replacement });
        var merged = selection.Merge(updates);

        Assert.Equal((failed.Targets[0].Domain, replacement.IP.ToString()), Assert.Single(manager.Updates));
        Assert.Equal(replacement.IP, merged.AssignedIps.Single(item => item.Rank == failedRank).IP);
        foreach (var healthy in selection.AssignedIps.Where(item => item.Rank != failedRank))
        {
            Assert.Same(healthy, merged.AssignedIps.Single(item => item.Rank == healthy.Rank));
        }
    }

    [Fact]
    public async Task MultipleFailedRanksUseDistinctCandidatesWithoutMovingHealthyRank()
    {
        var selection = CreateSelection();
        var targets = selection.GetTargetsToUpdate(GetTargets(selection),
            new HashSet<IPAddress> { selection.AssignedIps[0].IP, selection.AssignedIps[2].IP });
        var manager = new FakeDnsManager();
        var updates = await Program.UpdateDnsAssignments(Providers(manager), targets, new[] { Candidate(10), Candidate(11) });
        var merged = selection.Merge(updates);

        Assert.Equal(2, manager.Updates.Count);
        Assert.Equal(Candidate(10).IP, merged.AssignedIps[0].IP);
        Assert.Same(selection.AssignedIps[1], merged.AssignedIps[1]);
        Assert.Equal(Candidate(11).IP, merged.AssignedIps[2].IP);
    }

    [Fact]
    public async Task MissingRankIsFilledWithFirstAvailableCandidate()
    {
        var complete = CreateSelection();
        var selection = new SelectionResult(complete.AssignedIps.Take(2).ToList());
        var targets = selection.GetTargetsToUpdate(GetTargets(complete), new HashSet<IPAddress>());
        var manager = new FakeDnsManager();
        var updates = await Program.UpdateDnsAssignments(Providers(manager), targets, new[] { Candidate(10) });
        var merged = selection.Merge(updates);

        Assert.Equal(3, Assert.Single(targets).Rank);
        Assert.Single(manager.Updates);
        Assert.Equal(Candidate(10).IP, merged.AssignedIps[2].IP);
        Assert.Same(selection.AssignedIps[0], merged.AssignedIps[0]);
        Assert.Same(selection.AssignedIps[1], merged.AssignedIps[1]);
    }

    [Fact]
    public async Task HealthyDomainInSameRankIsNotUpdated()
    {
        var healthy = CreateAssigned(2, domain: "healthy.example.com");
        var failed = CreateAssigned(2, domain: "failed.example.com", ipSuffix: 20);
        var selection = new SelectionResult(new[] { healthy, failed });
        var targets = selection.GetTargetsToUpdate(GetTargets(selection), new HashSet<IPAddress> { failed.IP });
        var manager = new FakeDnsManager();
        var updates = await Program.UpdateDnsAssignments(Providers(manager), targets, new[] { Candidate(10) });

        Assert.Equal("failed.example.com", Assert.Single(manager.Updates).Domain);
        Assert.Contains(healthy, selection.Merge(updates).AssignedIps);
    }

    [Fact]
    public void SharedFailedIpIncludesEveryAffectedProviderAndRank()
    {
        var ip = IPAddress.Parse("192.0.2.1");
        var cloudflareTarget = new DnsTarget("Cloudflare", "cf.example.com", 1);
        var tencentTarget = new DnsTarget("TencentCloud", "tc.example.com", 3);
        var selection = new SelectionResult(new[]
        {
            new AssignedIp(ip, TimeSpan.FromMilliseconds(100), 1, new[] { cloudflareTarget }),
            new AssignedIp(ip, TimeSpan.FromMilliseconds(100), 3, new[] { tencentTarget }),
            CreateAssigned(2)
        });

        var targets = selection.GetTargetsToUpdate(GetTargets(selection), new HashSet<IPAddress> { ip });
        Assert.Equal(new[] { cloudflareTarget, tencentTarget }, targets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialDnsFailureRetainsOldAssignmentForFailedDomain(bool throwOnFailure)
    {
        var healthy = CreateAssigned(1);
        var first = new DnsTarget("Cloudflare", "updated.example.com", 2);
        var second = new DnsTarget("Cloudflare", "failed.example.com", 2);
        var failed = new AssignedIp(IPAddress.Parse("192.0.2.2"), TimeSpan.FromMilliseconds(125), 2, new[] { first, second });
        var selection = new SelectionResult(new[] { healthy, failed });
        var manager = new FakeDnsManager { FailedDomain = second.Domain, ThrowOnFailure = throwOnFailure };
        var targets = selection.GetTargetsToUpdate(GetTargets(selection), new HashSet<IPAddress> { failed.IP });

        var updates = await Program.UpdateDnsAssignments(Providers(manager), targets, new[] { Candidate(10) });
        var merged = selection.Merge(updates);

        Assert.Equal(2, manager.Updates.Count);
        Assert.Same(healthy, merged.AssignedIps[0]);
        var retained = merged.AssignedIps.Single(item => item.IP.Equals(failed.IP));
        Assert.Equal(failed.BaselineDelay, retained.BaselineDelay);
        Assert.Equal(second, Assert.Single(retained.Targets));
        Assert.Equal(first, Assert.Single(Assert.Single(updates.AssignedIps).Targets));
        Assert.Equal(new[] { second }, merged.GetTargetsToUpdate(GetTargets(selection), new HashSet<IPAddress> { failed.IP }));
    }

    [Fact]
    public async Task InsufficientCandidatesRetainUnreplacedAssignment()
    {
        var selection = CreateSelection();
        var targets = selection.GetTargetsToUpdate(GetTargets(selection), selection.AssignedIps.Select(item => item.IP).ToHashSet());
        var manager = new FakeDnsManager();
        var updates = await Program.UpdateDnsAssignments(Providers(manager), targets, new[] { Candidate(10) });
        var merged = selection.Merge(updates);

        Assert.Single(manager.Updates);
        Assert.Equal(Candidate(10).IP, merged.AssignedIps[0].IP);
        Assert.Same(selection.AssignedIps[1], merged.AssignedIps[1]);
        Assert.Same(selection.AssignedIps[2], merged.AssignedIps[2]);
        Assert.Equal(selection.AssignedIps, selection.Merge(SelectionResult.Empty).AssignedIps);
    }

    [Fact]
    public async Task FullSelectionStillUsesOriginalCandidateRank()
    {
        var target = new DnsTarget("Cloudflare", "top3.example.com", 3);
        var manager = new FakeDnsManager();
        var candidates = new[] { Candidate(10), Candidate(11), Candidate(12) };
        var updates = await Program.UpdateDnsAssignments(Providers(manager), new[] { target }, candidates, fullSelection: true);

        Assert.Equal(candidates[2].IP, Assert.Single(updates.AssignedIps).IP);
        Assert.Equal((target.Domain, candidates[2].IP.ToString()), Assert.Single(manager.Updates));
    }

    [Fact]
    public async Task FullSelectionSupportsRanksBeyondTop3()
    {
        var targets = new[] { new DnsTarget("Cloudflare", "top1.example.com", 1), new DnsTarget("Cloudflare", "top6.example.com", 6) };
        var manager = new FakeDnsManager();
        var candidates = Enumerable.Range(10, 6).Select(Candidate).ToArray();
        var updates = await Program.UpdateDnsAssignments(Providers(manager), targets, candidates, fullSelection: true);

        Assert.Equal(new[] { candidates[0].IP, candidates[5].IP }, updates.AssignedIps.Select(item => item.IP));
        Assert.Equal(new[] { 1, 6 }, updates.AssignedIps.Select(item => item.Rank));
    }

    [Theory]
    // 默认 10 个候选，其中 3 个名额留给子网缓存
    [InlineData(12, 3, 10, 3, 7, 3)]
    // 子网缓存只通过 1 个时，空出的名额由 ICMP 结果补足
    [InlineData(12, 1, 10, 3, 9, 1)]
    // 不使用子网缓存
    [InlineData(12, 3, 10, 0, 10, 0)]
    // ICMP 结果不足时不凑数
    [InlineData(4, 3, 10, 3, 4, 3)]
    // 名额全部留给子网缓存，缓存不足时仍由 ICMP 补足
    [InlineData(12, 2, 5, 5, 3, 2)]
    public void HttpCandidatesUseConfiguredCounts(int icmpPassed, int cachedPassed, int candidateCount, int cachedCount,
        int expectedIcmp, int expectedCached)
    {
        var icmp = Enumerable.Range(1, icmpPassed).Select(Candidate).ToList();
        var cached = Enumerable.Range(200, cachedPassed).Select(Candidate).ToList();

        var candidates = Program.ComposeHttpCandidates(icmp, cached, candidateCount, cachedCount);

        Assert.Equal(expectedIcmp, candidates.Count(candidate => icmp.Any(item => item.IP.Equals(candidate.IP))));
        Assert.Equal(expectedCached, candidates.Count(candidate => cached.Any(item => item.IP.Equals(candidate.IP))));
        // ICMP 结果按原有顺序取用
        Assert.Equal(icmp.Take(expectedIcmp).Select(item => item.IP),
            candidates.Where(candidate => icmp.Any(item => item.IP.Equals(candidate.IP))).Select(candidate => candidate.IP));
    }

    [Fact]
    public void CachedCandidateAlreadyInIcmpResultsIsNotDuplicated()
    {
        var icmp = Enumerable.Range(1, 12).Select(Candidate).ToList();
        var cached = new List<PingData> { Candidate(2), Candidate(200) };

        var candidates = Program.ComposeHttpCandidates(icmp, cached, 10, 3);

        Assert.Equal(10, candidates.Count);
        Assert.Equal(candidates.Count, candidates.Select(candidate => candidate.IP).Distinct().Count());
    }

    [Fact]
    public async Task PartialSelectionFillsHighRanksWithNextAvailableCandidates()
    {
        var targets = new[] { new DnsTarget("Cloudflare", "top4.example.com", 4), new DnsTarget("Cloudflare", "top8.example.com", 8) };
        var manager = new FakeDnsManager();
        var updates = await Program.UpdateDnsAssignments(Providers(manager), targets, new[] { Candidate(10), Candidate(11) });

        Assert.Equal(new[] { ("top4.example.com", Candidate(10).IP.ToString()), ("top8.example.com", Candidate(11).IP.ToString()) }, manager.Updates);
    }

    [Fact]
    public void DashboardRetainsLastCheckStateAndOnlyRecordsNewAssignments()
    {
        DashboardState.SetAssignedIps(new[] { (1, "192.0.2.1", 100d), (2, "192.0.2.2", 100d) });
        DashboardState.RecordCheck(1, "192.0.2.1", 150, "定时复检");
        DashboardState.RecordCheck(2, "192.0.2.2", null, "定时复检");
        var before = JsonSerializer.Deserialize(DashboardState.GetStatusJson(), WebJsonContext.Default.StatusResponse)!;
        DashboardState.BeginRun("局部修复测试", "延迟优先");
        DashboardState.CompleteRun(new[] { (3, "192.0.2.10", 80d) },
            new[] { (1, "192.0.2.1", 100d), (2, "192.0.2.2", 100d), (3, "192.0.2.10", 80d) });
        var status = JsonSerializer.Deserialize(DashboardState.GetStatusJson(), WebJsonContext.Default.StatusResponse)!;
        var history = JsonSerializer.Deserialize(DashboardState.GetHistoryJson(), WebJsonContext.Default.HistoryData)!;

        Assert.Equal(150d, status.AssignedIps[0].LastDelayMs);
        Assert.Equal(before.AssignedIps[0].LastCheckedAt, status.AssignedIps[0].LastCheckedAt);
        Assert.False(status.AssignedIps[1].LastCheckOk);
        Assert.Null(status.AssignedIps[1].LastDelayMs);
        Assert.True(status.AssignedIps[2].LastCheckOk);
        Assert.Equal("192.0.2.10", Assert.Single(history.Runs.Last().Assigned).Ip);
    }

    [Fact]
    public async Task RevalidationPreservesQueuedFullSelection()
    {
        DashboardState.SetPhase(DashboardPhase.Revalidating);
        RuntimeControl.QueueFullSelection("DNS设置变更");
        await Program.ValidateAssignedIps(new[] { CreateAssigned(1) }, false,
            _ => Task.FromResult((0, TimeSpan.Zero, "HTTP失败")), _ => Task.CompletedTask);

        Assert.Equal(ManualCommand.FullSelection, RuntimeControl.Pending);
    }

    private static SelectionResult CreateSelection() => new(new[] { CreateAssigned(1), CreateAssigned(2), CreateAssigned(3) });

    private static AssignedIp CreateAssigned(int rank, string? domain = null, int? ipSuffix = null) =>
        new(IPAddress.Parse($"192.0.2.{ipSuffix ?? rank}"), TimeSpan.FromMilliseconds(100), rank,
            new[] { new DnsTarget("Cloudflare", domain ?? $"top{rank}.example.com", rank) });

    private static List<DnsTarget> GetTargets(SelectionResult selection) => selection.AssignedIps.SelectMany(item => item.Targets).ToList();

    private static PingData Candidate(int suffix) => new() { IP = IPAddress.Parse($"198.51.100.{suffix}"), Delay = TimeSpan.FromMilliseconds(80) };

    private static DnsUpdateProvider[] Providers(FakeDnsManager manager) =>
        new[] { new DnsUpdateProvider("Cloudflare", manager) };

    public void Dispose()
    {
        RuntimeControl.ClearPending();
        Directory.SetCurrentDirectory(_originalDirectory);
        Directory.Delete(_testDirectory, recursive: true);
    }

    private sealed class FakeDnsManager : IDnsRecordManager
    {
        public string ProviderName => "Cloudflare";
        public string? FailedDomain { get; init; }
        public bool ThrowOnFailure { get; init; }
        public List<(string Domain, string Ip)> Updates { get; } = new();

        public Task<bool> AddOrUpdateARecord(string fullDomain, string ipAddress)
        {
            Updates.Add((fullDomain, ipAddress));
            if (fullDomain == FailedDomain && ThrowOnFailure)
            {
                throw new InvalidOperationException("模拟DNS更新失败");
            }

            return Task.FromResult(fullDomain != FailedDomain);
        }
    }
}
