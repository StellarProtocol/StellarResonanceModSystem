using System;
using System.IO;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Theme;

internal sealed class EmbeddedAssetProvider : IThemeAssetProvider
{
    private const string FontResourceName = "Stellar.Infrastructure.Resources.NotoSans-Regular.ttf";

    private readonly Assembly _assembly = typeof(EmbeddedAssetProvider).Assembly;
    private byte[]? _fontBytesCache;

    public byte[] LoadFontBytes()
    {
        if (_fontBytesCache is not null) return _fontBytesCache;

        using var stream = _assembly.GetManifestResourceStream(FontResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{FontResourceName}' not found. Check that " +
                $"Stellar.Infrastructure.csproj has the EmbeddedResource entry " +
                $"and that NotoSans-Regular.ttf exists in Resources/.");
        }

        var buffer = new byte[stream.Length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        _fontBytesCache = buffer;
        return _fontBytesCache;
    }

    public byte[] MakeSolidTexturePixels(ColorRgba colour)
    {
        // 1x1 RGBA32 pixel — ThemeRenderer turns this into a Texture2D for use
        // as a GUIStyle background or bar fill.
        var r = (byte)System.Math.Clamp(colour.R * 255f, 0f, 255f);
        var g = (byte)System.Math.Clamp(colour.G * 255f, 0f, 255f);
        var b = (byte)System.Math.Clamp(colour.B * 255f, 0f, 255f);
        var a = (byte)System.Math.Clamp(colour.A * 255f, 0f, 255f);
        return new[] { r, g, b, a };
    }
}
