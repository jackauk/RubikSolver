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
        GD.Print(ok ? "SELFTEST PASSED" : "SELFTEST FAILED");
        return ok;
    }
}
