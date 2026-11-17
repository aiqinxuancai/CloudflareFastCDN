using System.Net;

namespace CloudflareFastCDN.Services
{
    internal sealed record DnsTarget(string ProviderName, string Domain, int Rank);

    internal sealed class AssignedIp
    {
        public AssignedIp(IPAddress ip, TimeSpan baselineDelay, int rank, IReadOnlyList<DnsTarget> targets)
        {
            IP = ip;
            BaselineDelay = baselineDelay;
            Rank = rank;
            Targets = targets;
        }

        public IPAddress IP { get; }
        public TimeSpan BaselineDelay { get; set; }
        public int Rank { get; }
        public IReadOnlyList<DnsTarget> Targets { get; }
    }

    internal sealed record SelectionResult(IReadOnlyList<AssignedIp> AssignedIps)
    {
        public static SelectionResult Empty { get; } = new(Array.Empty<AssignedIp>());

        public List<DnsTarget> GetTargetsToUpdate(IEnumerable<DnsTarget> configuredTargets, IReadOnlySet<IPAddress> failedIps)
        {
            var assignedTargets = AssignedIps.SelectMany(item => item.Targets).ToHashSet();
            var failedTargets = AssignedIps
                .Where(item => failedIps.Contains(item.IP))
                .SelectMany(item => item.Targets)
                .ToHashSet();

            return configuredTargets
                .Where(target => !assignedTargets.Contains(target) || failedTargets.Contains(target))
                .Distinct()
                .ToList();
        }

        public SelectionResult Merge(SelectionResult replacements)
        {
            var updatedTargets = replacements.AssignedIps.SelectMany(item => item.Targets).ToHashSet();
            var assignedIps = new List<AssignedIp>();
            foreach (var assignedIp in AssignedIps)
            {
                var remainingTargets = assignedIp.Targets.Where(target => !updatedTargets.Contains(target)).ToList();
                if (remainingTargets.Count == assignedIp.Targets.Count)
                {
                    assignedIps.Add(assignedIp);
                }
                else if (remainingTargets.Count > 0)
                {
                    assignedIps.Add(new AssignedIp(assignedIp.IP, assignedIp.BaselineDelay, assignedIp.Rank, remainingTargets));
                }
            }

            assignedIps.AddRange(replacements.AssignedIps);
            return new SelectionResult(assignedIps.OrderBy(item => item.Rank).ToList());
        }
    }

    // FailedIps 为 null 表示显式全量优选；非 null 时只修复异常 IP 和缺失的域名分配。
    internal sealed record SelectionRequest(string Trigger, IReadOnlySet<IPAddress>? FailedIps = null);
}
