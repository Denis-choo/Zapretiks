using System.Buffers.Binary;

namespace Zapretiks.Core;

public static class CoverImageData
{
    public const int MaxBytes = 96 * 1024;
    public const int MaxSide = 512;
    private const int MaxEncodedLength = (MaxBytes + 2) / 3 * 4;
    private const string InvalidCover = "Обложка решения должна быть изображением PNG или JPEG размером до 512 × 512 и 96 КБ.";

    public static string? Validate(string? value)
    {
        if (value is not null)
            Decode(value);
        return value;
    }

    public static byte[] Decode(string value)
    {
        if (value.Length is 0 or > MaxEncodedLength || value.Any(char.IsWhiteSpace))
            throw new InvalidDataException(InvalidCover);

        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            if (bytes.Length > MaxBytes || !TryReadSize(bytes, out var width, out var height)
                || width > MaxSide || height > MaxSide)
                throw new InvalidDataException(InvalidCover);
            return bytes;
        }
        catch (FormatException error)
        {
            throw new InvalidDataException(InvalidCover, error);
        }
    }

    public static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

    public static bool TryReadSize(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (IsPng(bytes))
        {
            if (bytes.Length < 33 || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13
                || !bytes[12..16].SequenceEqual("IHDR"u8))
                return false;
            width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..]);
            height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..]);
            return width > 0 && height > 0 && HasCompletePngData(bytes);
        }

        if (!bytes.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8]))
            return false;
        int offset = 2;
        while (offset < bytes.Length - 3 && bytes[offset++] == 0xff)
        {
            while (offset < bytes.Length && bytes[offset] == 0xff)
                offset++;
            if (offset >= bytes.Length - 2)
                break;
            int marker = bytes[offset++];
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || length > bytes.Length - offset)
                return false;
            if (marker == 0xda)
                return width > 0 && height > 0 && length < bytes.Length - offset - 2
                    && bytes.EndsWith((ReadOnlySpan<byte>)[0xff, 0xd9]);
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (width != 0 || length < 8 || bytes[offset + 2] != 8)
                    return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]);
            }
            else if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xcc))
                return false;
            offset += length;
        }
        return false;
    }

    private static bool HasCompletePngData(ReadOnlySpan<byte> bytes)
    {
        var foundPixels = false;
        int offset = 33;
        while (offset <= bytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > bytes.Length - offset - 12)
                return false;
            var type = bytes.Slice(offset + 4, 4);
            offset += 12 + (int)length;
            if (type.SequenceEqual("IDAT"u8))
                foundPixels |= length > 0;
            else if (type.SequenceEqual("IEND"u8))
                return foundPixels && length == 0 && offset == bytes.Length;
            else if (type.SequenceEqual("IHDR"u8) || type.SequenceEqual("acTL"u8))
                return false;
        }
        return false;
    }
}
