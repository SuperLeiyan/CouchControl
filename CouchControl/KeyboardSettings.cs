namespace CouchControl;

/// <summary>CouchControl's on-screen keyboard: size and position, saved when you move or resize it.</summary>
public sealed class KeyboardSettings
{
    /// <summary>Width in pixels; 0 = default (about 60% of the screen).</summary>
    public int Width { get; set; }

    /// <summary>Height in pixels; 0 = matches the width's default shape.</summary>
    public int Height { get; set; }

    /// <summary>Window opacity, 0.3 (see-through) to 1 (solid).</summary>
    public double Opacity { get; set; } = 0.96;

    /// <summary>Screen position of the top-left corner; null = docked at the bottom centre.</summary>
    public int? Left { get; set; }
    public int? Top { get; set; }
}
