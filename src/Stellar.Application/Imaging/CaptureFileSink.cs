using System.IO;
namespace Stellar.Application.Imaging;

/// <summary>Writes capture bytes; creates the folder and never overwrites (adds _N).</summary>
internal sealed class CaptureFileSink
{
    public string Write(string directory, string stem, string ext, byte[] data)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, stem + ext);
        for (var n = 1; File.Exists(path); n++) path = Path.Combine(directory, $"{stem}_{n}{ext}");
        File.WriteAllBytes(path, data);
        return path;
    }
}
