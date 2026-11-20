using System.Text;

namespace Hoardling.Core;

public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly int[] RetryDelaysMs = [50, 150, 400];

    /// <summary>
    /// Writes a unique sibling temp file, flushes it to disk and swaps it in. <see cref="File.Replace(string, string, string?)"/>
    /// keeps the target's ACLs. Sharing violations (antivirus, another reader) are retried briefly.
    /// </summary>
    public static void WriteAllText(string path, string text)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var temp = $"{full}.{Guid.NewGuid():N}.hoardling.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(full))
                    {
                        File.Replace(temp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(temp, full);
                    }
                    return;
                }
                catch (IOException) when (attempt < RetryDelaysMs.Length && File.Exists(temp))
                {
                    Thread.Sleep(RetryDelaysMs[attempt]);
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: never hide the original error.
            }
        }
    }
}
