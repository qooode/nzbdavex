using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Services;

/// <summary>
/// Cheap pre-flight check used by ProfilePlayController to decide whether to commit
/// to a candidate release. STATs (or optionally fetches body of) the first segment
/// of the first non-par2 file. Bounded by caller-supplied cancellation token.
/// </summary>
public class PlaybackFastVerifier
{
    private readonly UsenetStreamingClient _usenetClient;

    public PlaybackFastVerifier(UsenetStreamingClient usenetClient)
    {
        _usenetClient = usenetClient;
    }

    public async Task<VerifyOutcome> VerifyAsync(Stream nzbStream, string mode, CancellationToken ct)
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

        var sampleSegmentId = PickSampleSegment(nzb);
        if (sampleSegmentId is null) return new VerifyOutcome(Verdict.Dead, null);

        // Set a holder so the NNTP layer can record which provider answered.
        var attribution = new MultiProviderNntpClient.ResponderAttribution();
        MultiProviderNntpClient.AttributionContext.Value = attribution;

        try
        {
            if (mode == "body")
            {
                var resp = await _usenetClient.DecodedBodyAsync(sampleSegmentId, ct).ConfigureAwait(false);
                var verdict = resp.ResponseType == UsenetResponseType.ArticleRetrievedBodyFollows
                    ? Verdict.Available
                    : Verdict.Dead;
                return new VerifyOutcome(verdict, attribution.Host);
            }
            else
            {
                var resp = await _usenetClient.StatAsync(sampleSegmentId, ct).ConfigureAwait(false);
                var verdict = resp.ResponseType == UsenetResponseType.ArticleExists
                    ? Verdict.Available
                    : Verdict.Dead;
                return new VerifyOutcome(verdict, attribution.Host);
            }
        }
        catch (OperationCanceledException)
        {
            return new VerifyOutcome(Verdict.Timeout, null);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Debug("Fast-verify errored on {Segment}: {Message}", sampleSegmentId, e.Message);
            return new VerifyOutcome(Verdict.Timeout, null);
        }
    }

    public readonly record struct VerifyOutcome(Verdict Verdict, string? ResponderHost);

    private static string? PickSampleSegment(NzbDocument nzb)
    {
        var firstDataFile = nzb.Files
            .Where(f => f.Segments.Count > 0)
            .FirstOrDefault(f => !IsPar2(f));
        var anyFile = firstDataFile ?? nzb.Files.FirstOrDefault(f => f.Segments.Count > 0);
        return anyFile?.Segments[0].MessageId;
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
