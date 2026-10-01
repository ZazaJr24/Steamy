namespace Steamy.Services;

/// <summary>
/// Picks the next job only when a slot opens. Changing priority, cancelling or removing a
/// waiting job therefore takes effect before its process starts, including during a queue run.
/// </summary>
public static class DownloadQueueScheduler
{
    public static async Task RunAsync<T>(Func<IEnumerable<T>> waitingJobs, Func<T, int> priority,
        Func<T, long> position, Func<T, Task> start, int parallel, CancellationToken cancellationToken = default,
        Func<int>? externallyActiveJobs = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(waitingJobs);
        ArgumentNullException.ThrowIfNull(start);
        var claimed = new HashSet<T>();
        var running = new List<Task>();
        var slots = Math.Clamp(parallel, 1, 16);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (running.Count < Math.Max(0, slots - (externallyActiveJobs?.Invoke() ?? 0)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = waitingJobs().Where(job => !claimed.Contains(job))
                        .OrderByDescending(priority).ThenBy(position).Take(1).ToArray();
                    if (candidate.Length == 0) break;
                    var next = candidate[0];
                    claimed.Add(next);
                    // Keep the caller's context: queue collections belong to the UI thread.
                    running.Add(start(next));
                }

                if (running.Count == 0)
                {
                    if (!waitingJobs().Any(job => !claimed.Contains(job))) return;
                    // A download started from another page, or before Stop queue, can still
                    // occupy every slot. Wait without blocking the dispatcher and recheck.
                    await Task.Delay(200, cancellationToken);
                    continue;
                }
                var finished = await Task.WhenAny(running).WaitAsync(cancellationToken);
                running.Remove(finished);
                await finished;
            }
        }
        finally
        {
            // Stopping the queue prevents new starts; already-running downloads keep their
            // independent Pause / Cancel controls. Observe their errors without blocking stop.
            foreach (var operation in running)
                _ = operation.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
