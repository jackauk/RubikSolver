using System;

// The cube on the level of pieces, as in Kociemba's two-phase algorithm: which corner and edge
// sits at each position and how it is turned.
//
// Corners: URF, UFL, ULB, UBR, DFR, DLF, DBL, DRB.
// Edges:   UR, UF, UL, UB, DR, DF, DL, DB, FR, FL, BL, BR (the last four are the middle slice).
//
// A corner's orientation is which of its three stickers (in CornerStickers order) carries the U
// or D color; an edge's is 0 when its stickers match EdgeStickers order and 1 when flipped.
public sealed class CubieCube
{
    public const int Corners = 8, Edges = 12;

    // Sticker indices (see CubeState) of each corner and edge position, U/D sticker first.
    public static readonly int[][] CornerStickers =
    {
        new[] { 8, 9, 20 }, new[] { 6, 18, 38 }, new[] { 0, 36, 47 }, new[] { 2, 45, 11 },
        new[] { 29, 26, 15 }, new[] { 27, 44, 24 }, new[] { 33, 53, 42 }, new[] { 35, 17, 51 }
    };

    public static readonly int[][] EdgeStickers =
    {
        new[] { 5, 10 }, new[] { 7, 19 }, new[] { 3, 37 }, new[] { 1, 46 },
        new[] { 32, 16 }, new[] { 28, 25 }, new[] { 30, 43 }, new[] { 34, 52 },
        new[] { 23, 12 }, new[] { 21, 41 }, new[] { 50, 39 }, new[] { 48, 14 }
    };

    public static readonly string[] CornerNames = { "URF", "UFL", "ULB", "UBR", "DFR", "DLF", "DBL", "DRB" };
    public static readonly string[] EdgeNames = { "UR", "UF", "UL", "UB", "DR", "DF", "DL", "DB", "FR", "FL", "BL", "BR" };

    // Where the four slice edges are, ignoring their order: 495 = C(12, 4) values.
    static readonly int[] maskToSlice = new int[1 << Edges];
    static readonly int[] sliceToMask = new int[495];
    public static readonly int SolvedSlice;

    // A clockwise quarter turn of each face, read off the sticker model so both always agree.
    public static readonly CubieCube[] FaceMoves = new CubieCube[6];

    // Where a single piece goes under each move: [move][state] -> state, where the state of a
    // corner is position * 3 + twist and of an edge position * 2 + flip (24 states each).
    public const int PieceStates = 24;
    public static readonly int[][] CornerTurn = new int[CubeState.MoveCount][];
    public static readonly int[][] EdgeTurn = new int[CubeState.MoveCount][];

    public readonly int[] Cp = new int[Corners];
    public readonly int[] Co = new int[Corners];
    public readonly int[] Ep = new int[Edges];
    public readonly int[] Eo = new int[Edges];

    static CubieCube()
    {
        Array.Fill(maskToSlice, -1);
        int n = 0;
        for (int mask = 0; mask < 1 << Edges; mask++)
            if (System.Numerics.BitOperations.PopCount((uint)mask) == 4)
            {
                maskToSlice[mask] = n;
                sliceToMask[n++] = mask;
            }
        SolvedSlice = maskToSlice[0xF00];

        int[] solvedFaces = CubeState.Solved();
        for (int face = 0; face < 6; face++)
            FaceMoves[face] = FromFaces(CubeState.Apply(solvedFaces, face * 3), out _);

        for (int face = 0; face < 6; face++)
        {
            // After a turn q, slot i holds what was in slot q.Cp[i], turned q.Co[i] further.
            CubieCube q = FaceMoves[face];
            var corner = new int[PieceStates];
            var edge = new int[PieceStates];
            for (int i = 0; i < Corners; i++)
                for (int o = 0; o < 3; o++)
                    corner[q.Cp[i] * 3 + o] = i * 3 + (o + q.Co[i]) % 3;
            for (int i = 0; i < Edges; i++)
                for (int o = 0; o < 2; o++)
                    edge[q.Ep[i] * 2 + o] = i * 2 + (o + q.Eo[i]) % 2;
            int[] c = corner, e = edge;
            for (int k = 0; k < 3; k++)
            {
                CornerTurn[face * 3 + k] = c;
                EdgeTurn[face * 3 + k] = e;
                c = Array.ConvertAll(c, s => corner[s]);
                e = Array.ConvertAll(e, s => edge[s]);
            }
        }
    }

    public CubieCube()
    {
        for (int i = 0; i < Corners; i++)
            Cp[i] = i;
        for (int i = 0; i < Edges; i++)
            Ep[i] = i;
    }

    public CubieCube Clone()
    {
        var c = new CubieCube();
        Array.Copy(Cp, c.Cp, Corners);
        Array.Copy(Co, c.Co, Corners);
        Array.Copy(Ep, c.Ep, Edges);
        Array.Copy(Eo, c.Eo, Edges);
        return c;
    }

    // Builds the cube from stickers given as faces (0..5 = U R F D L B: which center each
    // sticker matches). Returns null with a reason when some piece cannot exist.
    public static CubieCube FromFaces(int[] faces, out string error)
    {
        error = null;
        var c = new CubieCube();
        for (int i = 0; i < Corners; i++)
        {
            int[] s = CornerStickers[i];
            int ori = 0;
            while (ori < 3 && faces[s[ori]] != CubeState.Up && faces[s[ori]] != CubeState.Down)
                ori++;
            int found = -1;
            if (ori < 3)
            {
                int c1 = faces[s[(ori + 1) % 3]], c2 = faces[s[(ori + 2) % 3]];
                for (int j = 0; j < Corners; j++)
                    if (c1 == CornerStickers[j][1] / 9 && c2 == CornerStickers[j][2] / 9
                        && faces[s[ori]] == CornerStickers[j][0] / 9)
                        found = j;
            }
            if (found < 0)
            {
                error = $"The corner at {CornerNames[i]} has a color combination no real corner has.";
                return null;
            }
            c.Cp[i] = found;
            c.Co[i] = ori;
        }

        for (int i = 0; i < Edges; i++)
        {
            int a = faces[EdgeStickers[i][0]], b = faces[EdgeStickers[i][1]];
            int found = -1;
            for (int j = 0; j < Edges; j++)
            {
                int ja = EdgeStickers[j][0] / 9, jb = EdgeStickers[j][1] / 9;
                if (a == ja && b == jb)
                {
                    found = j;
                    c.Eo[i] = 0;
                }
                else if (a == jb && b == ja)
                {
                    found = j;
                    c.Eo[i] = 1;
                }
            }
            if (found < 0)
            {
                error = $"The edge at {EdgeNames[i]} has a color combination no real edge has.";
                return null;
            }
            c.Ep[i] = found;
        }
        return c;
    }

    // Null when this is a position a real cube can reach, otherwise why not.
    public string Verify()
    {
        var seen = new bool[Edges];
        foreach (int e in Ep)
        {
            if (seen[e])
                return $"The {EdgeNames[e]} edge appears twice, so another edge is missing.";
            seen[e] = true;
        }
        seen = new bool[Corners];
        foreach (int corner in Cp)
        {
            if (seen[corner])
                return $"The {CornerNames[corner]} corner appears twice, so another corner is missing.";
            seen[corner] = true;
        }

        int flips = 0;
        foreach (int o in Eo)
            flips += o;
        if (flips % 2 != 0)
            return "One edge is flipped: its two stickers are the wrong way round.";

        int twists = 0;
        foreach (int o in Co)
            twists += o;
        if (twists % 3 != 0)
            return "One corner is twisted: its stickers are rotated the wrong way.";

        if (Parity(Ep) != Parity(Cp))
            return "Two pieces are swapped: check for two stickers entered the wrong way round.";
        return null;
    }

    // this = this * b: apply b (for example a face turn) after this.
    public void Multiply(CubieCube b)
    {
        Span<int> cp = stackalloc int[Corners];
        Span<int> co = stackalloc int[Corners];
        for (int i = 0; i < Corners; i++)
        {
            cp[i] = Cp[b.Cp[i]];
            co[i] = (Co[b.Cp[i]] + b.Co[i]) % 3;
        }
        Span<int> ep = stackalloc int[Edges];
        Span<int> eo = stackalloc int[Edges];
        for (int i = 0; i < Edges; i++)
        {
            ep[i] = Ep[b.Ep[i]];
            eo[i] = (Eo[b.Ep[i]] + b.Eo[i]) % 2;
        }
        cp.CopyTo(Cp);
        co.CopyTo(Co);
        ep.CopyTo(Ep);
        eo.CopyTo(Eo);
    }

    // The cube that undoes this one: this * Inverse() is solved.
    public CubieCube Inverse()
    {
        var inv = new CubieCube();
        for (int i = 0; i < Corners; i++)
        {
            inv.Cp[Cp[i]] = i;
            inv.Co[Cp[i]] = (3 - Co[i]) % 3;
        }
        for (int i = 0; i < Edges; i++)
        {
            inv.Ep[Ep[i]] = i;
            inv.Eo[Ep[i]] = Eo[i];
        }
        return inv;
    }

    public void ApplyMove(int move)
    {
        for (int k = 0; k <= move % 3; k++)
            Multiply(FaceMoves[move / 3]);
    }

    // Phase 1 coordinates: 0 in the subgroup <U, D, R2, F2, L2, B2> (SolvedSlice for Slice).

    public int Twist
    {
        get
        {
            int v = 0;
            for (int i = 0; i < Corners - 1; i++)
                v = v * 3 + Co[i];
            return v;
        }
        set
        {
            int sum = 0;
            for (int i = Corners - 2; i >= 0; i--)
            {
                Co[i] = value % 3;
                sum += Co[i];
                value /= 3;
            }
            Co[Corners - 1] = (3 - sum % 3) % 3;
        }
    }

    public int Flip
    {
        get
        {
            int v = 0;
            for (int i = 0; i < Edges - 1; i++)
                v = v * 2 + Eo[i];
            return v;
        }
        set
        {
            int sum = 0;
            for (int i = Edges - 2; i >= 0; i--)
            {
                Eo[i] = value % 2;
                sum += Eo[i];
                value /= 2;
            }
            Eo[Edges - 1] = sum % 2;
        }
    }

    public int Slice
    {
        get
        {
            int mask = 0;
            for (int i = 0; i < Edges; i++)
                if (Ep[i] >= 8)
                    mask |= 1 << i;
            return maskToSlice[mask];
        }
        set
        {
            int mask = sliceToMask[value];
            int slice = 8, other = 0;
            for (int i = 0; i < Edges; i++)
                Ep[i] = (mask & 1 << i) != 0 ? slice++ : other++;
        }
    }

    // Phase 2 coordinates, meaningful inside the subgroup; all 0 when solved.

    public int CornerPerm
    {
        get => Rank(Cp, 0, 8);
        set => Unrank(value, Cp, 0, 8, 0);
    }

    public int UDEdgePerm
    {
        get => Rank(Ep, 0, 8);
        set
        {
            Unrank(value, Ep, 0, 8, 0);
            for (int i = 8; i < Edges; i++)
                Ep[i] = i;
        }
    }

    public int SlicePerm
    {
        get => Rank(Ep, 8, 4);
        set
        {
            for (int i = 0; i < 8; i++)
                Ep[i] = i;
            Unrank(value, Ep, 8, 4, 8);
        }
    }

    // Lehmer code of a[offset .. offset + n) by relative order: 0 .. n! - 1.
    static int Rank(int[] a, int offset, int n)
    {
        int r = 0;
        for (int i = 0; i < n; i++)
        {
            int smaller = 0;
            for (int j = i + 1; j < n; j++)
                if (a[offset + j] < a[offset + i])
                    smaller++;
            r = r * (n - i) + smaller;
        }
        return r;
    }

    static void Unrank(int r, int[] a, int offset, int n, int baseValue)
    {
        Span<int> digits = stackalloc int[n];
        for (int i = n - 1; i >= 0; i--)
        {
            digits[i] = r % (n - i);
            r /= n - i;
        }
        Span<int> left = stackalloc int[n];
        for (int i = 0; i < n; i++)
            left[i] = i;
        int count = n;
        for (int i = 0; i < n; i++)
        {
            int d = digits[i];
            a[offset + i] = baseValue + left[d];
            for (int k = d; k < count - 1; k++)
                left[k] = left[k + 1];
            count--;
        }
    }

    static int Parity(int[] p)
    {
        int inversions = 0;
        for (int i = 0; i < p.Length; i++)
            for (int j = i + 1; j < p.Length; j++)
                if (p[j] < p[i])
                    inversions++;
        return inversions % 2;
    }
}
