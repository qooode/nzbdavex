using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Metrics;
using Xunit;

namespace NzbWebDAV.Tests.Services;

public class ProviderUsageHelperTests
{
    [Fact]
    public void ComputeUsage_SeparatesProvidersWithSameHostAndDifferentUsers()
    {
        var tracker = new ProviderBytesTracker();
        var providerA = Provider("news.example", "user-a", byteLimit: 100);
        var providerB = Provider("news.example", "user-b", byteLimit: 100);

        tracker.SetLifetime(providerA.ProviderKey, 95);

        Assert.Equal(95, ProviderUsageHelper.ComputeUsage(tracker, providerA));
        Assert.Equal(0, ProviderUsageHelper.ComputeUsage(tracker, providerB));
        Assert.True(ProviderUsageHelper.IsOverLimit(tracker, providerA));
        Assert.False(ProviderUsageHelper.IsOverLimit(tracker, providerB));
    }

    private static UsenetProviderConfig.ConnectionDetails Provider(string host, string user, long? byteLimit) =>
        new()
        {
            Type = ProviderType.Pooled,
            Host = host,
            Port = 563,
            UseSsl = true,
            User = user,
            Pass = "pass",
            MaxConnections = 1,
            ByteLimit = byteLimit,
        };
}
