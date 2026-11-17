using System;
using System.Collections.Generic;
using Godot;

// The 54 stickers of a 3x3 cube, stored as palette colors (-1 for a blank sticker) and indexed
// face by face in the order U, R, F, D, L, B - the order Kociemba's solver uses. Each face is
// read row by row as it looks in the unfolded net:
//
//                 U0 U1 U2
//                 U3 U4 U5
//                 U6 U7 U8
//     L0 L1 L2    F0 F1 F2    R0 R1 R2    B0 B1 B2
//     L3 L4 L5    F3 F4 F5    R3 R4 R5    B3 B4 B5
//     L6 L7 L8    F6 F7 F8    R6 R7 R8    B6 B7 B8
//                 D0 D1 D2
//                 D3 D4 D5
//                 D6 D7 D8
//
// x points right, y up and z towards the viewer, so the front face is at z = +1. Moves are
// numbered face * 3 + (quarter turns - 1): 0 = U, 1 = U2, 2 = U', 3 = R, ... 17 = B'. A turn is
// clockwise as seen looking straight at that face.
public static class CubeState
{
    public const int Up = 0, Right = 1, Front = 2, Down = 3, Left = 4, Back = 5;
    public const int Blank = -1;
    public const int MoveCount = 18;

    public const string FaceLetters = "URFDLB";
    public static readonly string[] FaceNames = { "Up", "Right", "Front", "Down", "Left", "Back" };

    public static readonly Vector3I[] Normals =
    {
        new(0, 1, 0), new(1, 0, 0), new(0, 0, 1), new(0, -1, 0), new(-1, 0, 0), new(0, 0, -1)
    };

    // The cubie each sticker sits on, in grid coordinates -1..1.
    public static readonly Vector3I[] Positions = new Vector3I[54];

    static readonly Dictionary<int, int> stickerAt = new();
    static readonly int[][] moveTargets = new int[MoveCount][]; // [move][sticker] -> where it goes

    static CubeState()
    {
        for (int i = 0; i < 54; i++)
        {
            Positions[i] = StickerPosition(i / 9, i % 9 / 3, i % 3);
            stickerAt[Key(Positions[i], i / 9)] = i;
        }

        for (int m = 0; m < MoveCount; m++)
        {
            int face = m / 3;
            int axis = Axis(face);
            int sign = Sign(face);
            // A clockwise quarter turn is -90 degrees about the outward normal, which for the
            // D, L and B faces is three -90 degree steps about the positive axis.
            int steps = (m % 3 + 1) * (sign > 0 ? 1 : 3) % 4;
            var targets = new int[54];
            for (int i = 0; i < 54; i++)
            {
                Vector3I pos = Positions[i];
                if (Component(pos, axis) != sign)
                {
                    targets[i] = i;
                    continue;
                }
                Vector3I normal = Normals[i / 9];
                for (int s = 0; s < steps; s++)
                {
                    pos = QuarterTurn(pos, axis);
                    normal = QuarterTurn(normal, axis);
                }
                targets[i] = StickerAt(pos, Array.IndexOf(Normals, normal));
            }
            moveTargets[m] = targets;
        }
    }

    public static int[] Solved()
    {
        var colors = new int[54];
        for (int i = 0; i < 54; i++)
            colors[i] = i / 9;
        return colors;
    }

    public static int[] Apply(int[] colors, int move)
    {
        var result = new int[54];
        int[] targets = moveTargets[move];
        for (int i = 0; i < 54; i++)
            result[targets[i]] = colors[i];
        return result;
    }

    public static int[] Apply(int[] colors, IEnumerable<int> moves)
    {
        foreach (int m in moves)
            colors = Apply(colors, m);
        return colors;
    }

    public static bool IsSolved(int[] colors)
    {
        for (int i = 0; i < 54; i++)
            if (colors[i] != colors[i / 9 * 9 + 4])
                return false;
        return true;
    }

    public static int Inverse(int move) => move / 3 * 3 + 2 - move % 3;

    public static string MoveName(int move) => FaceLetters[move / 3] + (move % 3) switch
    {
        0 => "",
        1 => "2",
        _ => "'"
    };

    // 0 = x, 1 = y, 2 = z
    public static int Axis(int face) => (face % 3) switch { 0 => 1, 1 => 0, _ => 2 };

    public static int Sign(int face) => face < 3 ? 1 : -1;

    public static int Component(Vector3I v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    // The sticker on the given face of the cubie at pos, or -1 if that cubie has no such sticker.
    public static int StickerAt(Vector3I pos, int face) =>
        stickerAt.TryGetValue(Key(pos, face), out int i) ? i : -1;

    static int Key(Vector3I pos, int face) => (((pos.X + 1) * 3 + pos.Y + 1) * 3 + pos.Z + 1) * 6 + face;

    static Vector3I StickerPosition(int face, int row, int col) => face switch
    {
        Up => new(col - 1, 1, row - 1),
        Right => new(1, 1 - row, 1 - col),
        Front => new(col - 1, 1 - row, 1),
        Down => new(col - 1, -1, 1 - row),
        Left => new(-1, 1 - row, col - 1),
        _ => new(1 - col, 1 - row, -1)
    };

    // -90 degrees about the positive axis.
    static Vector3I QuarterTurn(Vector3I v, int axis) => axis switch
    {
        0 => new(v.X, v.Z, -v.Y),
        1 => new(-v.Z, v.Y, v.X),
        _ => new(v.Y, -v.X, v.Z)
    };
}
