using System;
using System.Collections.Generic;
using Godot;

// The panels, built in code. The left panel is the editor: the mode (Solve or Target), palette,
// the unfolded net of the cube being edited (in Target mode the player's cube or the target),
// Solve and the editing buttons. The bottom panel lists the solution and steps through it. Hud
// only reports what the player does; Main decides what happens.
public partial class Hud : Control
{
    const int PanelWidth = 300;
    const int Margin = 12;
    const int SolutionHeight = 226;
    public const int AnySwatch = 6; // the seventh swatch: the rainbow, "any color"

    static readonly Color Accent = new(1f, 0.78f, 0.2f);
    static readonly Color Dim = new(0.62f, 0.65f, 0.72f);
    static readonly Color ErrorColor = new(1f, 0.45f, 0.4f);
    static readonly Color OkColor = new(0.55f, 0.9f, 0.6f);

    public event Action<bool> ModeSelected; // true: Target mode
    public event Action CopyToTargetPressed;
    public event Action<int> ColorSelected;
    public event Action<int> MethodSelected;
    public event Action<bool> EditTargetSelected;
    public event Action<int> StickerClicked;
    public event Action SolvePressed, ScramblePressed, ResetPressed, ClearPressed;
    public event Action CopyPressed, TargetSolvedPressed, TargetAnyPressed;
    public event Action FirstPressed, PrevPressed, PlayPressed, NextPressed, LastPressed;
    public event Action<int> StepSelected;
    public event Action<float> SpeedChanged;

    NetView net;
    Button solveModeTab, targetModeTab;
    Label introLabel;
    readonly Button[] swatches = new Button[7];
    HBoxContainer editTabs;
    Button cubeTab, targetTab;
    HBoxContainer cubeButtons, targetButtons, methodRow;
    Label orientationLabel;
    Label statusLabel;
    Button solveButton;

    Label stageLabel;
    Label stepLabel;
    Label stepDetail;
    ScrollContainer moveScroll;
    HFlowContainer moveList;
    readonly List<Button> chips = new();
    Button firstButton, prevButton, playButton, nextButton, lastButton, copyButton;

    StyleBoxFlat chipDone, chipNext, chipTodo;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        chipDone = Box(new Color(0.16f, 0.17f, 0.2f), 6, 6);
        chipNext = Box(Accent, 6, 6);
        chipTodo = Box(new Color(0.24f, 0.26f, 0.32f), 6, 6);
        BuildEditor();
        BuildSolution();
        ClearSolution();
    }

    // ---- editor panel ----

    void BuildEditor()
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Box(new Color(0.1f, 0.11f, 0.15f, 0.94f), 10, 14));
        AddChild(panel);
        panel.AnchorBottom = 1f;
        panel.OffsetLeft = Margin;
        panel.OffsetTop = Margin;
        panel.OffsetRight = Margin + PanelWidth;
        panel.OffsetBottom = -Margin;

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);

        box.AddChild(MakeLabel("Rubik Solver", 26, Colors.White));

        var modes = new HBoxContainer();
        modes.AddThemeConstantOverride("separation", 6);
        box.AddChild(modes);
        solveModeTab = MakeButton("Solve", () => ModeSelected?.Invoke(false));
        solveModeTab.TooltipText = "Paint your cube and solve it, step by step";
        targetModeTab = MakeButton("Target", () => ModeSelected?.Invoke(true));
        targetModeTab.TooltipText = "Paint your cube and the cube you want to get to; rainbow stickers may be any color";
        Toggles(modes, solveModeTab, targetModeTab);

        introLabel = MakeLabel("1. Pick a color   2. Paint the stickers   3. Solve", 13, Dim);
        box.AddChild(introLabel);

        var palette = new HBoxContainer();
        palette.AddThemeConstantOverride("separation", 5);
        box.AddChild(palette);
        for (int i = 0; i < swatches.Length; i++)
        {
            int color = i;
            var swatch = new Button
            {
                CustomMinimumSize = new Vector2(34, 34),
                FocusMode = FocusModeEnum.None,
                TooltipText = i == AnySwatch ? "Any color (7): the target cell may end up any color" : $"{Palette.Names[i]} ({i + 1})"
            };
            if (i == AnySwatch)
                swatch.AddChild(new TextureRect
                {
                    Texture = Palette.Rainbow,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.Scale,
                    MouseFilter = MouseFilterEnum.Ignore,
                    AnchorRight = 1f,
                    AnchorBottom = 1f,
                    OffsetLeft = 4,
                    OffsetTop = 4,
                    OffsetRight = -4,
                    OffsetBottom = -4
                });
            swatch.Pressed += () => ColorSelected?.Invoke(color);
            palette.AddChild(swatch);
            swatches[i] = swatch;
        }

        // Target mode: which cube the net shows and edits.
        editTabs = new HBoxContainer();
        editTabs.AddThemeConstantOverride("separation", 6);
        box.AddChild(editTabs);
        cubeTab = MakeButton("Your cube (left)", () => EditTargetSelected?.Invoke(false));
        targetTab = MakeButton("Target (right)", () => EditTargetSelected?.Invoke(true));
        Toggles(editTabs, cubeTab, targetTab);

        net = new NetView();
        net.StickerClicked += i => StickerClicked?.Invoke(i);
        box.AddChild(net);

        cubeButtons = ButtonRow(box,
            ("Scramble", "Mix your cube with random turns", () => ScramblePressed?.Invoke()),
            ("Solved", "Reset your cube to solved", () => ResetPressed?.Invoke()),
            ("Clear", "Blank every sticker except the centers", () => ClearPressed?.Invoke()));
        targetButtons = ButtonRow(box,
            ("Copy cube", "Make the target the same as your cube, to change from there", () => CopyPressed?.Invoke()),
            ("Solved", "Make the target the solved cube", () => TargetSolvedPressed?.Invoke()),
            ("All any", "Make every target sticker any color, except the centers", () => TargetAnyPressed?.Invoke()));
        targetButtons.Visible = false;

        orientationLabel = MakeLabel("", 13, Dim);
        orientationLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(orientationLabel);

        methodRow = new HBoxContainer();
        methodRow.AddThemeConstantOverride("separation", 8);
        box.AddChild(methodRow);
        methodRow.AddChild(MakeLabel("Method", 14, Dim));
        var method = new OptionButton
        {
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = "Beginner: the layer-by-layer method, one side first. Longer, but every stage can be learned.\n" +
                          "Shortest: about 20 moves, found by computer search.\n" +
                          "A target other than the solved cube is always reached the shortest way."
        };
        method.AddItem("Beginner: one side first", 0);
        method.AddItem("Shortest: fewest moves", 1);
        method.ItemSelected += index => MethodSelected?.Invoke((int)index);
        methodRow.AddChild(method);

        solveButton = MakeButton("Solve", () => SolvePressed?.Invoke());
        solveButton.CustomMinimumSize = new Vector2(0, 44);
        solveButton.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(solveButton);

        statusLabel = MakeLabel("", 14, Dim);
        statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(statusLabel);

        box.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        var help = MakeLabel(
            "Click: paint   Drag: turn the view   Wheel: zoom\n1-6: pick a color   7: any   Enter: solve\n" +
            "U R F D L B: turn a face (Shift: back)\nSpace: play   Left / Right: step", 12, Dim);
        box.AddChild(help);
    }

    // Two buttons of which one is chosen, highlighted.
    static void Toggles(HBoxContainer row, Button a, Button b)
    {
        var group = new ButtonGroup();
        StyleBoxFlat chosen = Box(Accent, 4, 4);
        foreach (Button tab in new[] { a, b })
        {
            tab.ToggleMode = true;
            tab.ButtonGroup = group;
            tab.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            tab.AddThemeStyleboxOverride("pressed", chosen);
            tab.AddThemeStyleboxOverride("hover_pressed", chosen);
            tab.AddThemeColorOverride("font_pressed_color", Colors.Black);
            tab.AddThemeColorOverride("font_hover_pressed_color", Colors.Black);
            row.AddChild(tab);
        }
        a.ButtonPressed = true;
    }

    static HBoxContainer ButtonRow(VBoxContainer box, params (string Text, string Tip, Action Action)[] buttons)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        box.AddChild(row);
        foreach (var (text, tip, action) in buttons)
        {
            var b = MakeButton(text, action);
            b.TooltipText = tip;
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(b);
        }
        return row;
    }

    // color: 0..5, or AnySwatch.
    public void SetSelectedColor(int color)
    {
        for (int i = 0; i < swatches.Length; i++)
        {
            bool selected = i == color;
            var style = Box(i == AnySwatch ? new Color(0.1f, 0.11f, 0.15f) : Palette.Colors[i], 6, 0);
            style.SetBorderWidthAll(selected ? 4 : 1);
            style.BorderColor = selected ? Colors.White : new Color(0f, 0f, 0f, 0.6f);
            foreach (string state in new[] { "normal", "hover", "pressed", "focus" })
                swatches[i].AddThemeStyleboxOverride(state, style);
        }
    }

    // Solve mode: one cube, solved by the chosen method. Target mode: the player's cube and the
    // target, with the rainbow and the target's buttons.
    public void SetMode(bool target)
    {
        solveModeTab.SetPressedNoSignal(!target);
        targetModeTab.SetPressedNoSignal(target);
        introLabel.Visible = !target; // in Target mode the cube tabs say it
        swatches[AnySwatch].Visible = target;
        editTabs.Visible = target;
        methodRow.Visible = !target;
        copyButton.Visible = !target;
        SetEditTarget(false);
    }

    // Target mode: shows the player's cube or the target on the net, with its buttons.
    public void SetEditTarget(bool target)
    {
        cubeTab.SetPressedNoSignal(!target);
        targetTab.SetPressedNoSignal(target);
        cubeButtons.Visible = !target;
        targetButtons.Visible = target;
    }

    public void SetColors(int[] colors) => net.SetColors(colors);

    public void SetOrientation(string text) => orientationLabel.Text = text;

    public string Status => statusLabel.Text;

    public void SetStatus(string text, bool error = false, bool ok = false)
    {
        statusLabel.Text = text;
        statusLabel.AddThemeColorOverride("font_color", error ? ErrorColor : ok ? OkColor : Dim);
    }

    public void SetSolving(bool solving)
    {
        solveButton.Disabled = solving;
        solveButton.Text = solving ? "Solving..." : "Solve";
    }

    // ---- solution panel ----

    void BuildSolution()
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Box(new Color(0.1f, 0.11f, 0.15f, 0.94f), 10, 12));
        AddChild(panel);
        panel.AnchorTop = 1f;
        panel.AnchorRight = 1f;
        panel.AnchorBottom = 1f;
        panel.OffsetLeft = Margin * 2 + PanelWidth;
        panel.OffsetTop = -Margin - SolutionHeight;
        panel.OffsetRight = -Margin;
        panel.OffsetBottom = -Margin;

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        panel.AddChild(box);

        stageLabel = MakeLabel("", 15, Accent);
        stageLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(stageLabel);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 14);
        box.AddChild(header);
        stepLabel = MakeLabel("", 22, Colors.White);
        header.AddChild(stepLabel);
        stepDetail = MakeLabel("", 16, Dim);
        stepDetail.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        stepDetail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        header.AddChild(stepDetail);

        moveScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        box.AddChild(moveScroll);
        moveList = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        moveList.AddThemeConstantOverride("h_separation", 5);
        moveList.AddThemeConstantOverride("v_separation", 5);
        moveScroll.AddChild(moveList);

        var controls = new HBoxContainer();
        controls.AddThemeConstantOverride("separation", 6);
        box.AddChild(controls);
        firstButton = MakeButton("|<  Start", () => FirstPressed?.Invoke());
        prevButton = MakeButton("<  Back", () => PrevPressed?.Invoke());
        playButton = MakeButton("Play", () => PlayPressed?.Invoke());
        playButton.CustomMinimumSize = new Vector2(90, 0);
        nextButton = MakeButton("Next  >", () => NextPressed?.Invoke());
        lastButton = MakeButton("End  >|", () => LastPressed?.Invoke());
        foreach (var b in new[] { firstButton, prevButton, playButton, nextButton, lastButton })
            controls.AddChild(b);
        copyButton = MakeButton("Copy to Target mode", () => CopyToTargetPressed?.Invoke());
        copyButton.TooltipText = "Take the cube as it is at this step to Target mode, to choose where to go from here";
        controls.AddChild(copyButton);

        controls.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        controls.AddChild(MakeLabel("Speed", 14, Dim));
        var speed = new HSlider
        {
            MinValue = 0.1,
            MaxValue = 1.0,
            Step = 0.05,
            Value = 0.55,
            CustomMinimumSize = new Vector2(140, 0),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            FocusMode = FocusModeEnum.None
        };
        speed.ValueChanged += v => SpeedChanged?.Invoke((float)v);
        controls.AddChild(speed);
    }

    // One chip per move; with several stages, each stage's chips follow its numbered name.
    public void ShowSolution(Solution solution)
    {
        ClearMoveList();
        for (int si = 0; si < solution.Stages.Length; si++)
        {
            SolutionStage stage = solution.Stages[si];
            if (solution.Stages.Length > 1)
            {
                var tag = MakeLabel($"{si + 1}. {stage.Title}", 14, Accent);
                tag.CustomMinimumSize = new Vector2(0, 30);
                tag.VerticalAlignment = VerticalAlignment.Center;
                tag.TooltipText = stage.Help;
                tag.MouseFilter = MouseFilterEnum.Pass;
                moveList.AddChild(tag);
            }
            for (int i = stage.Start; i < stage.Start + stage.Length; i++)
            {
                int step = i;
                var chip = MakeButton(CubeState.MoveName(solution.Moves[i]), () => StepSelected?.Invoke(step));
                chip.CustomMinimumSize = new Vector2(40, 30);
                chip.AddThemeFontSizeOverride("font_size", 16);
                chip.TooltipText = $"Step {i + 1}" + (solution.Notes[i] != null ? "\n" + solution.Notes[i] : "");
                moveList.AddChild(chip);
                chips.Add(chip);
            }
        }
        foreach (var b in new[] { firstButton, prevButton, playButton, nextButton, lastButton })
            b.Disabled = false;
    }

    // step = moves already made; the chip of the next move is highlighted and scrolled into view.
    public void SetStep(int step, string stage, string title, string detail)
    {
        stageLabel.Text = stage;
        stepLabel.Text = title;
        stepDetail.Text = detail;
        for (int i = 0; i < chips.Count; i++)
        {
            StyleBoxFlat style = i < step ? chipDone : i == step ? chipNext : chipTodo;
            foreach (string state in new[] { "normal", "hover", "pressed" })
                chips[i].AddThemeStyleboxOverride(state, style);
            Color text = i < step ? Dim : i == step ? Colors.Black : Colors.White;
            chips[i].AddThemeColorOverride("font_color", text);
            chips[i].AddThemeColorOverride("font_hover_color", text);
        }
        if (step < chips.Count)
            moveScroll.CallDeferred(ScrollContainer.MethodName.EnsureControlVisible, chips[step]);
        prevButton.Disabled = firstButton.Disabled = step == 0;
        nextButton.Disabled = lastButton.Disabled = step >= chips.Count;
    }

    public void SetPlaying(bool playing) => playButton.Text = playing ? "Pause" : "Play";

    public void ClearSolution(string message = "")
    {
        ClearMoveList();
        stageLabel.Text = "";
        stepLabel.Text = "Solution";
        stepDetail.Text = message;
        foreach (var b in new[] { firstButton, prevButton, playButton, nextButton, lastButton })
            b.Disabled = true;
        SetPlaying(false);
    }

    void ClearMoveList()
    {
        foreach (Node child in moveList.GetChildren())
            child.QueueFree();
        chips.Clear();
    }

    // ---- helpers ----

    static Label MakeLabel(string text, int size, Color color)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    static Button MakeButton(string text, Action pressed)
    {
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None };
        b.Pressed += pressed;
        return b;
    }

    static StyleBoxFlat Box(Color color, int radius, int padding)
    {
        var style = new StyleBoxFlat { BgColor = color };
        style.SetCornerRadiusAll(radius);
        style.SetContentMarginAll(padding);
        return style;
    }
}
