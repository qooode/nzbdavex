using System.Text;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._2.GetPar2FileDescriptors;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Queue.PostProcessors;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

public class QueueItemProcessor(
    QueueItem queueItem,
    Stream? queueNzbStream,
    DavDatabaseClient dbClient,
    INntpClient usenetClient,
    ConfigManager configManager,
    WebsocketManager websocketManager,
    ProviderUsageTracker providerUsageTracker,
    PlaybackAttemptLog playbackAttemptLog,
    QueueItemSourceTracker sourceTracker,
    IProgress<int> progress,
    CancellationToken ct
)
{
    public async Task ProcessAsync()
    {
        // initialize
        var startTime = DateTime.Now;
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Downloading");

        using var providerScope = providerUsageTracker.BeginScope(queueItem.Id);

        // process the job
        try
        {
            await ProcessQueueItemAsync(startTime).ConfigureAwait(false);
        }

        // When a queue-item is removed while processing,
        // then we need to clear any db changes and finish early.
        catch (Exception e) when (e.GetBaseException().IsCancellationException())
        {
            Log.Information($"Processing of queue item `{queueItem.JobName}` was cancelled.");
            dbClient.Ctx.ClearChangeTracker();
        }

        // when a retryable error is encountered
        // let's not remove the item from the queue
        // to give it a chance to retry. Simply
        // log the error and retry in a minute.
        catch (Exception e) when (e.IsRetryableDownloadException())
        {
            try
            {
                Log.Error($"Failed to process job, `{queueItem.JobName}` -- {e.Message}");
                dbClient.Ctx.ClearChangeTracker();
                queueItem.PauseUntil = DateTime.Now.AddMinutes(1);
                dbClient.Ctx.QueueItems.Attach(queueItem);
                dbClient.Ctx.Entry(queueItem).Property(x => x.PauseUntil).IsModified = true;
                await dbClient.Ctx.SaveChangesAsync().ConfigureAwait(false);
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemStatus, $"{queueItem.Id}|Queued");
            }
            catch (Exception ex)
            {
                Log.Error(ex, ex.Message);
            }
        }

        // when any other error is encountered,
        // we must still remove the queue-item and add
        // it to the history as a failed job.
        catch (Exception e)
        {
            // Surface the underlying exception (with stack) so operators can see
            // which provider/segment failed. Previously this branch only wrote
            // e.Message into the HistoryItem and was invisible in container logs.
            Log.Warning(e, "Queue item {Id} ({JobName}) failed: {Message}",
                queueItem.Id, queueItem.JobName, e.Message);
            try
            {
                await MarkQueueItemCompleted(startTime, error: e.Message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(e, ex.Message);
            }
        }
    }

    private async Task ProcessQueueItemAsync(DateTime startTime)
    {
        // if the `/blobs` folder is tampered with outside the nzbdav process,
        // then it is possible that the nzb file goes missing.
        if (queueNzbStream is null)
            throw new Exception($"The NZB file could not be found.");

        // load config for handling duplicate nzbs
        var existingMountFolder = await GetMountFolder().ConfigureAwait(false);
        var duplicateNzbBehavior = configManager.GetDuplicateNzbBehavior();

        // if the mount folder already exists and setting is `marked-failed`
        // then immediately mark the job as failed.
        var isDuplicateNzb = existingMountFolder is not null;
        if (isDuplicateNzb && duplicateNzbBehavior == "mark-failed")
        {
            const string error = "Duplicate nzb: the download folder for this nzb already exists.";
            await MarkQueueItemCompleted(startTime, error, () => Task.FromResult(existingMountFolder))
                .ConfigureAwait(false);
            return;
        }

        // read the nzb document
        var nzb = await NzbDocument.LoadAsync(queueNzbStream).ConfigureAwait(false);
        var nzbFiles = nzb.Files.Where(x => x.Segments.Count > 0).ToList();

        // Look for a password in filename and nzb document
        // The file name's password takes priority, as an easy override
        var archivePassword = FilenameUtil.GetNzbPassword(queueItem.FileName) ??
            nzb.Metadata.GetValueOrDefault("password");

        // step 0 -- perform article existence pre-check against cache
        // https://github.com/nzbdav-dev/nzbdav/issues/101
        var articlesToPrecheck = nzbFiles.SelectMany(x => x.Segments).Select(x => x.MessageId);
        HealthCheckService.CheckCachedMissingSegmentIds(articlesToPrecheck);

        // step 1 -- get name and size of each nzb file
        var part1Progress = progress
            .Scale(50, 100)
            .ToPercentage(nzbFiles.Count);
        var segments = await FetchFirstSegmentsStep.FetchFirstSegments(
            nzbFiles, usenetClient, configManager, ct, part1Progress).ConfigureAwait(false);
        var par2FileDescriptors = await GetPar2FileDescriptorsStep.GetPar2FileDescriptors(
            segments, usenetClient, ct).ConfigureAwait(false);
        var fileInfos = GetFileInfosStep.GetFileInfos(
            segments, par2FileDescriptors);

        // step 2a -- try altmount-style lazy RAR mounting for the rar group
        // when enabled. On success, the entire rar group is handled here
        // (only the first volume gets parsed) and skipped in step 2b. On
        // ineligibility — multi-file, compressed, solid, or first-volume
        // parse failure — fall through to the per-part eager pipeline.
        LazyRarProcessor.Result? lazyRarResult = null;
        var rarFiles = fileInfos.Where(x => GetGroupName(x) == "rar").ToList();
        if (configManager.IsLazyRarParsingEnabled() && rarFiles.Count > 0)
        {
            var lazyProc = new LazyRarProcessor(rarFiles, usenetClient, configManager, archivePassword, ct);
            lazyRarResult = await lazyProc.ProcessAsync().ConfigureAwait(false) as LazyRarProcessor.Result;
        }

        // step 2b -- per-file processing for everything else (and for the
        // rar group when lazy mounting was skipped or unsupported).
        var skipRarGroup = lazyRarResult is not null;
        var fileProcessors = GetFileProcessors(fileInfos, archivePassword, skipRarGroup).ToList();
        var part2Progress = progress
            .Offset(50)
            .Scale(50, 100)
            .ToMultiProgress(fileProcessors.Count);
        var fileProcessingResultsAll = await fileProcessors
            .Select(x => x!.ProcessAsync(part2Progress.SubProgress))
            .WithConcurrencyAsync(configManager.GetMaxDownloadConnections() + 5)
            .GetAllAsync(ct).ConfigureAwait(false);
        var fileProcessingResults = fileProcessingResultsAll
            .Where(x => x is not null)
            .Select(x => x!)
            .ToList();
        if (lazyRarResult is not null) fileProcessingResults.Add(lazyRarResult);

        // step 3 -- Optionally check full article existence
        var checkedFullHealth = false;
        var healthCheckCategories = configManager.GetEnsureArticleExistenceCategories();
        if (healthCheckCategories.Contains(queueItem.Category.ToLower()))
        {
            var articlesToCheck = fileInfos
                .Where(x => x.IsRar || FilenameUtil.IsImportantFileType(x.FileName))
                .SelectMany(x => x.NzbFile.GetSegmentIds())
                .ToList();
            var part3Progress = progress
                .Offset(100)
                .ToPercentage(articlesToCheck.Count);
            var healthCheckConcurrency = configManager
                .GetUsenetProviderConfig()
                .TotalPooledConnections;
            await usenetClient
                .CheckAllSegmentsAsync(articlesToCheck, healthCheckConcurrency, part3Progress, ct)
                .ConfigureAwait(false);
            checkedFullHealth = true;
        }

        // update the database
        await MarkQueueItemCompleted(startTime, error: null, async () =>
        {
            var categoryFolder = await GetOrCreateCategoryFolder().ConfigureAwait(false);
            var mountFolder = await CreateMountFolder(categoryFolder, existingMountFolder, duplicateNzbBehavior)
                .ConfigureAwait(false);
            new RarAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new FileAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new SevenZipAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);
            new MultipartMkvAggregator(dbClient, mountFolder, checkedFullHealth).UpdateDatabase(fileProcessingResults);

            // post-processing
            new RenameDuplicatesPostProcessor(dbClient).RenameDuplicates();
            new BlocklistedFilePostProcessor(configManager, dbClient).RemoveBlocklistedFiles();

            // validate video files found
            if (configManager.IsEnsureImportableVideoEnabled())
                new EnsureImportableVideoValidator(dbClient).ThrowIfValidationFails();

            // create strm files, if necessary
            if (configManager.GetImportStrategy() == "strm")
                await new CreateStrmFilesPostProcessor(configManager, dbClient).CreateStrmFilesAsync()
                    .ConfigureAwait(false);

            return mountFolder;
        }).ConfigureAwait(false);
    }

    private IEnumerable<BaseProcessor> GetFileProcessors
    (
        List<GetFileInfosStep.FileInfo> fileInfos,
        string? archivePassword,
        bool skipRarGroup = false
    )
    {
        var groups = fileInfos
            .GroupBy(GetGroupName);

        foreach (var group in groups)
        {
            if (group.Key == "7z")
                yield return new SevenZipProcessor(group.ToList(), usenetClient, configManager, archivePassword, ct);

            else if (group.Key == "rar")
            {
                if (skipRarGroup) continue;
                foreach (var fileInfo in group)
                    yield return new RarProcessor(fileInfo, usenetClient, archivePassword, ct);
            }

            else if (group.Key == "multipart-mkv")
                yield return new MultipartMkvProcessor(group.ToList(), usenetClient, ct);

            else if (group.Key == "other")
                foreach (var fileInfo in group)
                    yield return new FileProcessor(fileInfo, usenetClient, ct);
        }
    }

    private static string GetGroupName(GetFileInfosStep.FileInfo x) =>
        FilenameUtil.Is7zFile(x.FileName) ? "7z"
        : x.IsRar || FilenameUtil.IsRarFile(x.FileName) ? "rar"
        : FilenameUtil.IsMultipartMkv(x.FileName) ? "multipart-mkv"
        : "other";

    private async Task<DavItem?> GetMountFolder()
    {
        var query = from mountFolder in dbClient.Ctx.Items
            join categoryFolder in dbClient.Ctx.Items on mountFolder.ParentId equals categoryFolder.Id
            where mountFolder.Name == queueItem.JobName
                  && mountFolder.ParentId != null
                  && categoryFolder.Name == queueItem.Category
                  && categoryFolder.ParentId == DavItem.ContentFolder.Id
            select mountFolder;

        return await query.FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    private async Task<DavItem> GetOrCreateCategoryFolder()
    {
        // if the category item already exists, return it
        var categoryFolder = await dbClient.GetDirectoryChildAsync(
            DavItem.ContentFolder.Id, queueItem.Category, ct).ConfigureAwait(false);
        if (categoryFolder is not null)
            return categoryFolder;

        // otherwise, create it
        categoryFolder = DavItem.New(
            id: Guid.NewGuid(),
            parent: DavItem.ContentFolder,
            name: queueItem.Category,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: null
        );
        dbClient.Ctx.Items.Add(categoryFolder);
        return categoryFolder;
    }

    private Task<DavItem> CreateMountFolder
    (
        DavItem categoryFolder,
        DavItem? existingMountFolder,
        string duplicateNzbBehavior
    )
    {
        if (existingMountFolder is not null && duplicateNzbBehavior == "increment")
            return IncrementMountFolder(categoryFolder);

        var mountFolder = DavItem.New(
            id: Guid.NewGuid(),
            parent: categoryFolder,
            name: queueItem.JobName,
            fileSize: null,
            type: DavItem.ItemType.Directory,
            subType: DavItem.ItemSubType.Directory,
            releaseDate: null,
            lastHealthCheck: null,
            historyItemId: queueItem.Id,
            fileBlobId: null
        );
        dbClient.Ctx.Items.Add(mountFolder);
        return Task.FromResult(mountFolder);
    }

    private async Task<DavItem> IncrementMountFolder(DavItem categoryFolder)
    {
        for (var i = 2; i < 100; i++)
        {
            var name = $"{queueItem.JobName} ({i})";
            var existingMountFolder =
                await dbClient.GetDirectoryChildAsync(categoryFolder.Id, name, ct).ConfigureAwait(false);
            if (existingMountFolder is not null) continue;

            var mountFolder = DavItem.New(
                id: Guid.NewGuid(),
                parent: categoryFolder,
                name: name,
                fileSize: null,
                type: DavItem.ItemType.Directory,
                subType: DavItem.ItemSubType.Directory,
                releaseDate: null,
                lastHealthCheck: null,
                historyItemId: queueItem.Id,
                fileBlobId: null
            );
            dbClient.Ctx.Items.Add(mountFolder);
            return mountFolder;
        }

        throw new Exception("Duplicate nzb with more than 100 existing copies.");
    }

    private HistoryItem CreateHistoryItem(DavItem? mountFolder, DateTime jobStartTime, string? errorMessage = null)
    {
        return new HistoryItem()
        {
            Id = queueItem.Id,
            CreatedAt = DateTime.Now,
            FileName = queueItem.FileName,
            JobName = queueItem.JobName,
            Category = queueItem.Category,
            DownloadStatus = errorMessage == null
                ? HistoryItem.DownloadStatusOption.Completed
                : HistoryItem.DownloadStatusOption.Failed,
            TotalSegmentBytes = queueItem.TotalSegmentBytes,
            DownloadTimeSeconds = (int)(DateTime.Now - jobStartTime).TotalSeconds,
            FailMessage = errorMessage,
            DownloadDirId = mountFolder?.Id,
            NzbBlobId = queueItem.Id,
            IndexerName = queueItem.IndexerName,
        };
    }

    private async Task MarkQueueItemCompleted
    (
        DateTime startTime,
        string? error = null,
        Func<Task<DavItem?>>? databaseOperations = null
    )
    {
        dbClient.Ctx.ClearChangeTracker();
        var mountFolder = databaseOperations != null ? await databaseOperations.Invoke().ConfigureAwait(false) : null;
        var historyItem = CreateHistoryItem(mountFolder, startTime, error);
        var providerUsage = providerUsageTracker.Snapshot(queueItem.Id);
        var historySlot = GetHistoryResponse.HistorySlot.FromHistoryItem(historyItem, mountFolder, configManager, providerUsage);
        dbClient.Ctx.QueueItems.Remove(queueItem);
        dbClient.Ctx.HistoryItems.Add(historyItem);
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, queueItem.Id.ToString());
        _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemAdded, historySlot.ToJson());
        _ = DavDatabaseContext.RcloneVfsForget(["/nzbs"]);
        _ = RefreshMonitoredDownloads();
        RecordWatchdogAttemptIfExternal(startTime, error, providerUsage);
    }

    // Emits a Watchdog attempt entry for queue items that didn't come through
    // ProfilePlayController (which writes its own attempts already). Lets users
    // see service-mode AIOStreams / Sonarr enqueues with provider attribution
    // on the /watchdog page.
    private void RecordWatchdogAttemptIfExternal(
        DateTime startTime,
        string? error,
        IReadOnlyDictionary<string, long> providerUsage)
    {
        if (sourceTracker.ConsumeIsProfileFlow(queueItem.Id)) return;
        if (!configManager.IsPlaybackWatchdogEnabled()) return;

        var attemptedAt = new DateTimeOffset(startTime.ToUniversalTime(), TimeSpan.Zero);
        var durationMs = (int)Math.Max(0, (DateTime.Now - startTime).TotalMilliseconds);
        var outcome = error == null
            ? PlaybackAttemptLog.Outcome.QueueCompleted
            : PlaybackAttemptLog.Outcome.QueueFailed;
        var providerHost = FormatProviders(providerUsage);

        playbackAttemptLog.Record(new PlaybackAttemptLog.Attempt
        {
            ClickId = queueItem.Id,
            AttemptedAt = attemptedAt,
            ContentType = string.IsNullOrEmpty(queueItem.Category) ? "unknown" : queueItem.Category,
            RequestedTitle = queueItem.JobName ?? queueItem.FileName,
            CandidateTitle = queueItem.JobName ?? queueItem.FileName,
            IndexerName = queueItem.IndexerName ?? "—",
            Size = queueItem.TotalSegmentBytes,
            RankIndex = 0,
            Result = outcome,
            FailReason = error,
            DurationMs = durationMs,
            IsWinner = error == null,
            ProviderHost = providerHost,
        });
    }

    private static string? FormatProviders(IReadOnlyDictionary<string, long> usage)
    {
        if (usage.Count == 0) return null;
        var total = usage.Values.Sum();
        if (total == 0) return string.Join(", ", usage.Keys);
        return string.Join(", ", usage
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key} ({(int)Math.Round(100.0 * kv.Value / total)}%)"));
    }

    private async Task RefreshMonitoredDownloads()
    {
        var tasks = configManager
            .GetArrConfig()
            .GetArrClients()
            .Select(RefreshMonitoredDownloads);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task RefreshMonitoredDownloads(ArrClient arrClient)
    {
        try
        {
            var downloadClients = await arrClient.GetDownloadClientsAsync().ConfigureAwait(false);
            if (downloadClients.All(x => x.Category != queueItem.Category)) return;
            var queueCount = await arrClient.GetQueueCountAsync().ConfigureAwait(false);
            if (queueCount < 300) await arrClient.RefreshMonitoredDownloads().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Debug($"Could not refresh monitored downloads for Arr instance: `{arrClient.Host}`. {e.Message}");
        }
    }
}
