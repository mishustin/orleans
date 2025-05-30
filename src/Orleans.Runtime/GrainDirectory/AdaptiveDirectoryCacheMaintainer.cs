#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.GrainDirectory
{
    internal sealed partial class AdaptiveDirectoryCacheMaintainer
    {
        private static readonly TimeSpan SLEEP_TIME_BETWEEN_REFRESHES = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1); // this should be something like minTTL/4

        private readonly AdaptiveGrainDirectoryCache cache;
        private readonly LocalGrainDirectory router;
        private readonly IInternalGrainFactory grainFactory;
        private readonly CancellationTokenSource _shutdownCts = new();

        private long lastNumAccesses;       // for stats
        private long lastNumHits;           // for stats
        private Task? _runTask;

        internal AdaptiveDirectoryCacheMaintainer(
            LocalGrainDirectory router,
            AdaptiveGrainDirectoryCache cache,
            IInternalGrainFactory grainFactory,
            ILoggerFactory loggerFactory)
        {
            Log = loggerFactory.CreateLogger<AdaptiveDirectoryCacheMaintainer>();
            this.grainFactory = grainFactory;
            this.router = router;
            this.cache = cache;

            lastNumAccesses = 0;
            lastNumHits = 0;
        }

        private ILogger<AdaptiveDirectoryCacheMaintainer> Log { get; }

        public void Start()
        {
            _runTask = Run();
        }

        public async Task StopAsync()
        {
            _shutdownCts.Cancel();
            if (_runTask is { } task)
            {
                await task;
            }
        }

        private async Task Run()
        {
            // Immediately yield back to the caller
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);

            var cancellationToken = _shutdownCts.Token;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // recheck every X seconds (Consider making it a configurable parameter)
                    await Task.Delay(SLEEP_TIME_BETWEEN_REFRESHES, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // Run through all cache entries and do the following:
                    // 1. If the entry is not expired, skip it
                    // 2. If the entry is expired and was not accessed in the last time interval -- throw it away
                    // 3. If the entry is expired and was accessed in the last time interval, put into "fetch-batch-requests" list

                    // At the end of the process, fetch batch requests for entries that need to be refreshed

                    // Upon receiving refreshing answers, if the entry was not changed, double its expiration timer.
                    // If it was changed, update the cache and reset the expiration timer.

                    // this dictionary holds a map between a silo address and the list of grains that need to be refreshed
                    var fetchInBatchList = new Dictionary<SiloAddress, List<GrainId>>();

                    // get the list of cached grains

                    // Stats for debugging.
                    int ownedAndRemovedCount = 0, keptCount = 0, removedCount = 0, refreshedCount = 0;

                    // run through all cache entries
                    var enumerator = cache.GetStoredEntries();
                    while (enumerator.MoveNext())
                    {
                        var pair = enumerator.Current;
                        GrainId grain = pair.Key;
                        var entry = pair.Value;

                        var owner = router.CalculateGrainDirectoryPartition(grain);
                        if (owner == null) // Null means there's no other silo and we're shutting down, so skip this entry
                        {
                            continue;
                        }

                        if (entry == null)
                        {
                            // 0. If the entry was deleted in parallel, presumably due to cleanup after silo death
                            cache.Remove(grain);
                            removedCount++; // for debug
                        }
                        else if (!entry.IsExpired())
                        {
                            // 1. If the entry is not expired, skip it
                            keptCount++; // for debug
                        }
                        else if (entry.NumAccesses == 0)
                        {
                            // 2. If the entry is expired and was not accessed in the last time interval -- throw it away
                            cache.Remove(grain);
                            removedCount++; // for debug
                        }
                        else
                        {
                            // 3. If the entry is expired and was accessed in the last time interval, put into "fetch-batch-requests" list
                            if (!fetchInBatchList.TryGetValue(owner, out var list))
                            {
                                fetchInBatchList[owner] = list = new List<GrainId>();
                            }

                            list.Add(grain);
                            // And reset the entry's access count for next time
                            entry.NumAccesses = 0;
                            refreshedCount++; // for debug
                        }
                    }

                    LogTraceSelfOwnedAndRemoved(Log, router.MyAddress, ownedAndRemovedCount, keptCount, removedCount, refreshedCount);

                    // Send batch requests
                    SendBatchCacheRefreshRequests(fetchInBatchList);

                    ProduceStats();
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Log.LogError(ex, $"Error in {nameof(AdaptiveDirectoryCacheMaintainer)}.");
                }
            }
        }

        private void SendBatchCacheRefreshRequests(Dictionary<SiloAddress, List<GrainId>> refreshRequests)
        {
            foreach (var kv in refreshRequests)
            {
                var cachedGrainAndETagList = BuildGrainAndETagList(kv.Value);

                var silo = kv.Key;

                DirectoryInstruments.ValidationsCacheSent.Add(1);
                // Send all of the items in one large request
                var validator = this.grainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryCacheValidatorType, silo);

                router.CacheValidator.QueueTask(async () =>
                {
                    var response = await validator.LookUpMany(cachedGrainAndETagList);
                    ProcessCacheRefreshResponse(silo, response);
                }).Ignore();

                LogTraceSendingRequest(Log, router.MyAddress, silo, cachedGrainAndETagList.Count);
            }
        }

        private void ProcessCacheRefreshResponse(
            SiloAddress silo,
            List<AddressAndTag> refreshResponse)
        {
            LogTraceReceivedProcessCacheRefreshResponse(Log, router.MyAddress, refreshResponse.Count);

            int otherSiloCount = 0, updatedCount = 0, unchangedCount = 0;

            // pass through returned results and update the cache if needed
            foreach (var tuple in refreshResponse)
            {
                if (tuple.Address is { IsComplete: true })
                {
                    // the server returned an updated entry
                    cache.AddOrUpdate(tuple.Address, tuple.VersionTag);
                    otherSiloCount++;
                }
                else if (tuple.Address is { IsComplete: false })
                {
                    if (tuple.VersionTag == -1)
                    {
                        // The server indicates that it does not own the grain anymore.
                        // It could be that by now, the cache has been already updated and contains an entry received from another server (i.e., current owner for the grain).
                        // For simplicity, we do not care about this corner case and simply remove the cache entry.
                        cache.Remove(tuple.Address.GrainId);
                        updatedCount++;
                    }
                    else
                    {
                        // The server returned only a (not -1) generation number, indicating that we hold the most
                        // updated copy of the grain's activations list.
                        // Validate that the generation number in the request and the response are equal
                        // Contract.Assert(tuple.Item2 == refreshRequest.Find(o => o.Item1 == tuple.Item1).Item2);
                        // refresh the entry in the cache
                        cache.MarkAsFresh(tuple.Address.GrainId);
                        unchangedCount++;
                    }
                }
            }

            LogTraceProcessedRefreshResponse(Log, router.MyAddress, silo, otherSiloCount, updatedCount, unchangedCount);
        }

        /// <summary>
        /// Gets the list of grains (all owned by the same silo) and produces a new list
        /// of tuples, where each tuple holds the grain and its generation counter currently stored in the cache
        /// </summary>
        /// <param name="grains">List of grains owned by the same silo</param>
        /// <returns>List of grains in input along with their generation counters stored in the cache </returns>
        private List<(GrainId, int)> BuildGrainAndETagList(List<GrainId> grains)
        {
            var grainAndETagList = new List<(GrainId, int)>();

            foreach (GrainId grain in grains)
            {
                // NOTE: should this be done with TryGet? Won't Get invoke the LRU getter function?
                AdaptiveGrainDirectoryCache.GrainDirectoryCacheEntry entry = cache.Get(grain);

                if (entry != null)
                {
                    grainAndETagList.Add((grain, entry.ETag));
                }
                else
                {
                    // this may happen only if the LRU cache is full and decided to drop this grain
                    // while we try to refresh it
                    Log.LogWarning(
                        (int)ErrorCode.Runtime_Error_100199,
                        "Grain {GrainId} disappeared from the cache during maintenance",
                        grain);
                }
            }

            return grainAndETagList;
        }

        private void ProduceStats()
        {
            // We do not want to synchronize the access on numAccess and numHits in cache to avoid performance issues.
            // Thus we take the current reading of these fields and calculate the stats. We might miss an access or two,
            // but it should not be matter.
            long curNumAccesses = cache.NumAccesses;
            long curNumHits = cache.NumHits;

            long numAccesses = curNumAccesses - lastNumAccesses;
            long numHits = curNumHits - lastNumHits;

            if (Log.IsEnabled(LogLevel.Trace)) Log.LogTrace("#accesses: {AccessCount}, hit-ratio: {HitRatio}%", numAccesses, (numHits / Math.Max(numAccesses, 0.00001)) * 100);

            lastNumAccesses = curNumAccesses;
            lastNumHits = curNumHits;
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Silo {SiloAddress} self-owned (and removed) {OwnedAndRemovedCount}, kept {KeptCount}, removed {RemovedCount} and tried to refresh {RefreshedCount} grains"
        )]
        private static partial void LogTraceSelfOwnedAndRemoved(ILogger logger, SiloAddress siloAddress, int ownedAndRemovedCount, int keptCount, int removedCount, int refreshedCount);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Silo {SiloAddress} is sending request to silo {OwnerSilo} with {Count} entries"
        )]
        private static partial void LogTraceSendingRequest(ILogger logger, SiloAddress siloAddress, SiloAddress ownerSilo, int count);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Silo {SiloAddress} received ProcessCacheRefreshResponse. #Response entries {Count}."
        )]
        private static partial void LogTraceReceivedProcessCacheRefreshResponse(ILogger logger, SiloAddress siloAddress, int count);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Silo {SiloAddress} processed refresh response from {OtherSilo} with {UpdatedCount} updated, {RemovedCount} removed, {UnchangedCount} unchanged grains"
        )]
        private static partial void LogTraceProcessedRefreshResponse(ILogger logger, SiloAddress siloAddress, SiloAddress otherSilo, int updatedCount, int removedCount, int unchangedCount);
    }
}
