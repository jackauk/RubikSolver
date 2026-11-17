using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

// Runs the solver app: the player paints the stickers of their cube (on the 3D cube or on the
// net), presses Solve, and then steps through the solution while the 3D cube turns along.
//
// The cube is only colors (see CubeState). Solve checks that the colors can come from a real
// cube, maps each color to the face whose center has it, and runs Kociemba's two-phase solver
// on a worker thread. The solver tables are built in the background at start-up.
//
// User arguments: --selftest checks the cube model and solver and quits with the result;
// --demo scrambles the cube, solves it and plays the solution.
public partial class Main : Node3D
{
    const int ScrambleLength = 25;

    CubeView cube;
    OrbitCamera cameraRig;
    Hud hud;

    int[] colors = CubeState.Solved();
    int selectedColor;
    float moveDuration = 0.5f;

    // The solution being shown: the cube it starts from and how many moves have been made.
    int[] solution;
    int[] solutionStart;
    int step;
    bool playing;

    Task initTask;
    Task<int[]> solveTask;
    bool solveWaiting; // Solve pressed while the tables are still being built
    bool demo;

    bool leftDown;
    bool dragged;
    Vector2 pressPosition;

    readonly Random random = new();

    public override void _Ready()
    {
        cube = GetNode<CubeView>("Cube");
        cameraRig = GetNode<OrbitCamera>("Camera");
        hud = GetNode<Hud>("UI/Hud");

        string[] args = OS.GetCmdlineUserArgs();
        if (args.Contains("--selftest"))
        {
            bool ok = SelfTest.Run();
            GetTree().Quit(ok ? 0 : 1);
            return;
        }

        cube.MoveFinished += OnMoveFinished;
        hud.ColorSelected += SelectColor;
        hud.StickerClicked += Paint;
        hud.SolvePressed += Solve;
        hud.ScramblePressed += Scramble;
        hud.ResetPressed += () => Edit(CubeState.Solved(), "Solved cube.");
        hud.ClearPressed += Clear;
        hud.FirstPressed += () => JumpTo(0);
        hud.PrevPressed += Back;
        hud.PlayPressed += TogglePlay;
        hud.NextPressed += Next;
        hud.LastPressed += () => JumpTo(solution?.Length ?? 0);
        hud.StepSelected += JumpTo;
        hud.SpeedChanged += speed => moveDuration = Mathf.Lerp(1.0f, 0.12f, (speed - 0.1f) / 0.9f);

        SelectColor(0);
        ShowColors();
        hud.SetStatus("Preparing the solver...");
        initTask = Task.Run(TwoPhaseSolver.Init);

        if (args.Contains("--demo"))
        {
            demo = true;
            Scramble();
            Solve();
        }
    }

    public override void _Process(double delta)
    {
        if (initTask is { IsCompleted: true })
        {
            if (initTask.IsFaulted)
                hud.SetStatus($"The solver failed to start: {initTask.Exception?.InnerException?.Message}", true);
            else if (!solveWaiting && solveTask == null)
                hud.SetStatus("Ready. Paint the stickers to match your cube.");
            initTask = null;
            if (solveWaiting)
            {
                solveWaiting = false;
                Solve();
            }
        }

        if (solveTask is { IsCompleted: true })
        {
            Task<int[]> finished = solveTask;
            solveTask = null;
            hud.SetSolving(false);
            if (finished.IsFaulted || finished.Result == null)
                hud.SetStatus("No solution found. Check the colors and try again.", true);
            else
                ShowSolution(finished.Result);
        }
    }

    // ---- editing ----

    void SelectColor(int color)
    {
        selectedColor = color;
        hud.SetSelectedColor(color);
    }

    void Paint(int sticker)
    {
        if (sticker < 0 || colors[sticker] == selectedColor || cube.IsAnimating)
            return;
        var painted = (int[])colors.Clone();
        painted[sticker] = selectedColor;
        Edit(painted, null);
    }

    void Clear()
    {
        var cleared = new int[54];
        for (int i = 0; i < 54; i++)
            cleared[i] = i % 9 == 4 ? colors[i] : CubeState.Blank;
        Edit(cleared, "Cleared. Paint every sticker to match your cube.");
    }

    void Scramble()
    {
        var moves = new List<int>();
        int lastFace = -1;
        while (moves.Count < ScrambleLength)
        {
            int face = random.Next(6);
            if (face == lastFace)
                continue;
            moves.Add(face * 3 + random.Next(3));
            lastFace = face;
        }
        // Scrambles a solved cube in the current center colors.
        var start = new int[54];
        for (int i = 0; i < 54; i++)
            start[i] = colors[i / 9 * 9 + 4];
        Edit(CubeState.Apply(start, moves), "Scrambled: " + string.Join(" ", moves.Select(CubeState.MoveName)));
    }

    // Any change to the cube outside the solution ends the solution.
    void Edit(int[] newColors, string status)
    {
        if (cube.IsAnimating)
            return;
        EndSolution();
        colors = newColors;
        ShowColors();
        if (status != null)
            hud.SetStatus(status);
    }

    void TurnFace(int move)
    {
        if (cube.IsAnimating)
            return;
        EndSolution();
        colors = CubeState.Apply(colors, move);
        cube.AnimateMove(move, colors, moveDuration);
        hud.SetColors(colors);
        hud.SetStatus($"Turned {CubeState.MoveName(move)}.");
    }

    void ShowColors()
    {
        cube.SetColors(colors);
        hud.SetColors(colors);
        int up = colors[CubeState.Up * 9 + 4], front = colors[CubeState.Front * 9 + 4];
        hud.SetOrientation(
            $"Hold your cube with the {ColorName(up)} center on top and the {ColorName(front)} center facing you. " +
            "Clockwise means as seen looking straight at that face.");
    }

    static string ColorName(int color) => color < 0 ? "blank" : Palette.Names[color];

    // ---- solving ----

    void Solve()
    {
        if (solveTask != null || cube.IsAnimating)
            return;
        string problem = ToFaces(out int[] faces);
        CubieCube cubies = null;
        if (problem == null)
        {
            cubies = CubieCube.FromFaces(faces, out problem);
            problem ??= cubies.Verify();
        }
        if (problem != null)
        {
            EndSolution();
            hud.SetStatus(problem, true);
            return;
        }
        if (CubeState.IsSolved(colors))
        {
            EndSolution();
            hud.SetStatus("The cube is already solved.", ok: true);
            return;
        }
        if (initTask != null)
        {
            solveWaiting = true;
            hud.SetSolving(true);
            hud.SetStatus("Preparing the solver, the solution follows in a moment...");
            return;
        }

        EndSolution();
        hud.SetSolving(true);
        hud.SetStatus("Searching for a short solution...");
        solveTask = Task.Run(() => TwoPhaseSolver.Solve(cubies));
    }

    // Checks the counts and centers and turns colors into faces (which center each matches).
    string ToFaces(out int[] faces)
    {
        faces = null;
        var counts = new int[6];
        int blank = 0;
        foreach (int c in colors)
            if (c < 0)
                blank++;
            else
                counts[c]++;
        if (blank > 0)
            return blank == 1 ? "1 sticker is still blank (grey)." : $"{blank} stickers are still blank (grey).";

        var wrong = Enumerable.Range(0, 6).Where(c => counts[c] != 9).Select(c => $"{Palette.Names[c]} {counts[c]}").ToList();
        if (wrong.Count > 0)
            return "Each color must be on exactly 9 stickers. Now: " + string.Join(", ", wrong) + ".";

        var faceOfColor = new int[6];
        Array.Fill(faceOfColor, -1);
        for (int face = 0; face < 6; face++)
        {
            int center = colors[face * 9 + 4];
            if (faceOfColor[center] >= 0)
                return "The six centers must all have different colors.";
            faceOfColor[center] = face;
        }
        faces = colors.Select(c => faceOfColor[c]).ToArray();
        return null;
    }

    void ShowSolution(int[] moves)
    {
        solution = moves;
        solutionStart = (int[])colors.Clone();
        step = 0;
        hud.ShowSolution(moves);
        hud.SetStatus($"Solved in {moves.Length} moves. Follow the steps below.", ok: true);
        ShowStep();
        if (demo)
        {
            demo = false;
            TogglePlay();
        }
    }

    void EndSolution()
    {
        if (solution == null)
            return;
        solution = null;
        solutionStart = null;
        playing = false;
        hud.ClearSolution();
        cube.HighlightFace(-1);
    }

    void ShowStep()
    {
        hud.SetPlaying(playing);
        if (step >= solution.Length)
        {
            hud.SetStep(step, "Solved!", $"The cube is solved after {solution.Length} moves.");
            cube.HighlightFace(-1);
            return;
        }
        int move = solution[step];
        int face = move / 3;
        string turn = (move % 3) switch
        {
            0 => "a quarter turn clockwise",
            1 => "a half turn",
            _ => "a quarter turn counter-clockwise"
        };
        string center = ColorName(solutionStart[face * 9 + 4]);
        hud.SetStep(step, $"Step {step + 1} / {solution.Length}:  {CubeState.MoveName(move)}",
            $"Turn the {CubeState.FaceNames[face]} face ({center} center) {turn}.");
        cube.HighlightFace(face);
    }

    void Next()
    {
        if (solution == null || step >= solution.Length || cube.IsAnimating)
            return;
        int move = solution[step++];
        colors = CubeState.Apply(colors, move);
        cube.AnimateMove(move, colors, moveDuration);
        hud.SetColors(colors);
        ShowStep();
    }

    void Back()
    {
        if (solution == null || step == 0 || cube.IsAnimating)
            return;
        playing = false;
        int move = CubeState.Inverse(solution[--step]);
        colors = CubeState.Apply(colors, move);
        cube.AnimateMove(move, colors, moveDuration);
        hud.SetColors(colors);
        ShowStep();
    }

    void JumpTo(int target)
    {
        if (solution == null || cube.IsAnimating)
            return;
        playing = false;
        step = Math.Clamp(target, 0, solution.Length);
        colors = CubeState.Apply(solutionStart, solution.Take(step));
        cube.SetColors(colors);
        hud.SetColors(colors);
        ShowStep();
    }

    void TogglePlay()
    {
        if (solution == null)
            return;
        if (playing)
        {
            playing = false;
            hud.SetPlaying(false);
            return;
        }
        if (step >= solution.Length)
            JumpTo(0);
        playing = true;
        hud.SetPlaying(true);
        if (!cube.IsAnimating)
            Next();
    }

    void OnMoveFinished()
    {
        if (solution == null)
            return;
        if (playing && step < solution.Length)
            Next();
        else if (playing)
        {
            playing = false;
            ShowStep();
        }
    }

    // ---- input on the 3D view (the panels take their own clicks first) ----

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                cameraRig.Zoom(0.9f);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                cameraRig.Zoom(1.1f);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                if (button.Pressed)
                {
                    leftDown = true;
                    dragged = false;
                    pressPosition = button.Position;
                }
                else if (leftDown)
                {
                    leftDown = false;
                    if (!dragged)
                        Paint(cube.PickSticker(cameraRig, button.Position));
                }
                break;
            case InputEventMouseMotion motion
                when (motion.ButtonMask & (MouseButtonMask.Left | MouseButtonMask.Right | MouseButtonMask.Middle)) != 0:
                if (leftDown && !dragged && motion.Position.DistanceTo(pressPosition) < 6f)
                    break;
                dragged = true;
                cameraRig.Orbit(motion.Relative);
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                HandleKey(key);
                break;
        }
    }

    void HandleKey(InputEventKey key)
    {
        int face = "URFDLB".IndexOf(key.Keycode.ToString(), StringComparison.Ordinal);
        if (key.Keycode.ToString().Length == 1 && face >= 0)
        {
            TurnFace(face * 3 + (key.ShiftPressed ? 2 : 0));
            return;
        }
        switch (key.Keycode)
        {
            case >= Key.Key1 and <= Key.Key6:
                SelectColor((int)(key.Keycode - Key.Key1));
                break;
            case Key.Enter or Key.KpEnter:
                Solve();
                break;
            case Key.Space:
                TogglePlay();
                break;
            case Key.Right:
                playing = false;
                Next();
                break;
            case Key.Left:
                Back();
                break;
            case Key.Home:
                cameraRig.ResetView();
                break;
        }
    }
}
