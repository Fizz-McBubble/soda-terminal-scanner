using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

// One recognizer owns one cache. No images or tensors survive a request.
internal sealed class ExactRecognitionCache
{
    internal const int MaximumCapacity = 2048;
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();

    internal ExactRecognitionCache(int capacity)
    {
        if (capacity is < 0 or > MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal int Count => _entries.Count;
    internal int Capacity => _capacity;

    internal static string ContentKey(ReadOnlySpan<float> sample, int width, int height)
    {
        if (width <= 0 || height <= 0 || sample.Length != checked(3 * width * height))
            throw new ArgumentException("ocr_cache_tensor_shape_invalid");
        Span<byte> shape = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(shape, 3);
        BinaryPrimitives.WriteInt32LittleEndian(shape[4..], height);
        BinaryPrimitives.WriteInt32LittleEndian(shape[8..], width);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(shape);
        hash.AppendData(MemoryMarshal.AsBytes(sample));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal Entry? Get(string key)
    {
        if (!_entries.TryGetValue(key, out var node)) return null;
        _lru.Remove(node);
        _lru.AddFirst(node);
        return node.Value;
    }

    internal void Put(string key, string text, float confidence)
    {
        if (_capacity == 0) return;
        if (_entries.Remove(key, out var existing)) _lru.Remove(existing);
        var node = _lru.AddFirst(new Entry(key, text, confidence));
        _entries.Add(key, node);
        if (_entries.Count > _capacity)
        {
            var last = _lru.Last!;
            _entries.Remove(last.Value.Key);
            _lru.RemoveLast();
        }
    }

    internal sealed record Entry(string Key, string Text, float Confidence);
}
