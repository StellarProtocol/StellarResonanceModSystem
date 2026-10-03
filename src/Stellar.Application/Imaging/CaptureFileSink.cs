using System;
using System.IO;
namespace Stellar.Application.Imaging;

/// <summary>Writes capture files; creates the folder and never overwrites (adds _N), even under a race.</summary>
internal sealed class CaptureFileSink
{
    private const int StreamBufferSize = 64 * 1024;

    /// <summary>Writes <paramref name="data"/> to a fresh unique path; returns the path.</summary>
    public string Write(string directory, string stem, string ext, byte[] data) =>
        WriteNew(directory, stem, ext, s => s.Write(data, 0, data.Length));

    /// <summary>
    /// Claims a fresh unique path (<see cref="FileMode.CreateNew"/>, never an existing file) and streams into it via
    /// <paramref name="write"/>; returns the path. When <paramref name="write"/> throws, the half-written file THIS
    /// call created is deleted and the exception rethrown — nothing else is ever touched.
    /// </summary>
    public string WriteNew(string directory, string stem, string ext, Action<Stream> write)
    {
        Directory.CreateDirectory(directory);
        for (var n = 0; ; n++)
        {
            var path = Path.Combine(directory, n == 0 ? stem + ext : $"{stem}_{n}{ext}");
            FileStream fs;
            try
            {
                fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize);
            }
            catch (IOException) when (File.Exists(path))
            {
                continue;   // another writer claimed this name between our check and our create; try the next suffix
            }
            try
            {
                using (fs) write(fs);
                return path;
            }
            catch
            {
                TryDelete(path);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort — the original failure is what the caller reports */ }
    }
}
