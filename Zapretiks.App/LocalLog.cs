using System.Diagnostics;
using System.IO;

namespace Zapretiks.App;

internal sealed class LocalLog(string directory)
{
    public void Write(Exception error)
    {
        Trace.TraceError(error.ToString());
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "zapretiks.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                File.Move(path, Path.Combine(directory, "zapretiks.previous.log"), overwrite: true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {error}{Environment.NewLine}");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning(logError.Message);
        }
    }
}
