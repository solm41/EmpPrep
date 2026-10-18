using System.Text;

namespace EmpPrep.Services;

/// <summary>
/// Minimal bencode writer — the only encoding a .torrent metainfo file needs.
/// </summary>
/// <remarks>
/// Written by hand rather than taken from a package because the encoder has to be exact: an
/// infohash is the SHA-1 of the encoded <c>info</c> dictionary, so any deviation (key order,
/// integer formatting, string encoding) produces a torrent whose hash the tracker will not
/// recognise. Values are modelled as a small closed set rather than reflected from objects so
/// there is no ambiguity about how a given CLR type is serialised.
/// </remarks>
public abstract record BencodeValue
{
    public static BencodeValue Int(long value) => new BencodeInteger(value);
    public static BencodeValue Str(string value) => new BencodeBytes(Encoding.UTF8.GetBytes(value));
    public static BencodeValue Bytes(byte[] value) => new BencodeBytes(value);
    public static BencodeValue List(IEnumerable<BencodeValue> items) => new BencodeList([.. items]);
    public static BencodeDictionary Dict() => new();

    internal abstract void Write(Stream output);
}

public sealed record BencodeInteger(long Value) : BencodeValue
{
    internal override void Write(Stream output)
    {
        output.WriteByte((byte)'i');
        // Invariant formatting: a culture with a different negative sign or digit shapes would
        // otherwise produce an unparseable integer.
        WriteAscii(output, Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        output.WriteByte((byte)'e');
    }

    internal static void WriteAscii(Stream output, string text)
    {
        foreach (var ch in text)
            output.WriteByte((byte)ch);
    }
}

public sealed record BencodeBytes(byte[] Value) : BencodeValue
{
    internal override void Write(Stream output)
    {
        BencodeInteger.WriteAscii(output, Value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        output.WriteByte((byte)':');
        output.Write(Value, 0, Value.Length);
    }
}

public sealed record BencodeList(IReadOnlyList<BencodeValue> Items) : BencodeValue
{
    internal override void Write(Stream output)
    {
        output.WriteByte((byte)'l');
        foreach (var item in Items)
            item.Write(output);
        output.WriteByte((byte)'e');
    }
}

/// <summary>A bencode dictionary. Keys are emitted in raw byte order, as the spec requires.</summary>
public sealed record BencodeDictionary : BencodeValue
{
    private readonly Dictionary<string, BencodeValue> _entries = [];

    public BencodeDictionary Add(string key, BencodeValue value)
    {
        _entries[key] = value;
        return this;
    }

    public BencodeDictionary Add(string key, long value) => Add(key, BencodeValue.Int(value));

    /// <summary>Add a string entry, skipping it entirely when the value is blank.</summary>
    public BencodeDictionary AddIfPresent(string key, string? value)
        => string.IsNullOrWhiteSpace(value) ? this : Add(key, BencodeValue.Str(value));

    internal override void Write(Stream output)
    {
        output.WriteByte((byte)'d');

        // Bencode requires keys sorted as raw byte strings. Ordinal string comparison matches that
        // for the ASCII keys a torrent uses, and encoding first keeps it correct for any other key.
        var ordered = _entries
            .Select(entry => (Key: Encoding.UTF8.GetBytes(entry.Key), entry.Value))
            .OrderBy(entry => entry.Key, ByteArrayComparer.Instance);

        foreach (var (key, value) in ordered)
        {
            new BencodeBytes(key).Write(output);
            value.Write(output);
        }

        output.WriteByte((byte)'e');
    }

    public byte[] Encode()
    {
        using var buffer = new MemoryStream();
        Write(buffer);
        return buffer.ToArray();
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? left, byte[]? right)
            => left is null || right is null
                ? Comparer<int>.Default.Compare(left is null ? 0 : 1, right is null ? 0 : 1)
                : left.AsSpan().SequenceCompareTo(right.AsSpan());
    }
}
