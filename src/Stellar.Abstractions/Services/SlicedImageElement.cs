using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// A plugin picture drawn as a horizontal 3-slice frame (framework 2.34.0) — e.g. boss-bar frame art that follows a
/// width-resizable window. Three PNGs: the <paramref name="Left"/> and <paramref name="Right"/> caps keep their native
/// pixel width, the <paramref name="Middle"/> piece is stretched to fill the rest (give it identical columns, so a
/// stretch is lossless). The element fills the width its parent hands it (min width = both caps) at a fixed
/// <paramref name="Height"/>.
/// <para>Crisp: the pieces load with no mip chain and bilinear filtering, so at UI scale 1 every texel lands on one
/// pixel. Each Func is read ONCE when the window is built — return cached arrays (load the PNG bytes once).</para>
/// <para>A missing / undecodable piece is simply not drawn (the slot keeps its size).</para>
/// </summary>
/// <param name="Left">Left cap PNG (drawn at its native width).</param>
/// <param name="Middle">Middle PNG, stretched horizontally between the caps.</param>
/// <param name="Right">Right cap PNG (drawn at its native width).</param>
/// <param name="Height">Frame height in px (also the drawn height of every piece).</param>
public sealed record SlicedImageElement(Func<byte[]?> Left, Func<byte[]?> Middle, Func<byte[]?> Right, int Height) : HudElement;
