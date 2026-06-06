using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Watchtower;

[ApiController]
[Route("api/get-watchtower")]
public class GetWatchtowerController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;

        var sources = await dbClient.Ctx.ListSources.AsNoTracking()
            .OrderBy(s => s.CreatedAtUnix)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = await dbClient.Ctx.WantedItems.AsNoTracking()
            .OrderByDescending(w => w.UpdatedAtUnix)
            .Take(500)
            .Select(w => new
            {
                w.Key,
                w.Type,
                w.ContentId,
                w.Title,
                w.State,
                w.Provenance,
                w.Shortlist,
                w.LastVerifiedAtUnix,
                w.NextCheckAtUnix,
                w.FailReason,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var counts = await dbClient.Ctx.WantedItems.AsNoTracking()
            .GroupBy(w => w.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        int CountFor(string s) => counts.FirstOrDefault(c => c.State == s)?.Count ?? 0;

        return Ok(new GetWatchtowerResponse
        {
            Status = true,
            Enabled = configManager.IsWatchtowerEnabled(),
            Sources = sources.Select(s => new GetWatchtowerResponse.SourceDto
            {
                Id = s.Id.ToString(),
                Kind = s.Kind,
                Name = s.Name,
                Url = s.Url,
                Enabled = s.Enabled,
                Cap = s.Cap,
                SeriesScope = s.SeriesScope,
                LastSyncedAtUnix = s.LastSyncedAtUnix,
                LastSyncError = s.LastSyncError,
            }).ToList(),
            Items = items.Select(w =>
            {
                var shortlist = WtJson.ReadPointers(w.Shortlist);
                var winner = shortlist.FirstOrDefault();
                var provenance = WtJson.ReadStrings(w.Provenance);
                var expanderTag = provenance.FirstOrDefault(p => p.StartsWith("exp:", StringComparison.Ordinal));
                return new GetWatchtowerResponse.ItemDto
                {
                    Key = w.Key,
                    Type = w.Type,
                    ContentId = w.ContentId,
                    Title = w.Title,
                    State = w.State,
                    ProvenanceCount = provenance.Count,
                    ExpanderKey = expanderTag is null ? null : expanderTag.Substring(4),
                    ShortlistCount = shortlist.Count,
                    WinnerTitle = winner?.Title,
                    WinnerSize = winner?.Size ?? 0,
                    LastVerifiedAtUnix = w.LastVerifiedAtUnix,
                    NextCheckAtUnix = w.NextCheckAtUnix,
                    FailReason = w.FailReason,
                };
            }).ToList(),
            Stats = new GetWatchtowerResponse.StatsDto
            {
                Total = counts.Sum(c => c.Count),
                Ready = CountFor(WantedItem.StateReady),
                Scouting = CountFor(WantedItem.StateScouting),
                Unavailable = CountFor(WantedItem.StateUnavailable),
                Parked = CountFor(WantedItem.StateParked),
                Expanders = CountFor(WantedItem.StateExpander),
            },
        });
    }
}

public class GetWatchtowerResponse : BaseApiResponse
{
    [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
    [JsonPropertyName("sources")] public required List<SourceDto> Sources { get; init; }
    [JsonPropertyName("items")] public required List<ItemDto> Items { get; init; }
    [JsonPropertyName("stats")] public required StatsDto Stats { get; init; }

    public class SourceDto
    {
        [JsonPropertyName("id")] public required string Id { get; init; }
        [JsonPropertyName("kind")] public required string Kind { get; init; }
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
        [JsonPropertyName("cap")] public required int Cap { get; init; }
        [JsonPropertyName("seriesScope")] public string? SeriesScope { get; init; }
        [JsonPropertyName("lastSyncedAtUnix")] public long? LastSyncedAtUnix { get; init; }
        [JsonPropertyName("lastSyncError")] public string? LastSyncError { get; init; }
    }

    public class ItemDto
    {
        [JsonPropertyName("key")] public required string Key { get; init; }
        [JsonPropertyName("type")] public required string Type { get; init; }
        [JsonPropertyName("contentId")] public required string ContentId { get; init; }
        [JsonPropertyName("title")] public required string Title { get; init; }
        [JsonPropertyName("state")] public required string State { get; init; }
        [JsonPropertyName("provenanceCount")] public required int ProvenanceCount { get; init; }
        [JsonPropertyName("expanderKey")] public string? ExpanderKey { get; init; }
        [JsonPropertyName("shortlistCount")] public required int ShortlistCount { get; init; }
        [JsonPropertyName("winnerTitle")] public string? WinnerTitle { get; init; }
        [JsonPropertyName("winnerSize")] public required long WinnerSize { get; init; }
        [JsonPropertyName("lastVerifiedAtUnix")] public long? LastVerifiedAtUnix { get; init; }
        [JsonPropertyName("nextCheckAtUnix")] public long? NextCheckAtUnix { get; init; }
        [JsonPropertyName("failReason")] public string? FailReason { get; init; }
    }

    public class StatsDto
    {
        [JsonPropertyName("total")] public required int Total { get; init; }
        [JsonPropertyName("ready")] public required int Ready { get; init; }
        [JsonPropertyName("scouting")] public required int Scouting { get; init; }
        [JsonPropertyName("unavailable")] public required int Unavailable { get; init; }
        [JsonPropertyName("parked")] public required int Parked { get; init; }
        [JsonPropertyName("expanders")] public required int Expanders { get; init; }
    }
}
