using System.IO;
namespace Stellar.Application.Imaging;

/// <summary>Writes capture bytes; creates the folder and never overwrites (adds _N), even under a race.</summary>
internal sealed class CaptureFileSink
{
    public string Write(string directory, string stem, string ext, byte[] data)
    {
        Directory.CreateDirectory(directory);
        for (var n = 0; ; n++)
        {
            var path = Path.Combine(directory, n == 0 ? stem + ext : $"{stem}_{n}{ext}");
            try
            {
                using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                fs.Write(data, 0, data.Length);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another writer claimed this name between our check and our create; try the next suffix.
            }
        }
    }
}
