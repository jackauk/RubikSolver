using Godot;

// Circles the cube at its center. Dragging turns the view, the wheel zooms. The picture is
// shifted so the cube sits in the part of the screen the panels leave free.
public partial class OrbitCamera : Camera3D
{
    [Export] public float Distance = 10.5f;
    [Export] public float Yaw = 35f;   // degrees; 0 looks straight at the front face
    [Export] public float Pitch = 28f; // degrees above the cube
    [Export] public float MinDistance = 6f;
    [Export] public float MaxDistance = 20f;
    [Export] public float DegreesPerPixel = 0.35f;
    [Export] public Vector2 ScreenShift = new(-0.155f, -0.1f); // per unit of distance

    float yaw, pitch, distance;

    public override void _Ready()
    {
        yaw = Yaw;
        pitch = Pitch;
        distance = Distance;
        Place();
    }

    public void Orbit(Vector2 pixels)
    {
        Yaw -= pixels.X * DegreesPerPixel;
        Pitch = Mathf.Clamp(Pitch + pixels.Y * DegreesPerPixel, -85f, 85f);
    }

    public void Zoom(float factor) => Distance = Mathf.Clamp(Distance * factor, MinDistance, MaxDistance);

    public void ResetView()
    {
        Yaw = 35f;
        Pitch = 28f;
    }

    public override void _Process(double delta)
    {
        float weight = 1f - Mathf.Exp(-14f * (float)delta);
        yaw = Mathf.Lerp(yaw, Yaw, weight);
        pitch = Mathf.Lerp(pitch, Pitch, weight);
        distance = Mathf.Lerp(distance, Distance, weight);
        Place();
    }

    void Place()
    {
        float y = Mathf.DegToRad(yaw), p = Mathf.DegToRad(pitch);
        Position = new Vector3(Mathf.Sin(y) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Cos(y) * Mathf.Cos(p)) * distance;
        LookAt(Vector3.Zero, Vector3.Up);
        HOffset = ScreenShift.X * distance;
        VOffset = ScreenShift.Y * distance;
    }
}
