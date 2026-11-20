using System;

// A named part of a solution: moves [Start, Start + Length). Below: the stage works on the
// bottom face, best watched from underneath.
public sealed record SolutionStage(string Title, string Help, int Start, int Length, bool Below = false);

// The moves that solve a cube (see CubeState), grouped into stages, with an optional note per
// move naming the algorithm it belongs to.
public sealed record Solution(int[] Moves, SolutionStage[] Stages, string[] Notes)
{
    public static Solution Single(int[] moves, string title, string help) =>
        new(moves, moves.Length == 0 ? Array.Empty<SolutionStage>() : new[] { new SolutionStage(title, help, 0, moves.Length) },
            new string[moves.Length]);

    // The stage the move at step belongs to (the last stage once every move is made), or -1.
    public int StageAt(int step)
    {
        for (int i = 0; i < Stages.Length; i++)
            if (step < Stages[i].Start + Stages[i].Length)
                return i;
        return Stages.Length - 1;
    }
}
