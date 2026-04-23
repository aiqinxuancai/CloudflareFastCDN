namespace CloudflareFastCDN.Services
{
    internal interface IDnsRecordManager
    {
        string ProviderName { get; }

        Task<bool> AddOrUpdateARecord(string fullDomain, string ipAddress);
    }
}
