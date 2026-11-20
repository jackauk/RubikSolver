using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

// Finds turns that take the cube to a target the player paints, in which any sticker may be
// "any color". The target is then not one cube but every cube whose stickers match the rest.
//
// Two ways, the shorter kept:
//  1. Complete the target to one real cube, as close to the current cube as it allows (pieces
//     the target lets stay where they are, stay), and run the two-phase solver on the difference
//     between the two. This always works, in about 20 moves.
//  2. When the target leaves stickers open, search the face turns for the fewest that reach any
//     matching cube (IDA*), for at most SearchMillis. Small goals, such as a cross, come out in
//     their fewest moves this way.
//
// Patterns give for each sticker the face (0..5 = U R F D L B) whose center color it must have,
// or Any.
public static class TargetSolver
{
    public const int Any = -1;
    public const int SearchMillis = 2500;
    const int Infinity = 1 << 20;
    const long AssignNodes = 2_000_000;
    const int States = CubieCube.PieceStates;

    // Why no real cube can match the pattern, or null.
    public static string Check(int[] pattern)
    {
        Goal goal = Goal.FromPattern(pattern, out string error);
        return goal == null ? error : Complete(null, goal, out error) == null ? error : null;
    }

    public static bool Matches(int[] faces, int[] pattern)
    {
        for (int i = 0; i < 54; i++)
            if (pattern[i] != Any && pattern[i] != faces[i])
                return false;
        return true;
    }

    // Turns that take the cube (faces of a real cube) to one matching the pattern.
    public static int[] Solve(int[] faces, int[] pattern, CancellationToken cancel = default)
    {
        CubieCube now = CubieCube.FromFaces(faces, out string error) ?? throw new ArgumentException(error);
        Goal goal = Goal.FromPattern(pattern, out error) ?? throw new ArgumentException(error);
        if (goal.IsMet(now))
            return Array.Empty<int>();
        CubieCube target = Complete(now, goal, out error) ?? throw new ArgumentException(error);

        // now * moves = target, so (target^-1 * now) * moves is solved.
        CubieCube difference = target.Inverse();
        difference.Multiply(now);
        int[] moves = TwoPhaseSolver.Solve(difference, cancel);

        if (pattern.Contains(Any))
        {
            int[] shorter = Search(now, goal, (moves?.Length ?? TwoPhaseSolver.TargetLength + 1) - 1, cancel);
            if (shorter != null)
                moves = shorter;
        }
        return moves;
    }

    // ---- what the pattern allows ----

    // For each piece, the states (position and turn) in which its stickers match the pattern.
    // A cube matches when every piece is in such a state.
    sealed class Goal
    {
        public readonly bool[][] Corner = new bool[CubieCube.Corners][];
        public readonly bool[][] Edge = new bool[CubieCube.Edges][];

        public static Goal FromPattern(int[] pattern, out string error)
        {
            error = null;
            var g = new Goal();
            return Fill(g.Corner, CubieCube.CornerStickers, CubieCube.CornerNames, "corner", pattern, ref error)
                   && Fill(g.Edge, CubieCube.EdgeStickers, CubieCube.EdgeNames, "edge", pattern, ref error)
                ? g
                : null;
        }

        // Piece j turned o in slot s shows its k-th sticker on the slot's sticker (o + k) mod turns.
        static bool Fill(bool[][] allowed, int[][] stickers, string[] names, string kind, int[] pattern, ref string error)
        {
            int turns = stickers[0].Length;
            for (int j = 0; j < allowed.Length; j++)
                allowed[j] = new bool[States];
            for (int s = 0; s < allowed.Length; s++)
            {
                bool any = false;
                for (int j = 0; j < allowed.Length; j++)
                    for (int o = 0; o < turns; o++)
                    {
                        bool fits = true;
                        for (int k = 0; k < turns; k++)
                        {
                            int want = pattern[stickers[s][(o + k) % turns]];
                            if (want != Any && want != stickers[j][k] / 9)
                                fits = false;
                        }
                        allowed[j][s * turns + o] = fits;
                        any |= fits;
                    }
                if (!any)
                {
                    error = $"No real {kind} has the colors the target asks for at {names[s]}.";
                    return false;
                }
            }
            return true;
        }

        public bool IsMet(CubieCube c)
        {
            for (int i = 0; i < CubieCube.Corners; i++)
                if (!Corner[c.Cp[i]][i * 3 + c.Co[i]])
                    return false;
            for (int i = 0; i < CubieCube.Edges; i++)
                if (!Edge[c.Ep[i]][i * 2 + c.Eo[i]])
                    return false;
            return true;
        }
    }

    // ---- 1. one real cube that matches ----

    // The matching real cube that differs least from now (null: any), or null with the reason.
    static CubieCube Complete(CubieCube now, Goal goal, out string error)
    {
        error = null;
        Assignment[] corners = Assign(goal.Corner, 3, now?.Cp, now?.Co);
        Assignment[] edges = Assign(goal.Edge, 2, now?.Ep, now?.Eo);
        int parity = -1;
        for (int p = 0; p < 2; p++)
            if (corners[p].Perm != null && edges[p].Perm != null
                && (parity < 0 || corners[p].Cost + edges[p].Cost < corners[parity].Cost + edges[parity].Cost))
                parity = p;
        if (parity < 0)
        {
            error = "No real cube looks like the target: it needs a piece twice, a single twisted corner or " +
                    "flipped edge, or two pieces swapped. Make more of its stickers rainbow, or fix them.";
            return null;
        }
        var c = new CubieCube();
        corners[parity].Perm.CopyTo(c.Cp, 0);
        corners[parity].Ori.CopyTo(c.Co, 0);
        edges[parity].Perm.CopyTo(c.Ep, 0);
        edges[parity].Ori.CopyTo(c.Eo, 0);
        if (c.Verify() != null || !goal.IsMet(c))
            throw new InvalidOperationException("The completed target is not a real cube.");
        return c;
    }

    sealed class Assignment
    {
        public int Cost = Infinity;
        public int[] Perm, Ori;
    }

    // A piece for every slot and its turn, as the goal allows, with the turns adding up as on a
    // real cube; the cheapest found for each permutation parity. Cost: the slots whose piece or
    // turn differs from now.
    static Assignment[] Assign(bool[][] allowed, int turns, int[] nowPerm, int[] nowOri)
    {
        int n = allowed.Length;
        var fits = new bool[n, n]; // [piece, slot]
        for (int p = 0; p < n; p++)
            for (int s = 0; s < n; s++)
                for (int o = 0; o < turns; o++)
                    fits[p, s] |= allowed[p][s * turns + o];

        int Cost(int slot, int piece, int o) => nowPerm != null && nowPerm[slot] == piece && nowOri[slot] == o ? 0 : 1;

        var best = new[] { new Assignment(), new Assignment() };
        var perm = new int[n];
        var used = new bool[n];
        long nodes = 0;

        // dp[k]: the least cost so far with the turns adding up to k (mod turns).
        void Dfs(int slot, int[] dp)
        {
            if (++nodes > AssignNodes || dp.Min() >= Math.Max(best[0].Cost, best[1].Cost))
                return;
            if (slot == n)
            {
                int parity = Parity(perm);
                if (dp[0] < best[parity].Cost)
                    best[parity] = new Assignment { Cost = dp[0], Perm = (int[])perm.Clone() };
                return;
            }
            for (int s = slot; s < n; s++)
            {
                bool some = false;
                for (int p = 0; p < n && !some; p++)
                    some = !used[p] && fits[p, s];
                if (!some)
                    return;
            }
            // The piece there now first, so the first cubes found change little.
            int stay = nowPerm?[slot] ?? 0;
            for (int i = 0; i < n; i++)
            {
                int p = (stay + i) % n;
                if (used[p] || !fits[p, slot])
                    continue;
                var next = new int[turns];
                Array.Fill(next, Infinity);
                for (int k = 0; k < turns; k++)
                    for (int o = 0; o < turns; o++)
                        if (allowed[p][slot * turns + o])
                            next[(k + o) % turns] = Math.Min(next[(k + o) % turns], dp[k] + Cost(slot, p, o));
                used[p] = true;
                perm[slot] = p;
                Dfs(slot + 1, next);
                used[p] = false;
            }
        }

        var start = new int[turns];
        Array.Fill(start, Infinity);
        start[0] = 0;
        Dfs(0, start);

        // The turns for each permutation found: the cheapest adding up to 0.
        foreach (Assignment a in best)
        {
            if (a.Perm == null)
                continue;
            var cost = new int[n + 1, turns];
            var choice = new int[n, turns];
            for (int k = 0; k < turns; k++)
                cost[0, k] = k == 0 ? 0 : Infinity;
            for (int s = 0; s < n; s++)
            {
                for (int k = 0; k < turns; k++)
                    cost[s + 1, k] = Infinity;
                for (int k = 0; k < turns; k++)
                    for (int o = 0; o < turns; o++)
                        if (allowed[a.Perm[s]][s * turns + o] && cost[s, k] + Cost(s, a.Perm[s], o) < cost[s + 1, (k + o) % turns])
                        {
                            cost[s + 1, (k + o) % turns] = cost[s, k] + Cost(s, a.Perm[s], o);
                            choice[s, (k + o) % turns] = o;
                        }
            }
            a.Ori = new int[n];
            for (int s = n - 1, k = 0; s >= 0; s--)
            {
                a.Ori[s] = choice[s, k];
                k = ((k - a.Ori[s]) % turns + turns) % turns;
            }
        }
        return best;
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

    // ---- 2. the fewest turns into any matching cube ----

    // A few pieces followed together, with the fewest turns from each of their joint states to
    // one where all of them match.
    sealed class Group
    {
        public int[] Pieces; // indexes into the searched pieces
        public byte[] Distance;
    }

    static int[] Search(CubieCube now, Goal goal, int maxDepth, CancellationToken cancel)
    {
        // Only the pieces the target says something about; the others match anywhere.
        var turn = new List<int[][]>();
        var allowed = new List<bool[]>();
        var start = new List<int>();
        var isCorner = new List<bool>();
        for (int j = 0; j < CubieCube.Corners; j++)
            if (goal.Corner[j].Contains(false))
            {
                int slot = Array.IndexOf(now.Cp, j);
                turn.Add(CubieCube.CornerTurn);
                allowed.Add(goal.Corner[j]);
                start.Add(slot * 3 + now.Co[slot]);
                isCorner.Add(true);
            }
        for (int j = 0; j < CubieCube.Edges; j++)
            if (goal.Edge[j].Contains(false))
            {
                int slot = Array.IndexOf(now.Ep, j);
                turn.Add(CubieCube.EdgeTurn);
                allowed.Add(goal.Edge[j]);
                start.Add(slot * 2 + now.Eo[slot]);
                isCorner.Add(false);
            }
        int count = turn.Count;
        if (count == 0)
            return Array.Empty<int>();

        // Pieces in pairs of the same kind, so the estimate sees how they get in each other's way.
        var groups = new List<Group>();
        for (int i = 0; i < count;)
        {
            int size = i + 1 < count && isCorner[i + 1] == isCorner[i] ? 2 : 1;
            int[] pieces = Enumerable.Range(i, size).ToArray();
            groups.Add(new Group { Pieces = pieces, Distance = Distances(pieces.Select(p => turn[p]).ToArray(), pieces.Select(p => allowed[p]).ToArray()) });
            i += size;
        }

        var states = new int[maxDepth + 1][];
        for (int d = 0; d <= maxDepth; d++)
            states[d] = new int[count];
        start.CopyTo(states[0]);
        var path = new int[maxDepth];
        var clock = Stopwatch.StartNew();
        long nodes = 0;
        bool stopped = false;

        int Estimate(int[] s)
        {
            int h = 0;
            foreach (Group g in groups)
            {
                int index = 0;
                foreach (int p in g.Pieces)
                    index = index * States + s[p];
                h = Math.Max(h, g.Distance[index]);
            }
            return h;
        }

        bool Dfs(int depth, int togo, int lastFace)
        {
            int h = Estimate(states[depth]);
            if (h == 0)
                return true;
            if (h > togo)
                return false;
            if ((++nodes & 4095) == 0 && (clock.ElapsedMilliseconds > SearchMillis || cancel.IsCancellationRequested))
                stopped = true;
            if (stopped)
                return false;
            int[] from = states[depth], to = states[depth + 1];
            for (int m = 0; m < CubeState.MoveCount; m++)
            {
                int face = m / 3;
                if (face == lastFace || face == lastFace - 3)
                    continue;
                for (int i = 0; i < count; i++)
                    to[i] = turn[i][m][from[i]];
                path[depth] = m;
                if (Dfs(depth + 1, togo - 1, face))
                    return true;
            }
            return false;
        }

        for (int depth = Estimate(states[0]); depth <= maxDepth && !stopped; depth++)
            if (Dfs(0, depth, -1))
                return path.Take(depth).ToArray();
        return null;
    }

    // The fewest turns from each joint state of the pieces (index: their states as digits in
    // base 24) to one where every piece is allowed, by a search backwards from those.
    static byte[] Distances(int[][][] turn, bool[][] allowed)
    {
        int size = 1;
        foreach (int[][] _ in turn)
            size *= States;
        var distance = new byte[size];
        Array.Fill(distance, byte.MaxValue);
        var queue = new Queue<int>();
        var digits = new int[turn.Length];
        for (int index = 0; index < size; index++)
        {
            Split(index, digits);
            bool all = true;
            for (int p = 0; p < digits.Length; p++)
                all &= allowed[p][digits[p]];
            if (all)
            {
                distance[index] = 0;
                queue.Enqueue(index);
            }
        }
        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            Split(index, digits);
            for (int m = 0; m < CubeState.MoveCount; m++)
            {
                // The state that move m takes here.
                int back = CubeState.Inverse(m), before = 0;
                for (int p = 0; p < digits.Length; p++)
                    before = before * States + turn[p][back][digits[p]];
                if (distance[before] == byte.MaxValue)
                {
                    distance[before] = (byte)(distance[index] + 1);
                    queue.Enqueue(before);
                }
            }
        }
        return distance;
    }

    static void Split(int index, int[] digits)
    {
        for (int p = digits.Length - 1; p >= 0; p--)
        {
            digits[p] = index % States;
            index /= States;
        }
    }
}
