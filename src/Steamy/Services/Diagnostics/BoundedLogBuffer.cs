namespace Steamy.Services;

public sealed record LogBufferBatch<T>(IReadOnlyList<T> Items, long OmittedRoutine, long OmittedImportant);

/// <summary>
/// A bounded FIFO used for log bursts. Important entries displace routine output first. If even
/// important entries exceed capacity, the omitted count is returned to the consumer for an explicit
/// diagnostic; a stalled dispatcher or disk can never grow an unlimited backlog.
/// </summary>
public sealed class BoundedLogBuffer<T>
{
    private readonly int _capacity;
    private readonly Func<T, bool> _isImportant;
    private readonly LinkedList<T> _pending = new();
    private readonly Queue<LinkedListNode<T>> _routine = new();
    private readonly object _gate = new();
    private long _omittedRoutine;
    private long _omittedImportant;

    public BoundedLogBuffer(int capacity, Func<T, bool> isImportant)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentNullException.ThrowIfNull(isImportant);
        _capacity = capacity;
        _isImportant = isImportant;
    }

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    public void Enqueue(T item)
    {
        lock (_gate)
        {
            var important = _isImportant(item);
            if (_pending.Count == _capacity)
            {
                // Keep the newest routine output where possible, but do not let it evict errors.
                // A second FIFO makes this constant-time even when a burst fills the buffer
                // with warnings; rejected progress output never scans the whole backlog.
                if (_routine.Count > 0) { _pending.Remove(_routine.Dequeue()); _omittedRoutine++; }
                else if (!important) { _omittedRoutine++; return; }
                else { _pending.RemoveFirst(); _omittedImportant++; }
            }
            var node = _pending.AddLast(item);
            if (!important) _routine.Enqueue(node);
        }
    }

    public LogBufferBatch<T> Drain(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        lock (_gate)
        {
            var entries = new List<T>(Math.Min(maximum, _pending.Count));
            while (_pending.First is { } first && entries.Count < maximum)
            {
                entries.Add(first.Value);
                _pending.RemoveFirst();
                if (_routine.TryPeek(out var routine) && ReferenceEquals(first, routine)) _routine.Dequeue();
            }
            var batch = new LogBufferBatch<T>(entries, _omittedRoutine, _omittedImportant);
            _omittedRoutine = _omittedImportant = 0;
            return batch;
        }
    }
}
