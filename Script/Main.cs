using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

// Runs the solver app, in two modes, each with its own cube and solution:
//
//   Solve:     the player paints the stickers of their cube (on the 3D cube or on the net),
//              presses Solve, and steps through the solution while the 3D cube turns along.
//   Target:    two cubes stand side by side: on the left the player's cube, on the right the
//              target, the cube they want to get to, where a rainbow sticker means "any color".
//              Solve finds the turns from the one to the other. "Copy to Target mode" in Solve
//              mode brings the cube as it is at the current step over, to go on from there.
//
// The cubes are only colors (see CubeState). Solve checks that the colors can come from a real
// cube, maps each color to the face whose center has it, and runs a solver on a worker thread:
// in Solve mode the chosen method, the beginner's layer-by-layer method (BeginnerSolver, one side
// first) or Kociemba's two-phase solver for the fewest moves, whose tables are built in the
// background at start-up; in Target mode TargetSolver, the shortest way it finds.
//
// User arguments: --selftest checks the cube model and solver and quits with the result;
// --demo scrambles the cube, solves it and plays the solution.
public partial class Main : Node3D
{
    const int ScrambleLength = 25;
    const int Beginner = 0, Shortest = 1; // the Method choices in the panel
    const float CubeSpacing = 2.25f;      // in Target mode, each cube's distance from the middle of the view
    const string Preparing = "Preparing the solver...";
    const float SolveDistance = 10.5f, TargetDistance = 15.5f; // the camera's, to fit one cube or two

    CubeView cube;
    CubeView targetView;
    OrbitCamera cameraRig;
    Hud hud;

    // The mode on screen; the other one keeps its cube and solution in otherMode.
    sealed class Workspace
    {
        public int[] Colors = CubeState.Solved();
        public Solution Solution;
        public int[][] States;
        public int Step;
        public bool ToTarget;
    }

    bool targetMode;
    Workspace otherMode = new();

    int[] colors = CubeState.Solved();
    int[] target = CubeState.Solved();
    bool editTarget; // Target mode: the net shows the target rather than the player's cube
    int selectedColor;
    float moveDuration = 0.5f;
    int method = Beginner;

    // The solution being shown: the cube after each of its moves, and how many have been made.
    Solution solution;
    int[][] solutionStates;
    bool solutionToTarget; // the target is not the solved cube
    int step;
    int shownStage = -1; // the stage the camera was last turned for
    bool playing;

    Task initTask;
    Task<Solution> solveTask;
    int[] solvingFrom, solvingTo; // the cube and target solveTask works on
    bool solvingToTarget;
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

        targetView = new CubeView { Name = "Target", Visible = false };
        AddChild(targetView);
        targetView.SetTitle("Target");
        ProcessPriority = 1; // after the camera, so the cubes are placed for this frame's view

        cube.MoveFinished += OnMoveFinished;
        hud.ModeSelected += SwitchMode;
        hud.CopyToTargetPressed += CopyToTargetMode;
        hud.ColorSelected += SelectColor;
        hud.MethodSelected += m => method = m;
        hud.EditTargetSelected += SetEditTarget;
        hud.StickerClicked += i => Paint(i, editTarget);
        hud.SolvePressed += Solve;
        hud.ScramblePressed += Scramble;
        hud.ResetPressed += () => Edit(CubeState.Solved(), "Solved cube.");
        hud.ClearPressed += Clear;
        hud.CopyPressed += () => EditTarget((int[])colors.Clone(),
            "The target is a copy of your cube now. Paint what should change; rainbow means any color.");
        hud.TargetSolvedPressed += () => EditTarget(SolvedLike(colors), "Target: the solved cube.");
        hud.TargetAnyPressed += () => EditTarget(Enumerable.Range(0, 54).Select(i => i % 9 == 4 ? colors[i] : CubeState.Any).ToArray(),
            "Every target sticker is any color now. Paint the ones you care about.");
        hud.FirstPressed += () => JumpTo(0);
        hud.PrevPressed += Back;
        hud.PlayPressed += TogglePlay;
        hud.NextPressed += Next;
        hud.LastPressed += () => JumpTo(solution?.Moves.Length ?? 0);
        hud.StepSelected += JumpTo;
        hud.SpeedChanged += speed => moveDuration = Mathf.Lerp(1.0f, 0.12f, (speed - 0.1f) / 0.9f);

        SelectColor(0);
        hud.SetMode(false);
        hud.ClearSolution(IdleMessage);
        ShowColors();
        hud.SetStatus(Preparing);
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
        if (targetView == null) // --selftest: quitting
            return;
        // In Target mode both cubes face the same way, side by side across the view, wherever it
        // is turned.
        Vector3 right = cameraRig.GlobalTransform.Basis.X;
        cube.Position = targetMode ? -right * CubeSpacing : Vector3.Zero;
        targetView.Position = right * CubeSpacing;

        if (initTask is { IsCompleted: true })
        {
            if (initTask.IsFaulted)
                hud.SetStatus($"The solver failed to start: {initTask.Exception?.InnerException?.Message}", true);
            else if (hud.Status == Preparing) // nothing else said meanwhile
                hud.SetStatus(targetMode
                    ? "Ready. Paint your cube on the left and the target on the right."
                    : "Ready. Paint the stickers to match your cube.");
            initTask = null;
            if (solveWaiting)
            {
                solveWaiting = false;
                Solve();
            }
        }

        if (solveTask is { IsCompleted: true })
        {
            Task<Solution> finished = solveTask;
            solveTask = null;
            hud.SetSolving(false);
            if (finished.IsFaulted || finished.Result == null)
                hud.SetStatus("No solution found. " + (finished.Exception?.InnerException?.Message ?? "Check the colors and try again."), true);
            else if (!colors.SequenceEqual(solvingFrom) || !target.SequenceEqual(solvingTo))
                hud.SetStatus("The cube or the target changed while solving. Press Solve again.", true);
            else
                ShowSolution(finished.Result);
        }
    }

    // ---- modes ----

    string IdleMessage => targetMode
        ? "Paint your cube and the target, then press Solve."
        : "Paint your cube's colors, then press Solve.";

    bool Busy => cube.IsAnimating || solveTask != null || solveWaiting;

    void SwitchMode(bool toTarget)
    {
        if (toTarget == targetMode)
            return;
        if (Busy)
        {
            hud.SetMode(targetMode);
            hud.SetStatus("Wait for the turn or the solving to finish.", true);
            return;
        }
        playing = false;
        var leaving = new Workspace { Colors = colors, Solution = solution, States = solutionStates, Step = step, ToTarget = solutionToTarget };
        colors = otherMode.Colors;
        solution = otherMode.Solution;
        solutionStates = otherMode.States;
        step = otherMode.Step;
        solutionToTarget = otherMode.ToTarget;
        otherMode = leaving;

        targetMode = toTarget;
        targetView.Visible = toTarget;
        cube.SetTitle(toTarget ? "Your cube" : "");
        cameraRig.Distance = toTarget ? TargetDistance : SolveDistance;
        hud.SetMode(toTarget);
        if (!toTarget && selectedColor == Hud.AnySwatch)
            SelectColor(0);
        SetEditTarget(false);
        ShowColors();
        shownStage = -1;
        if (solution != null)
        {
            hud.ShowSolution(solution);
            ShowStep();
        }
        else
        {
            hud.ClearSolution(IdleMessage);
            cube.HighlightFace(-1);
        }
        hud.SetStatus(toTarget
            ? "Target mode: paint your cube (left) and the cube you want (right). Rainbow means any color."
            : "Solve mode: paint your cube and solve it.");
    }

    // From Solve mode: the cube as it is at this step becomes the player's cube in Target mode.
    void CopyToTargetMode()
    {
        if (targetMode || Busy)
            return;
        int[] now = (int[])colors.Clone();
        string what = solution == null ? "as in Solve mode" : $"at step {step} of {solution.Moves.Length}";
        SwitchMode(true);
        Edit(now, null);
        SetEditTarget(true);
        hud.SetStatus($"Your cube is now the cube {what}. Paint the target you want from here.");
    }

    // ---- editing ----

    void SelectColor(int color)
    {
        if (color == Hud.AnySwatch && !targetMode)
        {
            hud.SetStatus("Rainbow (any color) is for Target mode.", true);
            return;
        }
        selectedColor = color;
        hud.SetSelectedColor(color);
        if (color == Hud.AnySwatch && !editTarget)
        {
            SetEditTarget(true);
            hud.SetStatus("Rainbow: any color. Paint it on the target, where a cell may end up any color.");
        }
    }

    void SetEditTarget(bool on)
    {
        editTarget = on;
        hud.SetEditTarget(on);
        ShowNet();
    }

    // Paints a sticker of the player's cube or of the target with the selected color.
    void Paint(int sticker, bool onTarget)
    {
        if (sticker < 0 || cube.IsAnimating || (onTarget && !targetMode))
            return;
        if (onTarget != editTarget)
            SetEditTarget(onTarget);
        int color = selectedColor == Hud.AnySwatch ? CubeState.Any : selectedColor;
        if (!onTarget)
        {
            if (color == CubeState.Any)
            {
                hud.SetStatus("Rainbow (any color) is for the target only: your cube has real colors.", true);
                return;
            }
            if (colors[sticker] == color)
                return;
            var painted = (int[])colors.Clone();
            painted[sticker] = color;
            Edit(painted, null);
            return;
        }
        if (sticker % 9 == 4)
        {
            hud.SetStatus("Centers never move, so the target's centers are those of your cube.", true);
            return;
        }
        if (target[sticker] == color)
            return;
        var goal = (int[])target.Clone();
        goal[sticker] = color;
        EditTarget(goal, null);
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
        Edit(CubeState.Apply(SolvedLike(colors), moves), "Scrambled: " + string.Join(" ", moves.Select(CubeState.MoveName)));
    }

    // The solved cube in the center colors of the given one.
    static int[] SolvedLike(int[] cubeColors) => Enumerable.Range(0, 54).Select(i => cubeColors[i / 9 * 9 + 4]).ToArray();

    // Any change to either cube outside the solution ends the solution.
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

    void EditTarget(int[] newTarget, string status)
    {
        if (cube.IsAnimating)
            return;
        EndSolution();
        target = newTarget;
        if (!editTarget)
            SetEditTarget(true);
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
        ShowNet();
        hud.SetStatus($"Turned {CubeState.MoveName(move)}.");
    }

    void ShowColors()
    {
        // Turns never move centers, so the target's are always the cube's.
        for (int face = 0; face < 6; face++)
            target[face * 9 + 4] = colors[face * 9 + 4];
        cube.SetColors(colors);
        targetView.SetColors(target);
        ShowNet();
        ShowOrientation();
    }

    void ShowNet() => hud.SetColors(editTarget ? target : colors);

    void ShowOrientation()
    {
        int up = colors[CubeState.Up * 9 + 4], front = colors[CubeState.Front * 9 + 4];
        hud.SetOrientation(
            $"Hold your cube with the {ColorName(up)} center on top and the {ColorName(front)} one facing you. " +
            "Clockwise: as seen looking at that face.");
    }

    static string ColorName(int color) => color < 0 ? "blank" : Palette.Names[color];

    // ---- solving ----

    void Solve()
    {
        if (solveTask != null || cube.IsAnimating)
            return;
        string problem = ToFaces(out int[] faceOfColor, out int[] faces);
        CubieCube cubies = null;
        if (problem == null)
        {
            cubies = CubieCube.FromFaces(faces, out problem);
            problem ??= cubies.Verify();
        }
        // Solve mode solves the cube; Target mode goes to the painted target.
        int[] pattern = Enumerable.Range(0, 54).Select(i => i / 9).ToArray();
        if (problem == null && targetMode)
        {
            problem = TargetPattern(faceOfColor, out pattern) ?? TargetSolver.Check(pattern);
            if (problem != null)
                problem = "Target: " + problem;
        }
        if (problem != null)
        {
            EndSolution();
            hud.SetStatus(problem, true);
            return;
        }

        bool toSolved = !targetMode;
        if (TargetSolver.Matches(faces, pattern))
        {
            EndSolution();
            hud.SetStatus(toSolved ? "The cube is already solved." : "Your cube already matches the target.", ok: true);
            return;
        }
        if ((!toSolved || method == Shortest) && initTask != null)
        {
            solveWaiting = true;
            hud.SetSolving(true);
            hud.SetStatus("Preparing the solver, the solution follows in a moment...");
            return;
        }

        EndSolution();
        hud.SetSolving(true);
        solvingFrom = (int[])colors.Clone();
        solvingTo = (int[])target.Clone();
        solvingToTarget = !toSolved;
        if (!toSolved)
        {
            hud.SetStatus("Finding the shortest way to your target...");
            solveTask = Task.Run(() =>
            {
                int[] moves = TargetSolver.Solve(faces, pattern);
                return moves == null ? null : Solution.Single(moves, "To the target",
                    "Turns that take your cube to the target. Rainbow stickers of the target may end up any color.");
            });
        }
        else if (method == Beginner)
        {
            hud.SetStatus("Working out the beginner's solution...");
            string[] centerColors = Enumerable.Range(0, 6).Select(f => ColorName(colors[f * 9 + 4])).ToArray();
            solveTask = Task.Run(() => BeginnerSolver.Solve(faces, centerColors));
        }
        else
        {
            hud.SetStatus("Searching for a short solution...");
            solveTask = Task.Run(() =>
            {
                int[] moves = TwoPhaseSolver.Solve(cubies);
                return moves == null ? null : Solution.Single(moves, "Shortest solution",
                    "Found by Kociemba's two-phase search: few moves, but no stages a person would follow.");
            });
        }
    }

    // Checks the counts and centers and turns colors into faces (which center each matches).
    string ToFaces(out int[] faceOfColor, out int[] faces)
    {
        faces = null;
        faceOfColor = null;
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

        faceOfColor = new int[6];
        Array.Fill(faceOfColor, -1);
        for (int face = 0; face < 6; face++)
        {
            int center = colors[face * 9 + 4];
            if (faceOfColor[center] >= 0)
                return "The six centers must all have different colors.";
            faceOfColor[center] = face;
        }
        int[] map = faceOfColor;
        faces = colors.Select(c => map[c]).ToArray();
        return null;
    }

    // The target as faces, with TargetSolver.Any for its rainbow stickers.
    string TargetPattern(int[] faceOfColor, out int[] pattern)
    {
        pattern = null;
        int blank = target.Count(c => c == CubeState.Blank);
        if (blank > 0)
            return $"{blank} sticker{(blank == 1 ? " is" : "s are")} still blank (grey). Paint {(blank == 1 ? "it" : "them")}, " +
                   "or make them rainbow (any color).";
        var counts = new int[6];
        foreach (int c in target)
            if (c >= 0)
                counts[c]++;
        var tooMany = Enumerable.Range(0, 6).Where(c => counts[c] > 9).Select(c => $"{Palette.Names[c]} {counts[c]}").ToList();
        if (tooMany.Count > 0)
            return "A cube has only 9 stickers of each color. Now: " + string.Join(", ", tooMany) + ".";
        pattern = target.Select(c => c == CubeState.Any ? TargetSolver.Any : faceOfColor[c]).ToArray();
        return null;
    }

    void ShowSolution(Solution found)
    {
        solution = found;
        solutionToTarget = solvingToTarget;
        solutionStates = new int[found.Moves.Length + 1][];
        solutionStates[0] = (int[])colors.Clone();
        for (int i = 0; i < found.Moves.Length; i++)
            solutionStates[i + 1] = CubeState.Apply(solutionStates[i], found.Moves[i]);
        step = 0;
        shownStage = -1;
        hud.ShowSolution(found);
        hud.SetStatus(solutionToTarget
            ? $"{found.Moves.Length} move{(found.Moves.Length == 1 ? "" : "s")} take your cube to the target. Follow the steps below."
            : found.Stages.Length > 1
                ? $"Beginner's solution: {found.Moves.Length} turns in {found.Stages.Length} stages. Follow the steps below."
                : $"Solved in {found.Moves.Length} moves. Follow the steps below.", ok: true);
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
        solutionStates = null;
        playing = false;
        hud.ClearSolution(IdleMessage);
        cube.HighlightFace(-1);
    }

    void ShowStep()
    {
        hud.SetPlaying(playing);
        int count = solution.Moves.Length;
        if (step >= count)
        {
            if (solutionToTarget)
                hud.SetStep(step, "", "Target reached!", $"Your cube matches the target after {count} moves.");
            else
                hud.SetStep(step, "", "Solved!", $"The cube is solved after {count} moves.");
            cube.HighlightFace(-1);
            return;
        }

        string stageText = "";
        int si = solution.StageAt(step);
        if (si >= 0 && solution.Stages.Length > 1)
        {
            SolutionStage stage = solution.Stages[si];
            stageText = $"Stage {si + 1} of {solution.Stages.Length} · {stage.Title}:  {stage.Help}";
            // Entering a stage, look from the side it works on: from above, or from below for
            // the bottom face. Within a stage the view stays wherever the player turns it.
            if (si != shownStage)
            {
                shownStage = si;
                cameraRig.Pitch = stage.Below ? -cameraRig.ViewPitch : cameraRig.ViewPitch;
            }
        }

        int move = solution.Moves[step];
        string title = $"Step {step + 1} / {count}:  {CubeState.MoveName(move)}";
        int face = move / 3;
        string turn = (move % 3) switch
        {
            0 => "a quarter turn clockwise",
            1 => "a half turn",
            _ => "a quarter turn counter-clockwise"
        };
        string detail = $"Turn the {CubeState.FaceNames[face]} face ({ColorName(colors[face * 9 + 4])} center) {turn}.";
        if (solution.Notes[step] != null)
            detail += "\n" + solution.Notes[step];
        hud.SetStep(step, stageText, title, detail);
        cube.HighlightFace(face);
    }

    void Next()
    {
        if (solution == null || step >= solution.Moves.Length || cube.IsAnimating)
            return;
        int move = solution.Moves[step++];
        colors = solutionStates[step];
        cube.AnimateMove(move, colors, moveDuration);
        ShowNet();
        ShowStep();
    }

    void Back()
    {
        if (solution == null || step == 0 || cube.IsAnimating)
            return;
        playing = false;
        int move = CubeState.Inverse(solution.Moves[--step]);
        colors = solutionStates[step];
        cube.AnimateMove(move, colors, moveDuration);
        ShowNet();
        ShowStep();
    }

    void JumpTo(int to)
    {
        if (solution == null || cube.IsAnimating)
            return;
        playing = false;
        step = Math.Clamp(to, 0, solution.Moves.Length);
        colors = solutionStates[step];
        cube.SetColors(colors);
        ShowNet();
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
        if (step >= solution.Moves.Length)
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
        if (playing && step < solution.Moves.Length)
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
                        PaintAt(button.Position);
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

    // Paints the sticker under the mouse, on whichever cube it is.
    void PaintAt(Vector2 position)
    {
        int sticker = cube.PickSticker(cameraRig, position);
        if (sticker >= 0)
            Paint(sticker, false);
        else if (targetMode)
            Paint(targetView.PickSticker(cameraRig, position), true);
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
            case >= Key.Key1 and <= Key.Key7:
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
