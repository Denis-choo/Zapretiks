using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Zapretiks.Core;

namespace Zapretiks.Infrastructure;

public sealed class EmbeddedCatalogService : ICatalogService
{
    public async Task<IReadOnlyList<SolutionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = typeof(EmbeddedCatalogService).Assembly.GetManifestResourceStream("Zapretiks.Catalog.json")
            ?? throw new InvalidDataException("Встроенный каталог недоступен.");
        var profiles = await ProfileJson.ReadAsync<SolutionProfile[]>(stream, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return profiles.Select(profile => ProfileJson.NormalizeUnique(profile, seen)).ToArray();
    }
}

public sealed class JsonUserStore : IUserStore
{
    private const int MaxStateBytes = 32 * 1024 * 1024;
    private const int MaxProfiles = 1024;
    private readonly string _statePath;

    public JsonUserStore(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zapretiks"));
        _statePath = Path.Combine(DataDirectory, "state.json");
    }

    public string DataDirectory { get; }

    public async Task<UserState> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = ProfileJson.OpenRead(_statePath, MaxStateBytes);
            var state = await ProfileJson.ReadAsync<UserState>(stream, cancellationToken).ConfigureAwait(false);
            return NormalizeState(state);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return UserState.Empty;
        }
    }

    public Task SaveAsync(UserState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeState(state);
        Directory.CreateDirectory(DataDirectory);
        return ProfileJson.WriteAtomicAsync(_statePath, normalized, MaxStateBytes, cancellationToken);
    }

    private static UserState NormalizeState(UserState state)
    {
        if (state.Settings is null || state.Profiles is null || state.Profiles.Length > MaxProfiles)
            throw new InvalidDataException($"Библиотека должна содержать настройки и до {MaxProfiles} решений.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profiles = state.Profiles.Select(profile => profile is null
            ? throw new InvalidDataException("В библиотеке найден пустое решение.")
            : profile with { Profile = ProfileJson.NormalizeUnique(profile.Profile, seen) }).ToArray();
        return state with { Profiles = profiles };
    }
}

public sealed class JsonProfileTransfer : IProfileTransfer
{
    public const int MaxProfileBytes = 512 * 1024;

    public async Task<SolutionProfile> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = ProfileJson.OpenRead(path, MaxProfileBytes);
        var profile = await ProfileJson.ReadAsync<SolutionProfile>(stream, cancellationToken).ConfigureAwait(false);
        return ProfileValidation.Normalize(profile) with { IsVerified = false, Source = "Локальный импорт" };
    }

    public Task ExportAsync(string path, SolutionProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ProfileJson.WriteAtomicAsync(path, ProfileValidation.Normalize(profile), MaxProfileBytes, cancellationToken);
    }
}

internal static class ProfileJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        MaxDepth = 12,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),
        Converters = { new JsonStringEnumConverter<ProfileCategory>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static SolutionProfile NormalizeUnique(SolutionProfile? profile, HashSet<string> seen)
    {
        if (profile is null)
            throw new InvalidDataException("В данных найден пустое решение.");

        var normalized = ProfileValidation.Normalize(profile);
        if (!seen.Add(normalized.Id))
            throw new InvalidDataException($"Решение с идентификатором «{normalized.Id}» уже существует.");

        return normalized;
    }

    public static FileStream OpenRead(string path, int limit)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length > limit)
                throw new InvalidDataException($"Размер файла превышает {limit / 1024} КБ.");

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("JSON-файл пуст.");
        }
        catch (JsonException exception)
        {
            var location = exception.Path is null ? "" : $" Поле {exception.Path}.";
            throw new InvalidDataException($"Некорректный JSON или неподдерживаемая структура файла.{location}", exception);
        }
    }

    public static async Task WriteAtomicAsync<T>(string path, T value, int limit, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(fullPath)!, $".zapretiks-{Guid.NewGuid():N}.tmp");
        var temporaryFileExists = false;
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                temporaryFileExists = true;
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
                if (stream.Length > limit)
                    throw new InvalidDataException($"Размер данных превышает {limit / 1024} КБ.");

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
            temporaryFileExists = false;
        }
        finally
        {
            if (temporaryFileExists)
                File.Delete(temporaryPath);
        }
    }
}
