using System;
using System.Diagnostics;
using System.Linq;
using Godot;

// Checks the sticker model against Kociemba's published face turns, the validation of
// impossible cubes, and solves random scrambles. Run with the user argument --selftest:
//
//   Godot_console.exe --headless --path . -- --selftest
public static class SelfTest
{
    public static bool Run()
    {
        bool ok = true;
        void Check(bool condition, string what)
        {
            if (!condition)
            {
                ok = false;
                GD.PrintErr("FAIL: " + what);
            }
        }

        // Four quarter turns restore the cube; a turn and its inverse cancel.
        int[] solved = CubeState.Solved();
        for (int m = 0; m < CubeState.MoveCount; m++)
        {
            int[] c = solved;
            for (int k = 0; k < 4; k++)
                c = CubeState.Apply(c, m);
            Check(c.SequenceEqual(solved), $"{CubeState.MoveName(m)} four times");
            Check(CubeState.Apply(CubeState.Apply(solved, m), CubeState.Inverse(m)).SequenceEqual(solved),
                $"{CubeState.MoveName(m)} then its inverse");
        }

        // The R and F turns as published with the two-phase algorithm.
        CubieCube r = CubieCube.FaceMoves[CubeState.Right], f = CubieCube.FaceMoves[CubeState.Front];
        Check(r.Cp.SequenceEqual(new[] { 4, 1, 2, 0, 7, 5, 6, 3 }), "R corner permutation");
        Check(r.Co.SequenceEqual(new[] { 2, 0, 0, 1, 1, 0, 0, 2 }), "R corner orientation");
        Check(r.Ep.SequenceEqual(new[] { 8, 1, 2, 3, 11, 5, 6, 7, 4, 9, 10, 0 }), "R edge permutation");
        Check(r.Eo.All(o => o == 0), "R edge orientation");
        Check(f.Cp.SequenceEqual(new[] { 1, 5, 2, 3, 0, 4, 6, 7 }), "F corner permutation");
        Check(f.Co.SequenceEqual(new[] { 1, 2, 0, 0, 2, 1, 0, 0 }), "F corner orientation");
        Check(f.Ep.SequenceEqual(new[] { 0, 9, 2, 3, 4, 8, 6, 7, 1, 5, 10, 11 }), "F edge permutation");
        Check(f.Eo.SequenceEqual(new[] { 0, 1, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0 }), "F edge orientation");

        // Impossible cubes are refused.
        var twisted = (int[])solved.Clone();
        (twisted[8], twisted[9], twisted[20]) = (twisted[9], twisted[20], twisted[8]); // URF corner
        Check(CubieCube.FromFaces(twisted, out _)?.Verify()?.Contains("twisted") == true, "twisted corner detected");
        var flipped = (int[])solved.Clone();
        (flipped[5], flipped[10]) = (flipped[10], flipped[5]); // UR edge
        Check(CubieCube.FromFaces(flipped, out _)?.Verify()?.Contains("flipped") == true, "flipped edge detected");
        var swapped = (int[])solved.Clone();
        (swapped[7], swapped[5]) = (swapped[5], swapped[7]);    // UF and UR edges swapped
        (swapped[19], swapped[10]) = (swapped[10], swapped[19]);
        Check(CubieCube.FromFaces(swapped, out _)?.Verify()?.Contains("swapped") == true, "swapped edges detected");

        var clock = Stopwatch.StartNew();
        TwoPhaseSolver.Init();
        GD.Print($"Solver tables built in {clock.ElapsedMilliseconds} ms");

        // Short cases, then random scrambles.
        foreach (int m in new[] { 3, 0, 4 })
        {
            int[] sol = TwoPhaseSolver.Solve(CubieCube.FromFaces(CubeState.Apply(solved, m), out _));
            Check(sol != null && sol.Length == 1 && sol[0] == CubeState.Inverse(m),
                $"solve single {CubeState.MoveName(m)}: got {(sol == null ? "nothing" : string.Join(" ", sol.Select(CubeState.MoveName)))}");
        }

        var random = new Random(7);
        int total = 0, longest = 0;
        long slowest = 0;
        const int Cubes = 30;
        for (int n = 0; n < Cubes; n++)
        {
            int[] cube = solved;
            for (int k = 0; k < 30; k++)
                cube = CubeState.Apply(cube, random.Next(CubeState.MoveCount));
            CubieCube cc = CubieCube.FromFaces(cube, out string error);
            Check(cc != null && cc.Verify() == null, $"scramble {n} is valid ({error})");
            clock.Restart();
            int[] sol = TwoPhaseSolver.Solve(cc);
            slowest = Math.Max(slowest, clock.ElapsedMilliseconds);
            Check(sol != null && CubeState.IsSolved(CubeState.Apply(cube, sol)), $"scramble {n} solved");
            if (sol != null)
            {
                total += sol.Length;
                longest = Math.Max(longest, sol.Length);
            }
        }
        GD.Print($"{Cubes} scrambles: average {total / (float)Cubes:F1} moves, longest {longest}, slowest {slowest} ms");

        // Targets: a whole cube is reached exactly; with stickers left any color, the rest
        // matches; the cross alone comes out in its fewest moves; impossible targets are refused.
        int[] RandomCube()
        {
            int[] c = solved;
            for (int k = 0; k < 30; k++)
                c = CubeState.Apply(c, random.Next(CubeState.MoveCount));
            return c;
        }
        slowest = 0;
        for (int n = 0; n < 6; n++)
        {
            int[] from = RandomCube(), pattern = RandomCube();
            if (n >= 3)
                for (int i = 0; i < 54; i++)
                    if (i % 9 != 4 && random.Next(3) == 0)
                        pattern[i] = TargetSolver.Any;
            Check(TargetSolver.Check(pattern) == null, $"target {n} is possible");
            clock.Restart();
            int[] moves = TargetSolver.Solve(from, pattern);
            slowest = Math.Max(slowest, clock.ElapsedMilliseconds);
            Check(moves != null && TargetSolver.Matches(CubeState.Apply(from, moves), pattern), $"target {n} reached");
        }
        int[] cross = Enumerable.Range(0, 54).Select(i => i % 9 == 4 ? i / 9 : TargetSolver.Any).ToArray();
        foreach (int[] edge in CubieCube.EdgeStickers.Take(4))
            foreach (int s in edge)
                cross[s] = s / 9;
        longest = 0;
        for (int n = 0; n < 5; n++)
        {
            int[] from = RandomCube();
            clock.Restart();
            int[] moves = TargetSolver.Solve(from, cross);
            slowest = Math.Max(slowest, clock.ElapsedMilliseconds);
            Check(moves != null && moves.Length <= 8 && TargetSolver.Matches(CubeState.Apply(from, moves), cross),
                $"cross target {n}: {moves?.Length} moves");
            longest = Math.Max(longest, moves?.Length ?? 0);
        }
        Check(TargetSolver.Solve(solved, cross).Length == 0, "a target already met needs no moves");
        Check(TargetSolver.Check(twisted) != null, "a target with a twisted corner is refused");
        var twoEdges = (int[])cross.Clone();
        (twoEdges[3], twoEdges[37]) = (twoEdges[7], twoEdges[19]); // UL asks for the UF edge as well
        Check(TargetSolver.Check(twoEdges) != null, "a target with one edge twice is refused");
        GD.Print($"Targets: crosses in at most {longest} moves, slowest {slowest} ms");

        // Turning the cube over: twice is nothing, and a U turn before it is a D turn after it.
        int[] mixed = CubeState.Apply(solved, new[] { 3, 7, 0, 14, 10 });
        Check(CubeState.Apply(CubeState.Apply(mixed, CubeState.FlipMove), CubeState.FlipMove).SequenceEqual(mixed), "z2 twice");
        Check(CubeState.Apply(CubeState.Apply(mixed, CubeState.Up * 3), CubeState.FlipMove)
                .SequenceEqual(CubeState.Apply(CubeState.Apply(mixed, CubeState.FlipMove), CubeState.Down * 3)), "U then z2 = z2 then D");

        // The beginner's method: the first side on top, where it stays, then layer by layer down.
        string[] names = Palette.Names;
        total = longest = 0;
        slowest = 0;
        for (int n = 0; n < Cubes; n++)
        {
            int[] cube = solved;
            for (int k = 0; k < 30; k++)
                cube = CubeState.Apply(cube, random.Next(CubeState.MoveCount));
            clock.Restart();
            Solution sol;
            try
            {
                sol = BeginnerSolver.Solve(cube, names);
            }
            catch (Exception e)
            {
                Check(false, $"beginner scramble {n}: {e.Message}");
                continue;
            }
            slowest = Math.Max(slowest, clock.ElapsedMilliseconds);
            total += sol.Moves.Length;
            longest = Math.Max(longest, sol.Moves.Length);
            Check(CubeState.IsSolved(CubeState.Apply(cube, sol.Moves)), $"beginner scramble {n} solved");
            Check(sol.Moves.All(m => m < CubeState.MoveCount), $"beginner scramble {n} uses face turns only");
            Check(Enumerable.Range(1, sol.Moves.Length - 1).All(i => sol.Moves[i] / 3 != sol.Moves[i - 1] / 3 || sol.StageAt(i) != sol.StageAt(i - 1)),
                $"beginner scramble {n}: no two turns of the same face in a row within a stage");

            foreach (SolutionStage stage in sol.Stages)
            {
                int[] after = CubeState.Apply(cube, sol.Moves.Take(stage.Start + stage.Length));
                bool Row(int face, int row) => Enumerable.Range(0, 3).All(c => after[face * 9 + row * 3 + c] == after[face * 9 + 4]);
                int[] sides = { CubeState.Front, CubeState.Right, CubeState.Back, CubeState.Left };
                bool topDone = Row(CubeState.Up, 0) && Row(CubeState.Up, 1) && Row(CubeState.Up, 2) && sides.All(f => Row(f, 0));
                if (stage.Title != "White cross")
                    Check(topDone, $"beginner scramble {n}: top side done after '{stage.Title}'");
                if (stage.Title == "Middle row")
                    Check(topDone && sides.All(f => Row(f, 1)), $"beginner scramble {n}: top and middle rows done after the middle row");
            }
        }
        GD.Print($"{Cubes} beginner solutions: average {total / (float)Cubes:F1} moves, longest {longest}, slowest {slowest} ms");
        GD.Print(ok ? "SELFTEST PASSED" : "SELFTEST FAILED");
        return ok;
    }
}
