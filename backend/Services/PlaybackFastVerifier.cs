using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Services;

public class PlaybackFastVerifier
{
    private readonly UsenetStreamingClient _usenetClient;

    public PlaybackFastVerifier(UsenetStreamingClient usenetClient)
    {
        _usenetClient = usenetClient;
    }

    public async Task<VerifyOutcome> VerifyAsync(Stream nzbStream, string mode, int sampleCount, CancellationToken ct)
    {
        if (mode == "none") return new VerifyOutcome(Verdict.Available, null);

        NzbDocument nzb;
        try
        {
            nzb = await NzbDocument.LoadAsync(nzbStream).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Debug("Fast-verify: NZB parse failed: {Message}", e.Message);
            return new VerifyOutcome(Verdict.Dead, null);
        }

        var samples = PickSampleSegments(nzb, Math.Max(1, sampleCount));
        if (samples.Count == 0) return new VerifyOutcome(Verdict.Dead, null);

        var attribution = new MultiProviderNntpClient.ResponderAttribution();
        MultiProviderNntpClient.AttributionContext.Value = attribution;

        var tasks = samples.Select(s => CheckSegmentAsync(s, mode, ct)).ToList();
        Verdict[] results;
        try
        {
            results = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new VerifyOutcome(Verdict.Timeout, attribution.Host);
        }

        if (results.Any(r => r == Verdict.Dead))
            return new VerifyOutcome(Verdict.Dead, attribution.Host);
        if (results.All(r => r == Verdict.Timeout))
            return new VerifyOutcome(Verdict.Timeout, attribution.Host);
        return new VerifyOutcome(Verdict.Available, attribution.Host);
    }

    private async Task<Verdict> CheckSegmentAsync(string messageId, string mode, CancellationToken ct)
    {
        try
        {
            if (mode == "body")
            {
                var resp = await _usenetClient.DecodedBodyAsync(messageId, ct).ConfigureAwait(false);
                return resp.ResponseType == UsenetResponseType.ArticleRetrievedBodyFollows
                    ? Verdict.Available
                    : Verdict.Dead;
            }
            else
            {
                var resp = await _usenetClient.StatAsync(messageId, ct).ConfigureAwait(false);
                return resp.ResponseType == UsenetResponseType.ArticleExists
                    ? Verdict.Available
                    : Verdict.Dead;
            }
        }
        catch (OperationCanceledException)
        {
            return Verdict.Timeout;
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Debug("Fast-verify errored on {Segment}: {Message}", messageId, e.Message);
            return Verdict.Timeout;
        }
    }

    public readonly record struct VerifyOutcome(Verdict Verdict, string? ResponderHost);

    private static List<string> PickSampleSegments(NzbDocument nzb, int sampleCount)
    {
        var dataFile = nzb.Files
            .Where(f => f.Segments.Count > 0 && !IsPar2(f))
            .OrderByDescending(f => f.GetTotalYencodedSize())
            .FirstOrDefault();
        var anyFile = dataFile ?? nzb.Files
            .Where(f => f.Segments.Count > 0)
            .OrderByDescending(f => f.GetTotalYencodedSize())
            .FirstOrDefault();
        if (anyFile is null) return new List<string>();

        var segs = anyFile.Segments;
        var n = Math.Min(sampleCount, segs.Count);
        if (n <= 1) return new List<string> { segs[0].MessageId };

        var indices = new SortedSet<int>();
        for (var i = 0; i < n; i++)
        {
            var idx = (int)Math.Round(i * (segs.Count - 1) / (double)(n - 1));
            indices.Add(idx);
        }
        return indices.Select(i => segs[i].MessageId).ToList();
    }

    private static bool IsPar2(NzbFile file)
    {
        var name = file.GetSubjectFileName();
        if (!string.IsNullOrEmpty(name) && name.EndsWith(".par2", StringComparison.OrdinalIgnoreCase))
            return true;
        return file.Subject.Contains(".par2", StringComparison.OrdinalIgnoreCase);
    }

    public enum Verdict
    {
        Available,
        Dead,
        Timeout,
    }
}
