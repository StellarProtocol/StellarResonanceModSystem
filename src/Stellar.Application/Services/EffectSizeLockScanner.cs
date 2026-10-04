using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Stellar.Application.Services;

/// <summary>
/// Pure text scan deciding whether a ReShade effect (.fx) source is <b>size-locked</b>: it declares a texture whose
/// <c>Width</c> or <c>Height</c> is computed from the screen size. No I/O.
/// <para>Why it matters (ReShade 6.8.0): every render-target size gets its own compiled permutation of an effect, with
/// <c>BUFFER_WIDTH</c>/<c>BUFFER_HEIGHT</c> set to that size, but named textures live in ONE global list. A texture
/// the screen-size permutation already created is reused by the other permutation even though its declared size differs
/// — ReShade only logs a warning (source/runtime.cpp:2035-2084, create_effect 2211-2219) — while each pass's viewport
/// comes from the declared size (effect_parser_stmt.cpp:2336-2337, runtime.cpp:4233-4246). Every pass that samples
/// such a texture by <c>uv</c> then reads its top-left 1/scale region magnified (AcerolaFX Draft at 4× came out as a
/// 256×-magnified hatch). Fixed-size textures match in every permutation and the <c>: COLOR</c> / <c>: DEPTH</c>
/// semantic textures are the runtime's own, so neither counts.</para>
/// <para>A <c>Width</c>/<c>Height</c> expression counts as screen-sized when it names a screen token
/// (<see cref="ScreenTokens"/>) directly or through any <c>#define</c> or <c>const</c> it references, followed through
/// every definition of that name (preprocessor conditionals are ignored: all branches are ORed, like the depth scan).
/// Comments are stripped first.</para>
/// </summary>
internal static class EffectSizeLockScanner
{
    /// <summary>The screen-size names an effect can size a texture from: the compiler's BUFFER_* macros, the standard
    /// header's derived macros (ReShade.fxh: BUFFER_SCREEN_SIZE, BUFFER_PIXEL_SIZE, BUFFER_ASPECT_RATIO) and its
    /// <c>ReShade::</c> constants/functions (ScreenSize, PixelSize, AspectRatio). The flattener skips ReShade.fxh, so
    /// these are matched by name rather than by following its definitions.</summary>
    private static readonly HashSet<string> ScreenTokens = new(StringComparer.Ordinal)
    {
        "BUFFER_WIDTH", "BUFFER_HEIGHT", "BUFFER_RCP_WIDTH", "BUFFER_RCP_HEIGHT",
        "BUFFER_SCREEN_SIZE", "BUFFER_PIXEL_SIZE", "BUFFER_ASPECT_RATIO",
        "ScreenSize", "PixelSize", "AspectRatio", "GetScreenSize", "GetPixelSize", "GetAspectRatio",
    };

    // texture[1D|2D|3D] Name [: SEMANTIC] [< annotations >] { body }
    private static readonly Regex TextureDeclaration = new(
        @"\btexture(?:[123]D)?\s+\w+\s*(?<semantic>:\s*\w+\s*)?(?:<[^>]*>\s*)?\{(?<body>[^}]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SizeField = new(@"\b(?:Width|Height)\s*=\s*(?<expr>[^;]+);", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Define = new(@"^[ \t]*#[ \t]*define[ \t]+(?<name>\w+)(?:\([^)\r\n]*\))?(?<body>[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex Constant = new(@"\bconst\s+\w+\s+(?<name>\w+)\s*(?:\[[^\]]*\]\s*)?=\s*(?<body>[^;]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LineContinuation = new(@"\\[ \t]*\r?\n", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new(@"[A-Za-z_]\w*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="fxSource"/> (comments stripped; includes already flattened by the caller)
    /// declares a non-semantic texture whose Width or Height depends on the screen size. False for null — a caller that
    /// could not READ the source must decide for itself (it should assume locked).</summary>
    internal static bool IsSizeLocked(string? fxSource)
    {
        if (fxSource is null) return false;
        var source = LineContinuation.Replace(EffectSourceText.WithoutComments(fxSource), " ");
        Dictionary<string, List<string>>? definitions = null;
        foreach (Match texture in TextureDeclaration.Matches(source))
        {
            if (texture.Groups["semantic"].Success) continue;   // : COLOR / : DEPTH — the runtime's own, never a size of ours
            foreach (Match field in SizeField.Matches(texture.Groups["body"].Value))
            {
                definitions ??= CollectDefinitions(source);
                if (ReferencesScreen(field.Groups["expr"].Value, definitions, new HashSet<string>(StringComparer.Ordinal)))
                    return true;
            }
        }
        return false;
    }

    private static Dictionary<string, List<string>> CollectDefinitions(string source)
    {
        var definitions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Match m in Define.Matches(source)) Add(definitions, m.Groups["name"].Value, m.Groups["body"].Value);
        foreach (Match m in Constant.Matches(source)) Add(definitions, m.Groups["name"].Value, m.Groups["body"].Value);
        return definitions;
    }

    private static void Add(Dictionary<string, List<string>> definitions, string name, string body)
    {
        if (!definitions.TryGetValue(name, out var bodies)) definitions[name] = bodies = new List<string>();
        bodies.Add(body);
    }

    // Each name is expanded at most once per texture field (visited), so mutually recursive macros terminate.
    private static bool ReferencesScreen(string expression, Dictionary<string, List<string>> definitions, HashSet<string> visited)
    {
        foreach (Match token in Identifier.Matches(expression))
        {
            var name = token.Value;
            if (ScreenTokens.Contains(name)) return true;
            if (!visited.Add(name) || !definitions.TryGetValue(name, out var bodies)) continue;
            foreach (var body in bodies)
            {
                if (ReferencesScreen(body, definitions, visited)) return true;
            }
        }
        return false;
    }
}
