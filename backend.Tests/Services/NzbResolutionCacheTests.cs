using NzbWebDAV.Services;
using Xunit;

namespace NzbWebDAV.Tests.Services;

public class NzbResolutionCacheTests
{
    [Fact]
    public void GetForProfile_ReturnsEntryForMatchingProfileToken()
    {
        var cache = new NzbResolutionCache();
        var token = AddEntry(cache, "profile-a");

        var entry = cache.GetForProfile(token, "profile-a");

        Assert.NotNull(entry);
        Assert.Equal("profile-a", entry.ProfileToken);
    }

    [Fact]
    public void GetForProfile_ReturnsNullForDifferentProfileToken()
    {
        var cache = new NzbResolutionCache();
        var token = AddEntry(cache, "profile-a");

        var entry = cache.GetForProfile(token, "profile-b");

        Assert.Null(entry);
    }

    [Fact]
    public void GetForProfile_UsesOrdinalProfileTokenComparison()
    {
        var cache = new NzbResolutionCache();
        var token = AddEntry(cache, "Profile-A");

        var entry = cache.GetForProfile(token, "profile-a");

        Assert.Null(entry);
    }

    private static string AddEntry(NzbResolutionCache cache, string profileToken)
    {
        var tokens = cache.AddGroup(
            [
                new NzbResolutionCache.Candidate
                {
                    IndexerName = "indexer",
                    IndexerUserAgent = "tests",
                    NzbUrl = "https://example.invalid/file.nzb",
                    Title = "Test Release",
                    Size = 123,
                }
            ],
            type: "movie",
            profileToken,
            id: "tt123");
        return tokens[0];
    }
}
