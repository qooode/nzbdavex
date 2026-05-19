using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Api.Controllers.GetProviderUsage;

[ApiController]
[Route("api/get-provider-usage")]
public class GetProviderUsageController(
    ConfigManager configManager,
    ProviderBytesTracker bytesTracker
) : BaseApiController
{
    private GetProviderUsageResponse GetUsage()
    {
        var providerConfig = configManager.GetUsenetProviderConfig();
        var items = providerConfig.Providers
            .Select((provider, index) => new GetProviderUsageResponse.ProviderUsageItem
            {
                Index = index,
                Host = provider.Host,
                BytesUsed = ProviderUsageHelper.ComputeUsage(bytesTracker, provider),
                ByteLimit = provider.ByteLimit,
                OverLimit = ProviderUsageHelper.IsOverLimit(bytesTracker, provider),
            })
            .ToList();

        return new GetProviderUsageResponse
        {
            Status = true,
            Providers = items,
        };
    }

    protected override Task<IActionResult> HandleRequest()
    {
        return Task.FromResult<IActionResult>(Ok(GetUsage()));
    }
}
