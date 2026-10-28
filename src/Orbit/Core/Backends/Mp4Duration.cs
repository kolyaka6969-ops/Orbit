using System.Buffers.Binary;

namespace Orbit.Core.Backends;

/// <summary>
/// Длительность фрагментированного MP4 (moov без длительности, кадры в moof): Windows такие файлы играет и перекодирует,
/// но длину не сообщает. Складываем её из фрагментов первой дорожки; читаются только заголовки, данные пропускаются.
/// </summary>
internal static class Mp4Duration
{
    public static TimeSpan? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            uint trackId = 0, timescale = 0, defaultDuration = 0;
            ulong end = 0;
            foreach (var (type, start, size) in Boxes(stream, 0, stream.Length))
            {
                if (type == "moov")
                {
                    var moov = Read(stream, start, size);
                    if (Find(moov, "trak") is { } trak)
                    {
                        if (Find(trak, "tkhd") is { } tkhd)
                            trackId = BinaryPrimitives.ReadUInt32BigEndian(tkhd.Span[(tkhd.Span[0] == 1 ? 20 : 12)..]);
                        if (Find(trak, "mdia") is { } mdia && Find(mdia, "mdhd") is { } mdhd)
                            timescale = BinaryPrimitives.ReadUInt32BigEndian(mdhd.Span[(mdhd.Span[0] == 1 ? 20 : 12)..]);
                    }
                    if (Find(moov, "mvex") is { } mvex)
                    {
                        foreach (var trex in All(mvex, "trex"))
                            if (BinaryPrimitives.ReadUInt32BigEndian(trex.Span[4..]) == trackId)
                                defaultDuration = BinaryPrimitives.ReadUInt32BigEndian(trex.Span[12..]);
                    }
                }
                else if (type == "moof" && timescale > 0)
                {
                    foreach (var traf in All(Read(stream, start, size), "traf"))
                    {
                        if (Find(traf, "tfhd") is not { } tfhd || BinaryPrimitives.ReadUInt32BigEndian(tfhd.Span[4..]) != trackId)
                            continue;
                        var trafDefault = TfhdDefaultDuration(tfhd.Span) ?? defaultDuration;
                        ulong time = 0;
                        if (Find(traf, "tfdt") is { } tfdt)
                            time = tfdt.Span[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(tfdt.Span[4..]) : BinaryPrimitives.ReadUInt32BigEndian(tfdt.Span[4..]);
                        foreach (var trun in All(traf, "trun"))
                            time += TrunDuration(trun.Span, trafDefault);
                        end = Math.Max(end, time);
                    }
                }
            }
            return timescale > 0 && end > 0 ? TimeSpan.FromSeconds((double)end / timescale) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static uint? TfhdDefaultDuration(ReadOnlySpan<byte> tfhd)
    {
        var flags = BinaryPrimitives.ReadUInt32BigEndian(tfhd) & 0xFFFFFF;
        var at = 8;
        if ((flags & 0x01) != 0) at += 8; // base data offset
        if ((flags & 0x02) != 0) at += 4; // sample description index
        return (flags & 0x08) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(tfhd[at..]) : null;
    }

    private static ulong TrunDuration(ReadOnlySpan<byte> trun, uint defaultDuration)
    {
        var flags = BinaryPrimitives.ReadUInt32BigEndian(trun) & 0xFFFFFF;
        var count = BinaryPrimitives.ReadUInt32BigEndian(trun[4..]);
        if ((flags & 0x100) == 0)
            return (ulong)count * defaultDuration;
        var at = 8;
        if ((flags & 0x01) != 0) at += 4; // data offset
        if ((flags & 0x04) != 0) at += 4; // first sample flags
        var stride = 4 + ((flags & 0x200) != 0 ? 4 : 0) + ((flags & 0x400) != 0 ? 4 : 0) + ((flags & 0x800) != 0 ? 4 : 0);
        ulong total = 0;
        for (var i = 0; i < count; i++, at += stride)
            total += BinaryPrimitives.ReadUInt32BigEndian(trun[at..]);
        return total;
    }

    /// <summary>Коробки верхнего уровня: тип, начало содержимого и его длина.</summary>
    private static IEnumerable<(string Type, long Start, long Size)> Boxes(FileStream stream, long offset, long end)
    {
        var header = new byte[16];
        while (offset + 8 <= end)
        {
            stream.Position = offset;
            stream.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            var headerSize = 8;
            if (size == 1)
            {
                stream.ReadExactly(header, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0)
                size = end - offset;
            if (size < headerSize)
                yield break;
            yield return (type, offset + headerSize, size - headerSize);
            offset += size;
        }
    }

    private static ReadOnlyMemory<byte> Read(FileStream stream, long start, long size)
    {
        if (size > 16 * 1024 * 1024)
            throw new IOException("Слишком большой заголовок.");
        var buffer = new byte[size];
        stream.Position = start;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static ReadOnlyMemory<byte>? Find(ReadOnlyMemory<byte> parent, string type) =>
        All(parent, type).Select(m => (ReadOnlyMemory<byte>?)m).FirstOrDefault();

    /// <summary>Вложенные коробки нужного типа (только 32-битные размеры — в заголовках других не бывает).</summary>
    private static IEnumerable<ReadOnlyMemory<byte>> All(ReadOnlyMemory<byte> parent, string type)
    {
        var at = 0;
        while (at + 8 <= parent.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(parent.Span[at..]);
            if (size < 8 || at + size > parent.Length)
                yield break;
            if (System.Text.Encoding.ASCII.GetString(parent.Span.Slice(at + 4, 4)) == type)
                yield return parent.Slice(at + 8, size - 8);
            at += size;
        }
    }
}
