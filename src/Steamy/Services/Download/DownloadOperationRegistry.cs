using System.Collections.Concurrent;

namespace Steamy.Services;

/// <summary>One owner per job until its process and persistence have finished.</summary>
public sealed class DownloadOperationRegistry
{
    private readonly ConcurrentDictionary<Guid, Operation> _operations = new();

    public bool TryRegister(Guid id, CancellationTokenSource cancellation) =>
        _operations.TryAdd(id, new Operation(cancellation));

    public bool IsRunning(Guid id) => _operations.ContainsKey(id);
    public bool IsPauseRequested(Guid id) => _operations.TryGetValue(id, out var operation) && operation.PauseRequested;
    public Operation? Find(Guid id) => _operations.GetValueOrDefault(id);

    public void Complete(Guid id)
    {
        if (_operations.TryRemove(id, out var operation)) operation.Complete();
    }

    public void CancelAll()
    {
        foreach (var operation in _operations.Values) operation.Cancel();
    }

    public sealed class Operation
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pauseRequested;

        internal Operation(CancellationTokenSource cancellation) => _cancellation = cancellation;
        public bool PauseRequested => Volatile.Read(ref _pauseRequested) != 0;
        public Task Completion => _completion.Task;
        public void RequestStop(bool pause) => Interlocked.Exchange(ref _pauseRequested, pause ? 1 : 0);
        public void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        internal void Complete() => _completion.TrySetResult(true);
    }
}
