using System.Collections.Generic;

namespace DeployAngel.Core;

/// <summary>
/// Bounded FIFO of payloads waiting to be sent. When full, the oldest payload
/// is dropped: losing telemetry is always preferable to growing memory in the
/// customer's process.
/// </summary>
internal sealed class PayloadBuffer
{
    private readonly LinkedList<byte[]> _items = new();
    private readonly object _lock = new();

    public PayloadBuffer(int limit)
    {
        Limit = limit;
    }

    public int Limit { get; }
    public int Dropped { get; private set; }

    public int Count
    {
        get { lock (_lock) return _items.Count; }
    }

    public void Push(byte[] item)
    {
        lock (_lock)
        {
            _items.AddLast(item);
            while (_items.Count > Limit)
            {
                _items.RemoveFirst();
                Dropped++;
            }
        }
    }

    public byte[]? Shift()
    {
        lock (_lock)
        {
            if (_items.First is null) return null;
            var item = _items.First.Value;
            _items.RemoveFirst();
            return item;
        }
    }

    public void Unshift(byte[] item)
    {
        lock (_lock)
        {
            if (_items.Count < Limit) _items.AddFirst(item);
        }
    }
}
