using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Warden;

[ApiController]
[Route("api/warden-export")]
public class WardenExportController(WardenStore warden) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        var json = JsonSerializer.Serialize(warden.Export(), WardenStore.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Task.FromResult<IActionResult>(File(bytes, "application/json", "warden.json"));
    }
}
