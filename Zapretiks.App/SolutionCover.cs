using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zapretiks.Core;

namespace Zapretiks.App;

internal static class SolutionCover
{
    private const int MaxSourceBytes = 8 * 1024 * 1024;
    private const int MaxSourceSide = 4096;
    private static readonly ConditionalWeakTable<string, CacheEntry> Thumbnails = new();

    public static BitmapSource? Decode(string? encoded) => encoded is null ? null :
        Thumbnails.GetValue(encoded, key =>
        {
            try { return new(ReadBitmap(CoverImageData.Decode(key), CoverImageData.MaxSide)); }
            catch (Exception error) when (IsImageError(error)) { return new(null); }
        }).Bitmap;

    public static async Task<string> ReadFileAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is 0 or > MaxSourceBytes)
            throw new InvalidDataException("Выберите изображение PNG или JPEG размером до 8 МБ.");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes).ConfigureAwait(false);
        return await Task.Run(() => Encode(bytes)).ConfigureAwait(false);
    }

    private static string Encode(byte[] bytes)
    {
        try
        {
            var source = ReadBitmap(bytes, MaxSourceSide);
            for (int size = CoverImageData.MaxSide; size >= 120; size = size * 3 / 4)
            {
                var pixels = Resize(source, size);
                BitmapEncoder encoder = CoverImageData.IsPng(bytes)
                    ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 88 };
                using var result = new MemoryStream();
                encoder.Frames.Add(BitmapFrame.Create(pixels));
                encoder.Save(result);
                if (result.Length <= CoverImageData.MaxBytes)
                    return Convert.ToBase64String(result.ToArray());
            }
        }
        catch (Exception error) when (IsImageError(error))
        {
            throw new InvalidDataException("Не удалось прочитать изображение. Выберите PNG или JPEG до 4096 × 4096.", error);
        }
        throw new InvalidDataException("Не удалось подготовить обложку. Попробуйте другую картинку.");
    }

    private static BitmapSource ReadBitmap(byte[] bytes, int maxSide)
    {
        if (!CoverImageData.TryReadSize(bytes, out var width, out var height) || width > maxSide || height > maxSide)
            throw new InvalidDataException("Неподдерживаемый размер или формат изображения.");

        using var stream = new MemoryStream(bytes, writable: false);
        const BitmapCreateOptions options = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
        BitmapDecoder decoder = CoverImageData.IsPng(bytes)
            ? new PngBitmapDecoder(stream, options, BitmapCacheOption.OnLoad)
            : new JpegBitmapDecoder(stream, options, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
            throw new InvalidDataException("Для обложки нужно одно изображение.");
        var source = decoder.Frames[0];
        if (source.PixelWidth != width || source.PixelHeight != height)
            throw new InvalidDataException("Некорректные размеры изображения.");
        source.Freeze();
        return source;
    }

    private static BitmapSource Resize(BitmapSource source, int maxSide)
    {
        var scale = Math.Min(1, (double)maxSide / Math.Max(source.PixelWidth, source.PixelHeight));
        // Keep very wide or very tall valid images at least one pixel across.
        var horizontal = Math.Max(1d / source.PixelWidth, scale);
        var vertical = Math.Max(1d / source.PixelHeight, scale);
        if (scale < 1)
            source = new TransformedBitmap(source, new ScaleTransform(horizontal, vertical));
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32,
            null, pixels, stride);
    }

    private static bool IsImageError(Exception error) => error is InvalidDataException or IOException or FormatException or NotSupportedException
        or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException;

    private sealed record CacheEntry(BitmapSource? Bitmap);
}
