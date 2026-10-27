using System.Text.Json;

namespace CloudflareFastCDN.Web
{
    internal enum DashboardPhase
    {
        Starting,
        InitialCheck,
        Selecting,
        Revalidating,
        Waiting,
        SupplementalCheck,
        NotConfigured
    }

    /// <summary>
    /// 运行状态与历史数据，供 Web 管理页面读取。所有成员线程安全。
    /// </summary>
    internal static class DashboardState
    {
        private const int MaxLatencySamples = 5000;
        private const int MaxRuns = 200;

        private static readonly string HistoryFile =
            File.Exists("/.dockerenv")
                ? "/data/web_history.json"
                : "web_history.json";

        private static readonly object SyncRoot = new();
        private static readonly DateTimeOffset StartedAt = DateTimeOffset.Now;
        private static List<LatencySample> _latency = new();
        private static List<SelectionRun> _runs = new();
        private static List<AssignedIpInfo> _assignedIps = new();
        private static List<DomainGroupInfo> _domainGroups = new();
        private static DashboardPhase _phase = DashboardPhase.Starting;
        private static string? _phaseDetail;
        private static DateTimeOffset _phaseStartedAt = DateTimeOffset.Now;
        private static DateTimeOffset? _nextCheckAt;
        private static SelectionRun? _currentRun;
        private static int _historyVersion;

        public static DashboardPhase CurrentPhase
        {
            get
            {
                lock (SyncRoot)
                {
                    return _phase;
                }
            }
        }

        public static void Initialize()
        {
            lock (SyncRoot)
            {
                LoadHistory();
            }
        }

        public static void SetDomainGroups(List<DomainGroupInfo> domainGroups)
        {
            lock (SyncRoot)
            {
                _domainGroups = domainGroups;
            }
        }

        public static void SetPhase(DashboardPhase phase, string? detail = null)
        {
            lock (SyncRoot)
            {
                if (_phase != phase)
                {
                    _phase = phase;
                    _phaseStartedAt = DateTimeOffset.Now;
                }

                _phaseDetail = detail;
                if (phase != DashboardPhase.Waiting && phase != DashboardPhase.SupplementalCheck)
                {
                    _nextCheckAt = null;
                }
            }
        }

        public static void SetWaiting(DateTimeOffset nextCheckAt)
        {
            lock (SyncRoot)
            {
                SetPhase(DashboardPhase.Waiting);
                _nextCheckAt = nextCheckAt;
            }
        }

        public static void SetAssignedIps(IEnumerable<(int Rank, string Ip, double BaselineMs)> assignedIps)
        {
            lock (SyncRoot)
            {
                var now = DateTimeOffset.Now;
                _assignedIps = assignedIps
                    .OrderBy(item => item.Rank)
                    .Select(item => new AssignedIpInfo
                    {
                        Rank = item.Rank,
                        Ip = item.Ip,
                        BaselineMs = Round(item.BaselineMs),
                        LastDelayMs = Round(item.BaselineMs),
                        LastCheckedAt = now,
                        LastCheckOk = true
                    })
                    .ToList();
            }
        }

        /// <summary>
        /// 记录一次 HTTP 延迟检查；delayMs 为 null 表示检查失败。
        /// </summary>
        public static void RecordCheck(int rank, string ip, double? delayMs, string source, double? baselineMs = null)
        {
            lock (SyncRoot)
            {
                var now = DateTimeOffset.Now;
                AddLatencySample(new LatencySample
                {
                    Time = now,
                    Rank = rank,
                    Ip = ip,
                    DelayMs = Round(delayMs),
                    Source = source
                });

                var assigned = _assignedIps.FirstOrDefault(item => item.Rank == rank && item.Ip == ip);
                if (assigned != null)
                {
                    assigned.LastDelayMs = Round(delayMs);
                    assigned.LastCheckedAt = now;
                    assigned.LastCheckOk = delayMs.HasValue;
                    if (baselineMs.HasValue)
                    {
                        assigned.BaselineMs = Round(baselineMs.Value);
                    }
                }

                SaveHistory();
            }
        }

        public static void BeginRun(string trigger, string mode)
        {
            lock (SyncRoot)
            {
                _currentRun = new SelectionRun
                {
                    Id = (_runs.Count == 0 ? 0 : _runs.Max(run => run.Id)) + 1,
                    StartedAt = DateTimeOffset.Now,
                    Trigger = trigger,
                    Mode = mode
                };
                SetPhase(DashboardPhase.Selecting, "准备抽样");
            }
        }

        public static void UpdateRun(Action<SelectionRun> update, string? phaseDetail = null)
        {
            lock (SyncRoot)
            {
                if (_currentRun != null)
                {
                    update(_currentRun);
                }

                if (phaseDetail != null && _phase == DashboardPhase.Selecting)
                {
                    _phaseDetail = phaseDetail;
                }
            }
        }

        public static void SetSelectionDetail(string detail)
        {
            UpdateRun(_ => { }, detail);
        }

        public static void CompleteRun(IEnumerable<(int Rank, string Ip, double DelayMs)> assignedIps)
        {
            lock (SyncRoot)
            {
                if (_currentRun == null)
                {
                    return;
                }

                var run = _currentRun;
                _currentRun = null;
                run.FinishedAt = DateTimeOffset.Now;
                run.Assigned = assignedIps
                    .Select(item => new RunAssignment { Rank = item.Rank, Ip = item.Ip, DelayMs = Round(item.DelayMs) })
                    .ToList();
                run.Note = run.Assigned.Count > 0 ? $"已分配{run.Assigned.Count}个IP"
                    : run.HttpCandidates == 0 ? "没有IP进入HTTP验证阶段"
                    : run.HttpPassed == 0 ? "没有IP通过HTTP验证"
                    : "DNS更新失败";
                _runs.Add(run);
                if (_runs.Count > MaxRuns)
                {
                    _runs.RemoveRange(0, _runs.Count - MaxRuns);
                }

                foreach (var assigned in run.Assigned)
                {
                    AddLatencySample(new LatencySample
                    {
                        Time = run.FinishedAt.Value,
                        Rank = assigned.Rank,
                        Ip = assigned.Ip,
                        DelayMs = assigned.DelayMs,
                        Source = "优选"
                    });
                }

                SetAssignedIps(run.Assigned.Select(item => (item.Rank, item.Ip, item.DelayMs)));
                SaveHistory();
            }
        }

        public static byte[] GetStatusJson()
        {
            // 先读取控制状态，避免与 RuntimeControl 的锁形成嵌套
            var pending = RuntimeControl.Pending;
            lock (SyncRoot)
            {
                var status = new StatusResponse
                {
                    ServerTime = DateTimeOffset.Now,
                    StartedAt = StartedAt,
                    Phase = FormatPhase(_phase),
                    PhaseDetail = _phaseDetail,
                    PhaseStartedAt = _phaseStartedAt,
                    NextCheckAt = _nextCheckAt,
                    PendingAction = pending switch
                    {
                        ManualCommand.FullSelection => "select",
                        ManualCommand.Revalidate => "revalidate",
                        _ => null
                    },
                    AssignedIps = _assignedIps,
                    DomainGroups = _domainGroups,
                    CurrentRun = _currentRun,
                    CompletedRuns = _runs.Count,
                    LastRunFinishedAt = _runs.Count > 0 ? _runs[^1].FinishedAt : null,
                    HistoryVersion = _historyVersion
                };

                return JsonSerializer.SerializeToUtf8Bytes(status, WebJsonContext.Default.StatusResponse);
            }
        }

        public static byte[] GetHistoryJson()
        {
            lock (SyncRoot)
            {
                var history = new HistoryData
                {
                    Latency = _latency,
                    Runs = _runs
                };

                return JsonSerializer.SerializeToUtf8Bytes(history, WebJsonContext.Default.HistoryData);
            }
        }

        private static void AddLatencySample(LatencySample sample)
        {
            _latency.Add(sample);
            if (_latency.Count > MaxLatencySamples)
            {
                _latency.RemoveRange(0, _latency.Count - MaxLatencySamples);
            }
        }

        private static void LoadHistory()
        {
            if (!File.Exists(HistoryFile))
            {
                return;
            }

            try
            {
                var history = JsonSerializer.Deserialize(File.ReadAllText(HistoryFile), WebJsonContext.Default.HistoryData);
                _latency = history?.Latency ?? new();
                _runs = history?.Runs ?? new();
                _historyVersion++;
                Console.WriteLine($"已加载Web历史数据：{_runs.Count}次优选记录，{_latency.Count}个延迟采样");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载Web历史数据失败，将重新记录：{ex.Message}");
            }
        }

        private static void SaveHistory()
        {
            _historyVersion++;
            try
            {
                var dir = Path.GetDirectoryName(HistoryFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var history = new HistoryData
                {
                    Latency = _latency,
                    Runs = _runs
                };
                File.WriteAllBytes(HistoryFile, JsonSerializer.SerializeToUtf8Bytes(history, WebJsonContext.Default.HistoryData));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"保存Web历史数据失败：{ex.Message}");
            }
        }

        private static string FormatPhase(DashboardPhase phase)
        {
            return phase switch
            {
                DashboardPhase.Starting => "starting",
                DashboardPhase.InitialCheck => "initial-check",
                DashboardPhase.Selecting => "selecting",
                DashboardPhase.Revalidating => "revalidating",
                DashboardPhase.Waiting => "waiting",
                DashboardPhase.SupplementalCheck => "supplemental-check",
                DashboardPhase.NotConfigured => "not-configured",
                _ => "unknown"
            };
        }

        private static double Round(double value)
        {
            return Math.Round(value, 2);
        }

        private static double? Round(double? value)
        {
            return value.HasValue ? Math.Round(value.Value, 2) : null;
        }
    }

    internal sealed class StatusResponse
    {
        public DateTimeOffset ServerTime { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public string Phase { get; set; } = string.Empty;
        public string? PhaseDetail { get; set; }
        public DateTimeOffset PhaseStartedAt { get; set; }
        public DateTimeOffset? NextCheckAt { get; set; }
        public string? PendingAction { get; set; }
        public List<AssignedIpInfo> AssignedIps { get; set; } = new();
        public List<DomainGroupInfo> DomainGroups { get; set; } = new();
        public SelectionRun? CurrentRun { get; set; }
        public int CompletedRuns { get; set; }
        public DateTimeOffset? LastRunFinishedAt { get; set; }
        public int HistoryVersion { get; set; }
    }

    internal sealed class HistoryData
    {
        public List<LatencySample> Latency { get; set; } = new();
        public List<SelectionRun> Runs { get; set; } = new();
    }

    internal sealed class AssignedIpInfo
    {
        public int Rank { get; set; }
        public string Ip { get; set; } = string.Empty;
        public double BaselineMs { get; set; }
        public double? LastDelayMs { get; set; }
        public DateTimeOffset? LastCheckedAt { get; set; }
        public bool LastCheckOk { get; set; }
    }

    internal sealed class LatencySample
    {
        public DateTimeOffset Time { get; set; }
        public int Rank { get; set; }
        public string Ip { get; set; } = string.Empty;
        public double? DelayMs { get; set; }
        public string Source { get; set; } = string.Empty;
    }

    internal sealed class SelectionRun
    {
        public long Id { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public string Trigger { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public int Sampled { get; set; }
        public int FirstRoundPassed { get; set; }
        public int SecondRoundPassed { get; set; }
        public int CachedSampled { get; set; }
        public int CachedPassed { get; set; }
        public int HttpCandidates { get; set; }
        public int HttpPassed { get; set; }
        public List<CandidateResult> Candidates { get; set; } = new();
        public List<DnsUpdateResult> DnsUpdates { get; set; } = new();
        public List<RunAssignment> Assigned { get; set; } = new();
        public string? Note { get; set; }
    }

    internal sealed class CandidateResult
    {
        public string Ip { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public double? PingMs { get; set; }
        public int HttpSuccess { get; set; }
        public int HttpTotal { get; set; }
        public double? HttpAvgMs { get; set; }
        public double? Mbps { get; set; }
        public bool Passed { get; set; }
        public string? Error { get; set; }
    }

    internal sealed class DnsUpdateResult
    {
        public string Provider { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public int Rank { get; set; }
        public string Ip { get; set; } = string.Empty;
        public bool Success { get; set; }
    }

    internal sealed class RunAssignment
    {
        public int Rank { get; set; }
        public string Ip { get; set; } = string.Empty;
        public double DelayMs { get; set; }
    }

    internal sealed class DomainGroupInfo
    {
        public int Rank { get; set; }
        public string Provider { get; set; } = string.Empty;
        public List<string> Domains { get; set; } = new();
    }
}
