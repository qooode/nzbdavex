using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Warden;

[ApiController]
[Route("api/warden-import")]
public class WardenImportController(WardenStore warden) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var action = HttpContext.Request.HasFormContentType
            ? HttpContext.Request.Form["action"].ToString()
            : "";

        if (action == "clear")
        {
            var removed = warden.Clear();
            return Ok(new WardenImportResponse { Status = true, Added = 0, Total = warden.Count, Cleared = removed });
        }

        var json = await ReadPayloadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            throw new BadHttpRequestException("No warden file was provided.");

        WardenFile? model;
        try
        {
            model = JsonSerializer.Deserialize<WardenFile>(json, WardenStore.JsonOptions);
        }
        catch (Exception e)
        {
            throw new BadHttpRequestException($"Invalid warden file: {e.Message}");
        }

        if (model is null)
            throw new BadHttpRequestException("Invalid warden file.");

        var added = warden.Import(model);
        return Ok(new WardenImportResponse { Status = true, Added = added, Total = warden.Count, Cleared = 0 });
    }

    private async Task<string> ReadPayloadAsync(CancellationToken ct)
    {
        if (HttpContext.Request.HasFormContentType && HttpContext.Request.Form.Files.Count > 0)
        {
            await using var stream = HttpContext.Request.Form.Files[0].OpenReadStream();
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        if (HttpContext.Request.HasFormContentType)
        {
            var payload = HttpContext.Request.Form["payload"].ToString();
            if (!string.IsNullOrWhiteSpace(payload)) return payload;
        }

        using var bodyReader = new StreamReader(HttpContext.Request.Body);
        return await bodyReader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}

public class WardenImportResponse : BaseApiResponse
{
    [JsonPropertyName("added")] public required int Added { get; init; }
    [JsonPropertyName("total")] public required int Total { get; init; }
    [JsonPropertyName("cleared")] public required int Cleared { get; init; }
}
