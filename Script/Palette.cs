using Godot;

// The six sticker colors. Index i is the usual color of face i's center (U white, R red,
// F green, D yellow, L orange, B blue), so the solved cube is simply color = face.
public static class Palette
{
    public static readonly Color[] Colors =
    {
        new(0.95f, 0.95f, 0.93f), // white
        new(0.78f, 0.07f, 0.09f), // red
        new(0.02f, 0.60f, 0.27f), // green
        new(1.00f, 0.84f, 0.05f), // yellow
        new(1.00f, 0.45f, 0.02f), // orange
        new(0.05f, 0.30f, 0.85f)  // blue
    };

    public static readonly string[] Names = { "white", "red", "green", "yellow", "orange", "blue" };

    public static readonly Color BlankColor = new(0.30f, 0.31f, 0.34f);

    public static Color Get(int color) => color < 0 ? BlankColor : Colors[color];

    // "Any color" (CubeState.Any), for target stickers: a rainbow, so it reads as no one color.
    static Texture2D rainbow;
    public static Texture2D Rainbow => rainbow ??= new GradientTexture2D
    {
        Width = 64,
        Height = 64,
        FillFrom = Vector2.Zero,
        FillTo = Vector2.One,
        Gradient = new Gradient
        {
            Offsets = new[] { 0f, 0.2f, 0.4f, 0.6f, 0.8f, 1f },
            Colors = new[]
            {
                new Color(0.95f, 0.2f, 0.25f), new Color(1f, 0.6f, 0.1f), new Color(1f, 0.92f, 0.2f),
                new Color(0.2f, 0.8f, 0.35f), new Color(0.2f, 0.5f, 1f), new Color(0.65f, 0.3f, 0.95f)
            }
        }
    };

    // Dark text on the light stickers, light text on the dark ones.
    public static Color TextOn(int color) => color is 0 or 3 or 4 ? new Color(0.1f, 0.1f, 0.1f) : Colors[0];
}
