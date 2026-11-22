using System.Security.Cryptography;
using System.Text;
using Zapretiks.Core;

namespace Zapretiks.Infrastructure;

// The block is ASCII. Latin-1 gives every byte of the surrounding user file a
// reversible representation, including legacy Windows comments and mixed line endings.
internal sealed record ManagedList(byte[]? Original, byte[]? Manual, string[] Owned)
{
    private const string Begin = "# Zapretiks begin v1 ";
    private const string End = "# Zapretiks end v1";
    private const string Bom = "\u00ef\u00bb\u00bf";

    internal static ManagedList Read(string name, byte[]? bytes)
    {
        if (bytes is null)
            return new(null, null, []);
        if (bytes.Contains((byte)0) || bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x1f, 0x8b]))
            throw new InvalidDataException($"Список {name} должен быть обычным текстовым файлом.");

        var text = Encoding.Latin1.GetString(bytes);
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 && end < 0)
            return new(bytes, bytes, []);

        var damaged = new InvalidDataException($"В {name} изменён блок Zapretiks. Вынесите свои адреса за его границы и восстановите блок из резервной копии.");
        if (start < 0 || end < start || !AtLineStart(text, start) || !AtLineStart(text, end) ||
            text.IndexOf(Begin, start + Begin.Length, StringComparison.Ordinal) >= 0 ||
            text.IndexOf(End, end + End.Length, StringComparison.Ordinal) >= 0)
            throw damaged;

        var headerEnd = text.IndexOf('\n', start);
        if (headerEnd < 0 || headerEnd >= end)
            throw damaged;
        var header = text[(start + Begin.Length)..headerEnd].TrimEnd('\r').Split(' ');
        var entries = text[(headerEnd + 1)..end].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r')).ToArray();
        if (header.Length != 2 || header[0] is not ("new" or "existing") ||
            header[1] != Digest(entries) || entries.Length == 0)
            throw damaged;
        var tail = end + End.Length;
        if (text.AsSpan(tail).StartsWith("\r\n")) tail += 2;
        else if (text.AsSpan(tail).StartsWith("\n")) tail++;
        else if (tail != text.Length) throw damaged;
        var manual = Encoding.Latin1.GetBytes(text.Remove(start, tail - start));
        return new(bytes, manual.Length == 0 && header[0] == "new" ? null : manual, entries);
    }

    private static bool AtLineStart(string text, int offset) => offset == 0 || text[offset - 1] == '\n' ||
        offset == Bom.Length && text.StartsWith(Bom, StringComparison.Ordinal);

    internal string? ManualText => Manual is null ? null : Encoding.UTF8.GetString(Manual).TrimStart('\uFEFF');

    internal byte[]? WithEntries(IReadOnlyList<string> entries)
    {
        if (Owned.SequenceEqual(entries))
            return Original;
        if (entries.Count == 0)
            return Manual;
        var text = Manual is null ? "" : Encoding.Latin1.GetString(Manual);
        var split = text.StartsWith(Bom, StringComparison.Ordinal) ? Bom.Length : 0;
        var newline = text.Contains('\n') && !text.Contains("\r\n", StringComparison.Ordinal) ? "\n" : "\r\n";
        var origin = Manual is null ? "new" : "existing";
        var block = $"{Begin}{origin} {Digest(entries)}{newline}{string.Join(newline, entries)}{newline}{End}{newline}";
        return Encoding.Latin1.GetBytes(text.Insert(split, block));
    }

    private static string Digest(IEnumerable<string> entries) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join('\n', entries))));
}
