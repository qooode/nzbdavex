using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Watchtower;

[ApiController]
[Route("api/watchtower-mutate")]
public class WatchtowerMutateController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var form = HttpContext.Request.Form;
        var action = form["action"].ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        switch (action)
        {
            case "add-source":
                AddSource(form, now);
                break;
            case "add-sources":
                await AddSourcesAsync(form, now, ct).ConfigureAwait(false);
                break;
            case "remove-source":
                await RemoveSourceAsync(form, now, ct).ConfigureAwait(false);
                break;
            case "toggle-source":
                await ToggleSourceAsync(form, ct).ConfigureAwait(false);
                break;
            case "set-source-scope":
                await SetSourceScopeAsync(form, now, ct).ConfigureAwait(false);
                break;
            case "add-item":
                await AddItemAsync(form, now, ct).ConfigureAwait(false);
                break;
            case "remove-item":
                await RemoveItemAsync(form, ct).ConfigureAwait(false);
                break;
            default:
                throw new BadHttpRequestException($"Unknown watchtower action: '{action}'");
        }

        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return Ok(new WatchtowerMutateResponse { Status = true });
    }

    private void AddSource(IFormCollection form, long now)
    {
        var kind = form["kind"].ToString();
        if (kind is not (ListSource.KindStremioCatalog or ListSource.KindUrlList or ListSource.KindManual))
            throw new BadHttpRequestException($"Unknown source kind: '{kind}'");

        var url = form["url"].ToString();
        if (kind != ListSource.KindManual && string.IsNullOrWhiteSpace(url))
            throw new BadHttpRequestException("A URL is required for this source kind.");

        var name = form["name"].ToString();
        if (string.IsNullOrWhiteSpace(name)) name = kind == ListSource.KindManual ? "Manual" : url;

        dbClient.Ctx.ListSources.Add(new ListSource
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Name = name,
            Url = kind == ListSource.KindManual ? null : url,
            Enabled = true,
            Cap = int.TryParse(form["cap"].ToString(), out var cap) ? Math.Max(0, cap) : 0,
            SeriesScope = ConfigManager.NormalizeSeriesScope(form["seriesScope"].ToString()),
            CreatedAtUnix = now,
        });
    }

    private async Task AddSourcesAsync(IFormCollection form, long now, CancellationToken ct)
    {
        var payload = form["sources"].ToString();
        if (string.IsNullOrWhiteSpace(payload))
            throw new BadHttpRequestException("No catalogs were selected.");

        List<SourceInput>? inputs;
        try
        {
            inputs = JsonSerializer.Deserialize<List<SourceInput>>(payload);
        }
        catch (Exception e)
        {
            throw new BadHttpRequestException($"Invalid catalog selection: {e.Message}");
        }
        if (inputs is null || inputs.Count == 0)
            throw new BadHttpRequestException("No catalogs were selected.");

        var bulkScope = ConfigManager.NormalizeSeriesScope(form["seriesScope"].ToString());

        var existingUrls = (await dbClient.Ctx.ListSources
                .Where(s => s.Url != null)
                .Select(s => s.Url!)
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            var url = input.Url?.Trim();
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (!existingUrls.Add(url)) continue;

            dbClient.Ctx.ListSources.Add(new ListSource
            {
                Id = Guid.NewGuid(),
                Kind = ListSource.KindStremioCatalog,
                Name = string.IsNullOrWhiteSpace(input.Name) ? url : input.Name!.Trim(),
                Url = url,
                Enabled = true,
                Cap = input.Cap is > 0 ? input.Cap.Value : 0,
                SeriesScope = ConfigManager.NormalizeSeriesScope(input.SeriesScope) ?? bulkScope,
                CreatedAtUnix = now,
            });
        }
    }

    private async Task RemoveSourceAsync(IFormCollection form, long now, CancellationToken ct)
    {
        if (!Guid.TryParse(form["id"].ToString(), out var id))
            throw new BadHttpRequestException("Invalid source id.");

        var source = await dbClient.Ctx.ListSources.FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (source is null) return;
        dbClient.Ctx.ListSources.Remove(source);

        var srcId = id.ToString();
        var claimed = await dbClient.Ctx.WantedItems
            .Where(w => w.Provenance.Contains(srcId))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var item in claimed)
        {
            var prov = WtJson.ReadStrings(item.Provenance);
            prov.Remove(srcId);
            if (prov.Count == 0)
            {
                dbClient.Ctx.WantedItems.Remove(item);
            }
            else
            {
                item.Provenance = WtJson.WriteStrings(prov);
                item.UpdatedAtUnix = now;
            }
        }
    }

    private async Task ToggleSourceAsync(IFormCollection form, CancellationToken ct)
    {
        if (!Guid.TryParse(form["id"].ToString(), out var id))
            throw new BadHttpRequestException("Invalid source id.");
        var source = await dbClient.Ctx.ListSources.FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (source is null) return;
        source.Enabled = form["enabled"].ToString() == "true";
    }

    private async Task SetSourceScopeAsync(IFormCollection form, long now, CancellationToken ct)
    {
        if (!Guid.TryParse(form["id"].ToString(), out var id))
            throw new BadHttpRequestException("Invalid source id.");
        var source = await dbClient.Ctx.ListSources.FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (source is null) return;
        source.SeriesScope = ConfigManager.NormalizeSeriesScope(form["seriesScope"].ToString());

        var srcId = id.ToString();
        var expanders = await dbClient.Ctx.WantedItems
            .Where(w => w.State == WantedItem.StateExpander && w.Provenance.Contains(srcId))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var expander in expanders)
        {
            expander.NextCheckAtUnix = now;
            expander.UpdatedAtUnix = now;
        }
    }

    private async Task AddItemAsync(IFormCollection form, long now, CancellationToken ct)
    {
        var type = form["type"].ToString();
        type = type is "series" or "tv" or "show" ? "series" : "movie";
        var contentId = form["id"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(contentId))
            throw new BadHttpRequestException("A content id (e.g. an imdb id) is required.");
        var title = form["title"].ToString();
        if (string.IsNullOrWhiteSpace(title)) title = contentId;

        var manualSource = await GetOrCreateManualSourceAsync(now, ct).ConfigureAwait(false);
        var srcId = manualSource.Id.ToString();
        var key = $"{type}:{contentId}";

        var existing = await dbClient.Ctx.WantedItems.FirstOrDefaultAsync(w => w.Key == key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var prov = WtJson.ReadStrings(existing.Provenance);
            if (!prov.Contains(srcId))
            {
                prov.Add(srcId);
                existing.Provenance = WtJson.WriteStrings(prov);
                existing.UpdatedAtUnix = now;
            }
            return;
        }

        dbClient.Ctx.WantedItems.Add(new WantedItem
        {
            Id = Guid.NewGuid(),
            Key = key,
            Type = type,
            ContentId = contentId,
            Title = title,
            State = WantedItem.IsBareSeries(type, contentId) ? WantedItem.StateExpander : WantedItem.StateScouting,
            Provenance = WtJson.WriteStrings(new[] { srcId }),
            Shortlist = "[]",
            CreatedAtUnix = now,
            UpdatedAtUnix = now,
            NextCheckAtUnix = now,
        });
    }

    private async Task RemoveItemAsync(IFormCollection form, CancellationToken ct)
    {
        var key = form["key"].ToString();
        if (string.IsNullOrWhiteSpace(key))
            throw new BadHttpRequestException("An item key is required.");
        var item = await dbClient.Ctx.WantedItems.FirstOrDefaultAsync(w => w.Key == key, ct).ConfigureAwait(false);
        if (item is not null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await WtReconcile.RemoveWithChildrenAsync(dbClient.Ctx, item, now, ct).ConfigureAwait(false);
        }
    }

    private async Task<ListSource> GetOrCreateManualSourceAsync(long now, CancellationToken ct)
    {
        var manual = await dbClient.Ctx.ListSources
            .FirstOrDefaultAsync(s => s.Kind == ListSource.KindManual, ct).ConfigureAwait(false);
        if (manual is not null) return manual;
        manual = new ListSource
        {
            Id = Guid.NewGuid(),
            Kind = ListSource.KindManual,
            Name = "Manual",
            Url = null,
            Enabled = true,
            Cap = 0,
            CreatedAtUnix = now,
        };
        dbClient.Ctx.ListSources.Add(manual);
        return manual;
    }

    private sealed class SourceInput
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("cap")] public int? Cap { get; set; }
        [JsonPropertyName("seriesScope")] public string? SeriesScope { get; set; }
    }
}

public class WatchtowerMutateResponse : BaseApiResponse
{
}
