using Godot;

namespace Solitude.Core;

public static class ColorExt
{
    /// <summary>
    /// Scale RGB, keep alpha. Use this instead of Color * float: vertex alpha is the
    /// emission mask in ps1_world, and Godot's operator also scales alpha.
    /// </summary>
    public static Color Shade(this Color c, float f) => new(c.R * f, c.G * f, c.B * f, c.A);
}
