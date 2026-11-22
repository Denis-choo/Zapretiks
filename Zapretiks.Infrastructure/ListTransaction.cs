using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zapretiks.Infrastructure;

internal sealed record ListEdit(string Name, byte[]? Before, byte[]? After);
internal sealed record ListBackup(string Name, byte[]? Before, string BeforeHash, string AfterHash);
internal sealed record ListJournal(Guid Id, ListBackup[] Files);

// A prepared journal is flushed before the first write. Renaming it to last.json
// commits the operation. Undo is restartable at every individual file boundary.
internal sealed class ListTransaction(string dataDirectory, string root)
{
    internal const int MaxListBytes = 16 * 1024 * 1024;
    private const int MaxJournalBytes = 96 * 1024 * 1024;
    private const string Pending = "pending.json", Last = "last.json", Undoing = "undo.json";
    private readonly string directory = Path.Combine(dataDirectory, "list-backups",
        Hash(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())));

    internal string ListPath(string name) => Path.Combine(root, "lists", name);
    internal static string Hash(byte[]? bytes) => bytes is null ? "-" : Convert.ToHexString(SHA256.HashData(bytes));
    private string StatePath(string name) => Path.Combine(directory, name);

    internal FileStream Lock()
    {
        EnsurePlainPath(directory);
        Directory.CreateDirectory(directory);
        var path = StatePath("operation.lock");
        EnsurePlainPath(path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal (string? State, ListJournal? Journal) Load()
    {
        foreach (var name in new[] { Pending, Undoing, Last })
        {
            var bytes = Read(StatePath(name), MaxJournalBytes);
            if (bytes is null) continue;
            ListJournal? journal;
            try { journal = JsonSerializer.Deserialize<ListJournal>(bytes); }
            catch (JsonException e) { throw new InvalidDataException("Повреждена резервная копия списков.", e); }
            if (journal?.Files is not { Length: > 0 and <= 4 } || journal.Id == Guid.Empty ||
                journal.Files.Any(f => f is null || !FlowsealConfigurationPreview.FileNames.Contains(f.Name) ||
                    f.Before?.Length > MaxListBytes || Hash(f.Before) != f.BeforeHash ||
                    !(f.AfterHash == "-" || f.AfterHash is { Length: 64 } && f.AfterHash.All(char.IsAsciiHexDigit))) ||
                journal.Files.Select(f => f.Name).Distinct().Count() != journal.Files.Length)
                throw new InvalidDataException("Не удалось проверить резервную копию списков.");
            return (name, journal);
        }
        return (null, null);
    }

    internal static bool Interrupted(string? state) => state is Pending or Undoing;

    internal static bool CanRestore(string? state, ListJournal? journal, IReadOnlyDictionary<string, byte[]?> current)
    {
        if (journal is null) return false;
        return journal.Files.All(file =>
        {
            var hash = Hash(current[file.Name]);
            return hash == file.AfterHash || Interrupted(state) && hash == file.BeforeHash;
        });
    }

    internal void Commit(IEnumerable<ListEdit> edits)
    {
        var changes = edits.ToArray();
        var journal = new ListJournal(Guid.NewGuid(), changes.Select(change =>
            new ListBackup(change.Name, change.Before, Hash(change.Before), Hash(change.After))).ToArray());
        var pending = StatePath(Pending);
        ReplaceChecked(pending, null, JsonSerializer.SerializeToUtf8Bytes(journal), MaxJournalBytes);
        try
        {
            foreach (var change in changes)
                ReplaceChecked(ListPath(change.Name), change.Before, change.After);
            EnsurePlainPath(StatePath(Last));
            File.Move(pending, StatePath(Last), true);
        }
        catch
        {
            // Roll back only our writes. A conflicting external edit is preserved
            // and the journal remains available for explicit recovery.
            Restore(Pending, journal);
            throw;
        }
    }

    internal void Restore(string state, ListJournal journal)
    {
        var current = journal.Files.ToDictionary(file => file.Name, file => Read(ListPath(file.Name)));
        if (!CanRestore(state, journal, current))
            throw new InvalidOperationException("После сохранения копии списки изменились. Внешние изменения сохранены.");
        if (state == Last)
        {
            EnsurePlainPath(StatePath(Undoing));
            File.Move(StatePath(Last), StatePath(Undoing));
            state = Undoing;
        }
        foreach (var file in journal.Files.Reverse())
        {
            var before = current[file.Name];
            if (Hash(before) != file.BeforeHash)
                ReplaceChecked(ListPath(file.Name), before, file.Before);
        }
        File.Delete(StatePath(state));
    }

    internal static void EnsurePlainPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Путь содержит ссылку или точку подключения. Выберите обычную локальную папку.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static byte[]? Read(string path, int limit = MaxListBytes)
    {
        EnsurePlainPath(path);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > limit)
                throw new InvalidDataException($"Файл {Path.GetFileName(path)} превышает допустимый размер.");
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void ReplaceChecked(string path, byte[]? expected, byte[]? next, int limit = MaxListBytes)
    {
        if (next?.Length > limit)
            throw new InvalidDataException($"Файл {Path.GetFileName(path)} превысит допустимый размер.");
        if (Hash(Read(path, limit)) != Hash(expected))
            throw new InvalidOperationException("Файлы изменились. Проверьте новый предпросмотр.");
        var prefix = Path.Combine(Path.GetDirectoryName(path)!, $".zapretiks-{Guid.NewGuid():N}");
        var temp = prefix + ".tmp";
        var displaced = prefix + ".bak";
        try
        {
            if (next is not null)
            {
                using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(next);
                stream.Flush(flushToDisk: true);
            }
            EnsurePlainPath(path);
            if (expected is null)
            {
                if (next is not null) File.Move(temp, path);
                return;
            }
            if (next is null) File.Move(path, displaced);
            else File.Replace(temp, path, displaced);
            // An editor can race the precondition by replacing the directory entry.
            // Inspect the *actual* displaced file and return it if that happened.
            if (Hash(Read(displaced, limit)) != Hash(expected))
            {
                if (Hash(Read(path, limit)) != Hash(next))
                    throw new IOException($"Конфликт записи. Исходный файл сохранён в {displaced}.");
                if (next is null) File.Move(displaced, path);
                else File.Replace(displaced, path, null);
                throw new InvalidOperationException("Файл был изменён другой программой. Обновите предпросмотр.");
            }
            File.Delete(displaced);
        }
        finally { File.Delete(temp); }
    }
}
