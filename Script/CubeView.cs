using System;
using System.Collections.Generic;
using Godot;

// Draws the cube as 26 cubies with colored stickers, animates face turns and finds the sticker
// under the mouse. It only shows colors it is given; the cube's state lives in Main.
//
// The cubies never really move: a turn rotates the nine cubies of one layer about the face's
// axis and, when the turn is complete, puts them back and shows the new colors.
public partial class CubeView : Node3D
{
    const float CubieSize = 0.95f;
    const float StickerSize = 0.82f;
    const float StickerThickness = 0.02f;
    const float PickExtent = 1.5f;

    static readonly Color HighlightColor = new(1f, 0.85f, 0.2f);
    static readonly Color LabelColor = new(0.8f, 0.82f, 0.88f);

    public event Action MoveFinished;

    readonly Dictionary<Vector3I, Node3D> cubies = new();
    readonly MeshInstance3D[] stickers = new MeshInstance3D[54];
    readonly StandardMaterial3D[] materials = new StandardMaterial3D[7]; // palette, then blank
    readonly Label3D[] faceLabels = new Label3D[6];

    readonly List<(Node3D node, Vector3 home)> turning = new();
    Vector3 turnAxis;
    float turnAngle;
    float turnTime;
    float turnDuration;
    int[] colorsAfterTurn;

    public bool IsAnimating { get; private set; }

    public override void _Ready()
    {
        for (int i = 0; i < 6; i++)
            materials[i] = new StandardMaterial3D { AlbedoColor = Palette.Colors[i], Roughness = 0.35f };
        materials[6] = new StandardMaterial3D { AlbedoColor = Palette.BlankColor, Roughness = 0.6f };

        var bodyMesh = new BoxMesh { Size = Vector3.One * CubieSize };
        var bodyMaterial = new StandardMaterial3D { AlbedoColor = new Color(0.04f, 0.04f, 0.05f), Roughness = 0.5f };
        for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && y == 0 && z == 0)
                        continue;
                    var pos = new Vector3I(x, y, z);
                    var cubie = new Node3D { Name = $"Cubie{x + 1}{y + 1}{z + 1}", Position = ToVector(pos) };
                    cubie.AddChild(new MeshInstance3D { Mesh = bodyMesh, MaterialOverride = bodyMaterial });
                    AddChild(cubie);
                    cubies[pos] = cubie;
                }

        var stickerMeshes = new BoxMesh[3];
        for (int axis = 0; axis < 3; axis++)
        {
            var size = Vector3.One * StickerSize;
            size[axis] = StickerThickness;
            stickerMeshes[axis] = new BoxMesh { Size = size };
        }
        for (int i = 0; i < 54; i++)
        {
            int face = i / 9;
            var sticker = new MeshInstance3D
            {
                Mesh = stickerMeshes[CubeState.Axis(face)],
                Position = ToVector(CubeState.Normals[face]) * (CubieSize + StickerThickness) * 0.5f
            };
            cubies[CubeState.Positions[i]].AddChild(sticker);
            stickers[i] = sticker;
        }

        for (int face = 0; face < 6; face++)
        {
            var label = new Label3D
            {
                Text = CubeState.FaceLetters[face].ToString(),
                FontSize = 64,
                OutlineSize = 12,
                PixelSize = 0.006f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Position = ToVector(CubeState.Normals[face]) * 2.15f,
                Modulate = LabelColor,
                OutlineModulate = new Color(0f, 0f, 0f, 0.8f)
            };
            AddChild(label);
            faceLabels[face] = label;
        }

        SetColors(CubeState.Solved());
    }

    public void SetColors(int[] colors)
    {
        for (int i = 0; i < 54; i++)
            stickers[i].MaterialOverride = materials[colors[i] < 0 ? 6 : colors[i]];
    }

    // Marks the face label of the next move to make, or none for -1.
    public void HighlightFace(int face)
    {
        for (int f = 0; f < 6; f++)
        {
            faceLabels[f].Modulate = f == face ? HighlightColor : LabelColor;
            faceLabels[f].FontSize = f == face ? 96 : 64;
        }
    }

    // Turns a layer, then shows colorsAfter and raises MoveFinished.
    public void AnimateMove(int move, int[] colorsAfter, float duration)
    {
        FinishTurn(false);
        int face = move / 3;
        int axis = CubeState.Axis(face);
        int sign = CubeState.Sign(face);
        int quarters = (move % 3) switch { 0 => 1, 1 => 2, _ => -1 };

        turnAxis = Vector3.Zero;
        turnAxis[axis] = 1f;
        turnAngle = -sign * quarters * Mathf.Pi / 2f; // clockwise is negative about the outward normal
        turnTime = 0f;
        turnDuration = Mathf.Max(duration, 0.001f) * (quarters == 2 ? 1.4f : 1f);
        colorsAfterTurn = (int[])colorsAfter.Clone();
        foreach (var (pos, cubie) in cubies)
            if (CubeState.Component(pos, axis) == sign)
                turning.Add((cubie, ToVector(pos)));
        IsAnimating = true;
    }

    public override void _Process(double delta)
    {
        if (!IsAnimating)
            return;
        turnTime += (float)delta;
        float t = Mathf.Min(turnTime / turnDuration, 1f);
        float eased = t * t * (3f - 2f * t);
        var rotation = new Basis(turnAxis, turnAngle * eased);
        foreach (var (node, home) in turning)
            node.Transform = new Transform3D(rotation, rotation * home);
        if (t >= 1f)
            FinishTurn(true);
    }

    void FinishTurn(bool notify)
    {
        if (!IsAnimating)
            return;
        foreach (var (node, home) in turning)
            node.Transform = new Transform3D(Basis.Identity, home);
        turning.Clear();
        SetColors(colorsAfterTurn);
        IsAnimating = false;
        if (notify)
            MoveFinished?.Invoke();
    }

    // The sticker under a screen position, or -1.
    public int PickSticker(Camera3D camera, Vector2 screenPos)
    {
        Transform3D toLocal = GlobalTransform.AffineInverse();
        Vector3 from = toLocal * camera.ProjectRayOrigin(screenPos);
        Vector3 dir = toLocal.Basis * camera.ProjectRayNormal(screenPos);

        // Ray against the cube's bounding box, slab by slab.
        float near = float.NegativeInfinity, far = float.PositiveInfinity;
        int hitAxis = -1;
        for (int i = 0; i < 3; i++)
        {
            if (Mathf.Abs(dir[i]) < 1e-6f)
            {
                if (Mathf.Abs(from[i]) > PickExtent)
                    return -1;
                continue;
            }
            float t1 = (-PickExtent - from[i]) / dir[i];
            float t2 = (PickExtent - from[i]) / dir[i];
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            if (t1 > near)
            {
                near = t1;
                hitAxis = i;
            }
            far = Mathf.Min(far, t2);
        }
        if (hitAxis < 0 || far < near || far < 0f)
            return -1;

        Vector3 hit = from + dir * near;
        int sign = hit[hitAxis] > 0f ? 1 : -1;
        var cell = new Vector3I(
            Mathf.RoundToInt(Mathf.Clamp(hit.X, -1.4f, 1.4f)),
            Mathf.RoundToInt(Mathf.Clamp(hit.Y, -1.4f, 1.4f)),
            Mathf.RoundToInt(Mathf.Clamp(hit.Z, -1.4f, 1.4f)));
        cell[hitAxis] = sign;
        var normal = Vector3I.Zero;
        normal[hitAxis] = sign;
        return CubeState.StickerAt(cell, Array.IndexOf(CubeState.Normals, normal));
    }

    static Vector3 ToVector(Vector3I v) => new(v.X, v.Y, v.Z);
}
