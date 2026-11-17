using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

// Kociemba's two-phase algorithm. Phase 1 brings the cube into the subgroup
// G1 = <U, D, R2, F2, L2, B2> (corners and edges oriented, middle-slice edges in the middle
// slice); phase 2 solves it using only those moves. Both phases are IDA* searches guided by
// pruning tables. Every phase 1 solution is completed by the shortest phase 2, and the search
// keeps looking for shorter totals until no shorter one can exist, or for a while: QuickMillis
// once it has one of TargetLength moves or fewer, ImproveMillis otherwise.
//
// Init() builds the tables (a second or two) and must have finished before Solve() is called.
// Solve() keeps its state in its own search object, so several solves may run at once.
public static class TwoPhaseSolver
{
    public const int TargetLength = 20;    // good enough once QuickMillis have passed
    public const int QuickMillis = 250;    // nearly solved cubes find their short solutions by then
    public const int ImproveMillis = 1500; // keep improving the first solution for this long
    public const int GiveUpMillis = 20000;

    const int NTwist = 2187, NFlip = 2048, NSlice = 495;
    const int NCornerPerm = 40320, NUDEdgePerm = 40320, NSlicePerm = 24;
    const int N = CubeState.MoveCount;

    static readonly int[] AllMoves = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 };
    static readonly int[] Phase2Moves = { 0, 1, 2, 4, 7, 9, 10, 11, 13, 16 }; // U*, R2, F2, D*, L2, B2
    static readonly bool[] isPhase2Move = new bool[N];

    static int[] twistMove, flipMove, sliceMove;
    static int[] cornerMove, udEdgeMove, slicePermMove;
    static byte[] sliceTwistPrune, sliceFlipPrune;
    static byte[] cornerSlicePrune, edgeSlicePrune;

    static readonly object initLock = new();
    static volatile bool ready;

    public static bool Ready => ready;

    public static void Init()
    {
        lock (initLock)
        {
            if (ready)
                return;
            foreach (int m in Phase2Moves)
                isPhase2Move[m] = true;

            twistMove = MoveTable(NTwist, AllMoves, (c, v) => c.Twist = v, c => c.Twist);
            flipMove = MoveTable(NFlip, AllMoves, (c, v) => c.Flip = v, c => c.Flip);
            sliceMove = MoveTable(NSlice, AllMoves, (c, v) => c.Slice = v, c => c.Slice);
            cornerMove = MoveTable(NCornerPerm, Phase2Moves, (c, v) => c.CornerPerm = v, c => c.CornerPerm);
            udEdgeMove = MoveTable(NUDEdgePerm, Phase2Moves, (c, v) => c.UDEdgePerm = v, c => c.UDEdgePerm);
            slicePermMove = MoveTable(NSlicePerm, Phase2Moves, (c, v) => c.SlicePerm = v, c => c.SlicePerm);

            sliceTwistPrune = PruneTable(sliceMove, NSlice, twistMove, NTwist, AllMoves, CubieCube.SolvedSlice);
            sliceFlipPrune = PruneTable(sliceMove, NSlice, flipMove, NFlip, AllMoves, CubieCube.SolvedSlice);
            cornerSlicePrune = PruneTable(cornerMove, NCornerPerm, slicePermMove, NSlicePerm, Phase2Moves, 0);
            edgeSlicePrune = PruneTable(udEdgeMove, NUDEdgePerm, slicePermMove, NSlicePerm, Phase2Moves, 0);
            ready = true;
        }
    }

    // The moves (see CubeState) that solve the cube, or null if none was found in time.
    public static int[] Solve(CubieCube cube, CancellationToken cancel = default)
    {
        Init();
        return new Search(cube, cancel).Run();
    }

    // table[value * 18 + move] = coordinate after the move; -1 for moves the table does not cover.
    static int[] MoveTable(int size, int[] moves, Action<CubieCube, int> set, Func<CubieCube, int> get)
    {
        var covered = new bool[N];
        foreach (int m in moves)
            covered[m] = true;
        var table = new int[size * N];
        for (int v = 0; v < size; v++)
        {
            var c = new CubieCube();
            set(c, v);
            for (int face = 0; face < 6; face++)
            {
                CubieCube turn = CubieCube.FaceMoves[face];
                for (int k = 0; k < 3; k++)
                {
                    c.Multiply(turn);
                    int m = face * 3 + k;
                    table[v * N + m] = covered[m] ? get(c) : -1;
                }
                c.Multiply(turn); // the fourth turn restores the cube
            }
        }
        return table;
    }

    // Moves needed to bring the pair (a, b) to (solvedA, 0), found breadth first.
    static byte[] PruneTable(int[] moveA, int sizeA, int[] moveB, int sizeB, int[] moves, int solvedA)
    {
        var table = new byte[sizeA * sizeB];
        Array.Fill(table, byte.MaxValue);
        int start = solvedA * sizeB;
        table[start] = 0;
        var frontier = new List<int> { start };
        for (byte depth = 0; frontier.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (int index in frontier)
            {
                int a = index / sizeB, b = index % sizeB;
                foreach (int m in moves)
                {
                    int ni = moveA[a * N + m] * sizeB + moveB[b * N + m];
                    if (table[ni] == byte.MaxValue)
                    {
                        table[ni] = (byte)(depth + 1);
                        next.Add(ni);
                    }
                }
            }
            frontier = next;
        }
        return table;
    }

    // Skips turning the same face twice in a row, and fixes the order of two opposite faces
    // (D after U is searched, U after D is not) since they commute.
    static bool Redundant(int face, int lastFace) => face == lastFace || face == lastFace - 3;

    sealed class Search
    {
        readonly CubieCube start;
        readonly CancellationToken cancel;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly int[] moves = new int[40];
        int[] best;
        int maxLength = 31; // any cube needs at most 12 + 18 moves
        int nodes;
        bool stop;

        public Search(CubieCube start, CancellationToken cancel)
        {
            this.start = start;
            this.cancel = cancel;
        }

        public int[] Run()
        {
            int twist = start.Twist, flip = start.Flip, slice = start.Slice;
            int h = Math.Max(sliceTwistPrune[slice * NTwist + twist], sliceFlipPrune[slice * NFlip + flip]);
            for (int depth = h; depth <= maxLength && !stop; depth++)
                Phase1(twist, flip, slice, 0, depth, -1);
            return best;
        }

        bool TimeUp()
        {
            if (cancel.IsCancellationRequested)
                return true;
            long ms = clock.ElapsedMilliseconds;
            if (best == null)
                return ms > GiveUpMillis;
            return ms > ImproveMillis || best.Length <= TargetLength && ms > QuickMillis;
        }

        void Phase1(int twist, int flip, int slice, int depth, int togo, int lastFace)
        {
            if ((++nodes & 0xFFF) == 0 && TimeUp())
                stop = true;
            if (stop)
                return;
            if (togo == 0)
            {
                // A last move that keeps G1 means the cube was already there one move earlier,
                // and that shorter phase 1 has been tried.
                if (depth == 0 || !isPhase2Move[moves[depth - 1]])
                    StartPhase2(depth);
                return;
            }
            for (int face = 0; face < 6; face++)
            {
                if (Redundant(face, lastFace))
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    int m = face * 3 + k;
                    int nt = twistMove[twist * N + m], nf = flipMove[flip * N + m], ns = sliceMove[slice * N + m];
                    int h = Math.Max(sliceTwistPrune[ns * NTwist + nt], sliceFlipPrune[ns * NFlip + nf]);
                    if (h > togo - 1)
                        continue;
                    moves[depth] = m;
                    Phase1(nt, nf, ns, depth + 1, togo - 1, face);
                    if (stop)
                        return;
                }
            }
        }

        void StartPhase2(int depth1)
        {
            var c = start.Clone();
            for (int i = 0; i < depth1; i++)
                c.ApplyMove(moves[i]);
            int corner = c.CornerPerm, edge = c.UDEdgePerm, slice = c.SlicePerm;
            int h = Math.Max(cornerSlicePrune[corner * NSlicePerm + slice], edgeSlicePrune[edge * NSlicePerm + slice]);
            int lastFace = depth1 > 0 ? moves[depth1 - 1] / 3 : -1;
            for (int depth2 = h; depth1 + depth2 <= maxLength; depth2++)
            {
                if (Phase2(corner, edge, slice, depth1, depth2, lastFace))
                {
                    best = moves[..(depth1 + depth2)];
                    maxLength = best.Length - 1;
                    if (TimeUp())
                        stop = true;
                    return;
                }
                if (stop)
                    return;
            }
        }

        bool Phase2(int corner, int edge, int slice, int depth, int togo, int lastFace)
        {
            if (togo == 0)
                return true; // the pruning values are 0 only for the solved cube
            if ((++nodes & 0xFFF) == 0 && TimeUp())
                stop = true;
            if (stop)
                return false;
            foreach (int m in Phase2Moves)
            {
                int face = m / 3;
                if (Redundant(face, lastFace))
                    continue;
                int nc = cornerMove[corner * N + m], ne = udEdgeMove[edge * N + m], ns = slicePermMove[slice * N + m];
                int h = Math.Max(cornerSlicePrune[nc * NSlicePerm + ns], edgeSlicePrune[ne * NSlicePerm + ns]);
                if (h > togo - 1)
                    continue;
                moves[depth] = m;
                if (Phase2(nc, ne, ns, depth + 1, togo - 1, face))
                    return true;
                if (stop)
                    return false;
            }
            return false;
        }
    }
}
