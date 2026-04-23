namespace CloudflareFastCDN.Services
{
    internal static class DnsDomainResolver
    {
        public static (string RootDomain, string RecordName)? Resolve(string fullDomain, IEnumerable<string> availableRootDomains)
        {
            var normalizedFullDomain = NormalizeDomain(fullDomain);
            if (string.IsNullOrWhiteSpace(normalizedFullDomain))
            {
                return null;
            }

            var matchedRootDomain = availableRootDomains
                .Select(NormalizeDomain)
                .Where(rootDomain =>
                    !string.IsNullOrWhiteSpace(rootDomain) &&
                    (string.Equals(normalizedFullDomain, rootDomain, StringComparison.OrdinalIgnoreCase) ||
                     normalizedFullDomain.EndsWith($".{rootDomain}", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(rootDomain => rootDomain.Length)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(matchedRootDomain))
            {
                return null;
            }

            if (string.Equals(normalizedFullDomain, matchedRootDomain, StringComparison.OrdinalIgnoreCase))
            {
                return (matchedRootDomain, "@");
            }

            var recordNameLength = normalizedFullDomain.Length - matchedRootDomain.Length - 1;
            if (recordNameLength <= 0)
            {
                return (matchedRootDomain, "@");
            }

            return (matchedRootDomain, normalizedFullDomain[..recordNameLength]);
        }

        public static string NormalizeDomain(string? domain)
        {
            return string.IsNullOrWhiteSpace(domain)
                ? string.Empty
                : domain.Trim().Trim('.').ToLowerInvariant();
        }
    }
}
