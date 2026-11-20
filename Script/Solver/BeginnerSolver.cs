using System;
using System.Collections.Generic;
using System.Linq;

// Solves the cube the way the layer-by-layer method is taught to beginners. The first side is
// made on top and stays there; the cube is never turned over:
//
//   1. a cross on the top face        2. the top corners (R' D' R D)   -> the first side is done
//   3. the middle row of the four side faces (D' R' D R D F D' F' and its mirror)
//   4. a cross on the bottom face (F' R' D' R D F)
//   5. its edges matched to the sides (R' D' R D' R' D2 R D')
//   6. its corners moved into place (D' R' D L D' R D L')
//   7. its corners twisted (R U R' U' at the front-right-bottom corner, turning the bottom between)
//
// Steps 1-6 place one piece (or one group) at a time with the fewest beginner moves that leave
// the finished pieces alone: single turns for the cross, otherwise the stage's algorithm (from
// any side) and bottom turns. Each search is IDA* on just the pieces involved, guided by how far
// each piece alone is from its goal. Step 7 follows the method literally.
//
// Algorithms are written as the player turns them. The search itself looks at the cube upside
// down (U and D swapped, R and L swapped, F and B kept) so that the layer it is finishing is
// always U and the finished layer D; Played and Algorithm translate between the two.
public static class BeginnerSolver
{
    const int States = 24;  // where a piece is and how it is turned: slot * 3 + twist, or slot * 2 + flip
    const int Far = 99;
    const int MaxDepth = 14;
    const long NodeBudget = 50_000_000;

    static readonly int[] flipFace = { 3, 4, 2, 0, 1, 5 }; // upside down: U<->D, R<->L, F and B stay
    const int TwistSlot = 1; // UFL as the search sees it: the player's front-right-bottom corner
    static readonly int[] sides = { CubeState.Front, CubeState.Right, CubeState.Back, CubeState.Left };

    static readonly Macro[] faceTurns, uTurns;
    static readonly Macro[] cornerMacros, middleMacros, crossMacros, edgeMacros, cornerCycleMacros;
    static readonly Macro cornerTwist;

    static BeginnerSolver()
    {
        faceTurns = Enumerable.Range(0, CubeState.MoveCount).Select(Turn).ToArray();
        uTurns = new[] { Turn(0), Turn(1), Turn(2) }; // the player's bottom turns
        cornerMacros = uTurns.Concat(FromEachSide("Corner insert", "R' D' R D")).ToArray();
        middleMacros = uTurns
            .Concat(FromEachSide("Right insert", "D' R' D R D F D' F'"))
            .Concat(FromEachSide("Left insert", "D L D' L' D' F' D F"))
            .ToArray();
        crossMacros = uTurns.Append(Algorithm("Cross", "F' R' D' R D F", 0)).ToArray();
        edgeMacros = uTurns.Concat(FromEachSide("Edge swap", "R' D' R D' R' D2 R D'")).ToArray();
        cornerCycleMacros = FromEachSide("Corner cycle", "D' R' D L D' R D L'").ToArray();
        cornerTwist = Algorithm("Corner twist", "R U R' U'", 0);
    }

    // faces: which center (0..5 = U R F D L B) each sticker matches, of a cube that passed
    // CubieCube.Verify. colorNames: the color of each face's center, for the stage texts.
    public static Solution Solve(int[] faces, string[] colorNames)
    {
        var flipped = CubeState.Apply(faces, CubeState.FlipMove);
        for (int i = 0; i < flipped.Length; i++)
            flipped[i] = flipFace[flipped[i]];
        CubieCube cube = CubieCube.FromFaces(flipped, out string error) ?? throw new ArgumentException(error);
        var b = new Builder(cube);
        if (IsSolved(cube))
            return b.Finish();

        string top = colorNames[CubeState.Up], last = colorNames[CubeState.Down];
        string Top = Capitalize(top), Last = Capitalize(last);
        var done = new List<Piece>();

        b.Stage($"{Top} cross",
            $"Put the four {top} edges around the {top} center on top, each matching the center of its side.");
        PlaceOneByOne(b, done, Enumerable.Range(4, 4).Select(e => Piece.Home(false, e)), faceTurns);

        b.Stage($"{Top} corners",
            $"Turn the bottom to bring a {top} corner under its place, then repeat R' D' R D until it fits. " +
            $"After this the whole {top} side is done.");
        PlaceOneByOne(b, done, Enumerable.Range(4, 4).Select(c => Piece.Home(true, c)), cornerMacros);

        b.Stage("Middle row",
            $"Keep the {top} side on top. Find a bottom edge without {last}, turn the bottom until its side color " +
            "is under the matching center, then move it up to the right (D' R' D R D F D' F') " +
            "or to the left (D L D' L' D' F' D F).");
        PlaceOneByOne(b, done, Enumerable.Range(8, 4).Select(e => Piece.Home(false, e)), middleMacros);

        b.Stage($"{Last} cross",
            $"Make a {last} cross on the bottom with F' R' D' R D F, turning the bottom in between.", below: true);
        var lastEdgesDown = Enumerable.Range(0, 4).Select(e => Piece.Where(false, e, s => s / 2 < 4 && s % 2 == 0));
        PlaceAll(b, done.Concat(lastEdgesDown), crossMacros);

        b.Stage($"{Last} edges",
            "Match each bottom cross edge with the center of its side using R' D' R D' R' D2 R D'.", below: true);
        var lastEdges = Enumerable.Range(0, 4).Select(e => Piece.Home(false, e)).ToList();
        PlaceAll(b, done.Concat(lastEdges), edgeMacros);
        done.AddRange(lastEdges);

        b.Stage("Corners into place",
            "Move each bottom corner to the place between its three centers with D' R' D L D' R D L'. " +
            "It may still be twisted.", below: true);
        PlaceAll(b, done.Concat(Enumerable.Range(0, 4).Select(c => Piece.Where(true, c, s => s / 3 == c))), cornerCycleMacros);

        b.Stage("Twist the corners",
            $"Hold a twisted bottom corner at the front right and repeat R U R' U' until its {last} sticker " +
            $"faces down; then turn only the bottom to bring the next one there. The {top} side mixes up on " +
            "the way and comes back by itself.", below: true);
        TwistLastCorners(b);

        if (!IsSolved(b.Cube))
            throw new InvalidOperationException("The beginner solution did not solve the cube.");
        return b.Finish();
    }

    // Places the pieces one at a time, the cheapest next one first, keeping `done` in place.
    static void PlaceOneByOne(Builder b, List<Piece> done, IEnumerable<Piece> todo, Macro[] macros)
    {
        var left = todo.ToList();
        while (left.Count > 0)
        {
            List<Macro> best = null;
            Piece bestPiece = null;
            foreach (Piece p in left)
            {
                List<Macro> path = Find(b.Cube, done.Append(p).ToArray(), macros);
                if (best == null || Cost(b, path) < Cost(b, best))
                {
                    best = path;
                    bestPiece = p;
                }
                if (best.Count == 0)
                    break;
            }
            foreach (Macro m in best)
                b.Apply(m);
            done.Add(bestPiece);
            left.Remove(bestPiece);
        }
    }

    static void PlaceAll(Builder b, IEnumerable<Piece> pieces, Macro[] macros)
    {
        foreach (Macro m in Find(b.Cube, pieces.ToArray(), macros))
            b.Apply(m);
    }

    // Turns the player makes for path, once joined with each other and with the turn before.
    static int Cost(Builder b, List<Macro> path)
    {
        var faces = new List<(int Face, int Quarters)>();
        if (b.LastMove >= 0)
            faces.Add((b.LastMove / 3, b.LastMove % 3 + 1));
        foreach (int m in path.SelectMany(m => m.Moves))
        {
            if (faces.Count > 0 && faces[^1].Face == m / 3)
            {
                int q = (faces[^1].Quarters + m % 3 + 1) % 4;
                faces.RemoveAt(faces.Count - 1);
                if (q != 0)
                    faces.Add((m / 3, q));
            }
            else
                faces.Add((m / 3, m % 3 + 1));
        }
        return faces.Count - (b.LastMove >= 0 ? 1 : 0);
    }

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // The beginner's way: R U R' U' in pairs at the front-right-bottom corner until it is turned
    // right, bottom turns to bring the next twisted corner there, and a last one to line up.
    static void TwistLastCorners(Builder b)
    {
        int[] order = { 0, 2, 1 }; // U, U', U2
        for (int corner = 0; corner < 4; corner++)
        {
            if (Enumerable.Range(0, 4).All(i => b.Cube.Co[i] == 0))
                break;
            if (b.Cube.Co[TwistSlot] == 0)
                foreach (int m in order)
                {
                    CubieCube c = b.Cube.Clone();
                    c.ApplyMove(m);
                    if (c.Co[TwistSlot] != 0)
                    {
                        b.Apply(uTurns[m]);
                        break;
                    }
                }
            for (int pairs = 0; b.Cube.Co[TwistSlot] != 0; pairs++)
            {
                if (pairs == 3)
                    throw new InvalidOperationException("A corner did not untwist.");
                b.Apply(cornerTwist);
                b.Apply(cornerTwist);
            }
        }
        if (!IsSolved(b.Cube))
            foreach (int m in order)
            {
                CubieCube c = b.Cube.Clone();
                c.ApplyMove(m);
                if (IsSolved(c))
                {
                    b.Apply(uTurns[m]);
                    break;
                }
            }
    }

    static bool IsSolved(CubieCube c)
    {
        for (int i = 0; i < CubieCube.Corners; i++)
            if (c.Cp[i] != i || c.Co[i] != 0)
                return false;
        for (int i = 0; i < CubieCube.Edges; i++)
            if (c.Ep[i] != i || c.Eo[i] != 0)
                return false;
        return true;
    }

    // ---- moves ----

    // A single turn (Name null) or a named algorithm, with what it does to any one piece.
    sealed class Macro
    {
        public readonly string Name;
        public readonly int[] Moves;
        public readonly int Face;  // the face of a single turn, else -1
        public readonly int[] Corner = new int[States], Edge = new int[States];

        public Macro(string name, int[] moves, int face)
        {
            Name = name;
            Moves = moves;
            Face = face;
            for (int s = 0; s < States; s++)
            {
                int c = s, e = s;
                foreach (int m in moves)
                {
                    c = CubieCube.CornerTurn[m][c];
                    e = CubieCube.EdgeTurn[m][e];
                }
                Corner[s] = c;
                Edge[s] = e;
            }
        }

        // Turning the same face twice in a row, or U after D (the same as D after U), is never needed.
        public bool Follows(Macro last) =>
            last != null && Face >= 0 && last.Face >= 0 && (Face == last.Face || Face == last.Face - 3);
    }

    static Macro Turn(int move) => new(null, new[] { move }, move / 3);

    // notation as the player turns it with `front` (an index into sides) as the front face,
    // translated for the upside-down search.
    static Macro Algorithm(string name, string notation, int front)
    {
        var moves = new List<int>();
        foreach (string token in notation.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int face = CubeState.FaceLetters.IndexOf(token[0]);
            int side = Array.IndexOf(sides, face);
            if (side >= 0)
                face = sides[(side + front) % 4];
            int turn = token.Length == 1 ? 0 : token[1] == '2' ? 1 : 2;
            moves.Add(flipFace[face] * 3 + turn);
        }
        return new Macro(name, moves.ToArray(), -1);
    }

    static IEnumerable<Macro> FromEachSide(string name, string notation) =>
        Enumerable.Range(0, 4).Select(front => Algorithm(name, notation, front));

    // ---- search ----

    sealed class Piece
    {
        public bool IsCorner;
        public int Id;
        public readonly bool[] Goal = new bool[States];

        public static Piece Home(bool corner, int id) => Where(corner, id, s => s == id * (corner ? 3 : 2));

        public static Piece Where(bool corner, int id, Func<int, bool> goal)
        {
            var p = new Piece { IsCorner = corner, Id = id };
            for (int s = 0; s < States; s++)
                p.Goal[s] = goal(s);
            return p;
        }

        public int StateIn(CubieCube c)
        {
            int[] perm = IsCorner ? c.Cp : c.Ep, ori = IsCorner ? c.Co : c.Eo;
            int slot = Array.IndexOf(perm, Id);
            return slot * (IsCorner ? 3 : 2) + ori[slot];
        }

        // Fewest macros that take this piece alone from each state to a goal state.
        public int[] Distances(Macro[] macros)
        {
            var d = new int[States];
            for (int s = 0; s < States; s++)
                d[s] = Goal[s] ? 0 : Far;
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int s = 0; s < States; s++)
                    foreach (Macro m in macros)
                    {
                        int t = (IsCorner ? m.Corner : m.Edge)[s];
                        if (d[t] + 1 < d[s])
                        {
                            d[s] = d[t] + 1;
                            changed = true;
                        }
                    }
            }
            return d;
        }
    }

    // The fewest macros that bring every piece to its goal (IDA*).
    static List<Macro> Find(CubieCube cube, Piece[] pieces, Macro[] macros)
    {
        var search = new Search(pieces, macros);
        int[] start = pieces.Select(p => p.StateIn(cube)).ToArray();
        for (int depth = search.Estimate(start); depth <= MaxDepth; depth++)
            if (search.Run(start, depth, null))
                return new List<Macro>(search.Path);
        throw new InvalidOperationException("No beginner moves found for a stage.");
    }

    sealed class Search
    {
        readonly Piece[] pieces;
        readonly Macro[] macros;
        readonly int[][] distances;
        public readonly List<Macro> Path = new();
        long nodes;

        public Search(Piece[] pieces, Macro[] macros)
        {
            this.pieces = pieces;
            this.macros = macros;
            distances = pieces.Select(p => p.Distances(macros)).ToArray();
        }

        public int Estimate(int[] states)
        {
            int h = 0;
            for (int i = 0; i < states.Length; i++)
                h = Math.Max(h, distances[i][states[i]]);
            return h;
        }

        public bool Run(int[] states, int togo, Macro last)
        {
            int h = Estimate(states);
            if (h == 0)
                return true;
            if (h > togo)
                return false;
            if (++nodes > NodeBudget)
                throw new InvalidOperationException("The beginner search took too long.");
            var next = new int[states.Length];
            foreach (Macro m in macros)
            {
                if (m.Follows(last))
                    continue;
                for (int i = 0; i < states.Length; i++)
                    next[i] = (pieces[i].IsCorner ? m.Corner : m.Edge)[states[i]];
                Path.Add(m);
                if (Run(next, togo - 1, m))
                    return true;
                Path.RemoveAt(Path.Count - 1);
            }
            return false;
        }
    }


    // ---- output ----

    // Collects the moves as the player makes them, keeping the search's cube in step. Where one
    // piece's or algorithm's moves meet the next, two turns of the same face can end up side by
    // side; Finish joins them so no turn is wasted: D then D' are left out, D then D become D2.
    // Turns are only joined within a stage, so every stage still ends with its part finished.
    sealed class Builder
    {
        // A turn as planned: the stage it is for and the algorithm (if any) it is part of.
        sealed record Planned(int Move, int Stage, string Algorithm, int Index, int Count);

        // A turn as played: the planned turns it joins, and those that cancelled just before it.
        sealed class Played
        {
            public int Move, Stage;
            public readonly List<Planned> Parts = new();
            public string Skipped;
        }

        public readonly CubieCube Cube;
        readonly List<Planned> planned = new();
        readonly List<(string Title, string Help, bool Below)> stageInfo = new();

        public Builder(CubieCube cube) => Cube = cube;

        // The search's last move in the current stage, or -1.
        public int LastMove { get; private set; } = -1;

        public void Stage(string title, string help, bool below = false)
        {
            stageInfo.Add((title, help, below));
            LastMove = -1;
        }

        public void Apply(Macro m)
        {
            int[] moves = m.Moves.Select(AsPlayed).ToArray();
            string algorithm = m.Name == null ? null : $"{m.Name}: {string.Join(" ", moves.Select(CubeState.MoveName))}";
            for (int i = 0; i < moves.Length; i++)
            {
                planned.Add(new Planned(moves[i], stageInfo.Count - 1, algorithm, i, moves.Length));
                Cube.ApplyMove(m.Moves[i]);
                LastMove = m.Moves[i];
            }
        }

        // The search's move as the player makes it on the cube in their hands.
        static int AsPlayed(int move) => flipFace[move / 3] * 3 + move % 3;

        public Solution Finish()
        {
            var played = new List<Played>();
            string skipped = null;
            foreach (Planned p in planned)
            {
                Played last = played.Count > 0 ? played[^1] : null;
                if (last != null && last.Stage == p.Stage && last.Move / 3 == p.Move / 3)
                {
                    last.Parts.Add(p);
                    int quarters = (last.Move % 3 + 1 + p.Move % 3 + 1) % 4;
                    if (quarters != 0)
                        last.Move = p.Move / 3 * 3 + quarters - 1;
                    else
                    {
                        played.RemoveAt(played.Count - 1);
                        skipped = Join(skipped, Join(last.Skipped, $"{Turns(last.Parts)} cancel out"));
                    }
                    continue;
                }
                var turn = new Played { Move = p.Move, Stage = p.Stage, Skipped = skipped };
                turn.Parts.Add(p);
                played.Add(turn);
                skipped = null;
            }

            // A turn joined into the one before belongs to that one's stage, so stages stay in order.
            var stages = new List<SolutionStage>();
            for (int i = 0; i < played.Count;)
            {
                int stage = played[i].Stage, start = i;
                while (i < played.Count && played[i].Stage == stage)
                    i++;
                var (title, help, below) = stageInfo[stage];
                stages.Add(new SolutionStage(title, help, start, i - start, below));
            }
            return new Solution(played.Select(t => t.Move).ToArray(), stages.ToArray(), played.Select(Note).ToArray());
        }

        static string Note(Played turn)
        {
            var parts = new List<string>();
            Planned step = turn.Parts.FirstOrDefault(p => p.Algorithm != null);
            if (step != null)
                parts.Add($"{step.Algorithm}  (move {step.Index + 1} of {step.Count})");
            if (turn.Parts.Count > 1)
                parts.Add($"{Turns(turn.Parts)} together make {CubeState.MoveName(turn.Move)}");
            if (turn.Skipped != null)
                parts.Add($"left out just before: {turn.Skipped}");
            return parts.Count == 0 ? null : string.Join(";  ", parts);
        }

        static string Turns(List<Planned> parts) => string.Join(" + ", parts.Select(p => CubeState.MoveName(p.Move)));

        static string Join(string a, string b) => a == null ? b : $"{a}, {b}";
    }
}
