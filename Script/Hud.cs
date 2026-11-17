using System;
using System.Collections.Generic;
using Godot;

// The panels, built in code. The left panel is the editor: palette, unfolded net, Solve and the
// editing buttons. The bottom panel lists the solution and steps through it. Hud only reports
// what the player does; Main decides what happens.
public partial class Hud : Control
{
    const int PanelWidth = 300;
    const int Margin = 12;
    const int SolutionHeight = 196;

    static readonly Color Accent = new(1f, 0.78f, 0.2f);
    static readonly Color Dim = new(0.62f, 0.65f, 0.72f);
    static readonly Color ErrorColor = new(1f, 0.45f, 0.4f);
    static readonly Color OkColor = new(0.55f, 0.9f, 0.6f);

    public event Action<int> ColorSelected;
    public event Action<int> StickerClicked;
    public event Action SolvePressed, ScramblePressed, ResetPressed, ClearPressed;
    public event Action FirstPressed, PrevPressed, PlayPressed, NextPressed, LastPressed;
    public event Action<int> StepSelected;
    public event Action<float> SpeedChanged;

    NetView net;
    readonly Button[] swatches = new Button[6];
    Label orientationLabel;
    Label statusLabel;
    Button solveButton;

    Label stepLabel;
    Label stepDetail;
    HFlowContainer moveList;
    readonly List<Button> chips = new();
    Button firstButton, prevButton, playButton, nextButton, lastButton;

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
        box.AddThemeConstantOverride("separation", 10);
        panel.AddChild(box);

        box.AddChild(MakeLabel("Rubik Solver", 26, Colors.White));
        box.AddChild(MakeLabel("1. Pick a color   2. Paint the stickers   3. Solve", 13, Dim));

        var palette = new HBoxContainer();
        palette.AddThemeConstantOverride("separation", 8);
        box.AddChild(palette);
        for (int i = 0; i < 6; i++)
        {
            int color = i;
            var swatch = new Button { CustomMinimumSize = new Vector2(38, 38), FocusMode = FocusModeEnum.None, TooltipText = $"{Palette.Names[i]} ({i + 1})" };
            swatch.Pressed += () => ColorSelected?.Invoke(color);
            palette.AddChild(swatch);
            swatches[i] = swatch;
        }

        net = new NetView();
        net.StickerClicked += i => StickerClicked?.Invoke(i);
        box.AddChild(net);

        orientationLabel = MakeLabel("", 13, Dim);
        orientationLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(orientationLabel);

        solveButton = MakeButton("Solve", () => SolvePressed?.Invoke());
        solveButton.CustomMinimumSize = new Vector2(0, 44);
        solveButton.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(solveButton);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        box.AddChild(row);
        foreach (var (text, tip, action) in new (string, string, Action)[]
                 {
                     ("Scramble", "Mix the cube with random turns", () => ScramblePressed?.Invoke()),
                     ("Solved", "Reset to a solved cube", () => ResetPressed?.Invoke()),
                     ("Clear", "Blank every sticker except the centers", () => ClearPressed?.Invoke())
                 })
        {
            var b = MakeButton(text, action);
            b.TooltipText = tip;
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(b);
        }

        statusLabel = MakeLabel("", 14, Dim);
        statusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(statusLabel);

        box.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        var help = MakeLabel(
            "Left click: paint a sticker\nDrag: turn the view   Wheel: zoom\n1-6: pick a color   Enter: solve\n" +
            "U R F D L B: turn a face (Shift: back)\nSpace: play   Left / Right: step", 12, Dim);
        box.AddChild(help);
    }

    public void SetSelectedColor(int color)
    {
        for (int i = 0; i < 6; i++)
        {
            bool selected = i == color;
            var style = Box(Palette.Colors[i], 6, 0);
            style.SetBorderWidthAll(selected ? 4 : 1);
            style.BorderColor = selected ? Colors.White : new Color(0f, 0f, 0f, 0.6f);
            foreach (string state in new[] { "normal", "hover", "pressed", "focus" })
                swatches[i].AddThemeStyleboxOverride(state, style);
        }
    }

    public void SetColors(int[] colors) => net.SetColors(colors);

    public void SetOrientation(string text) => orientationLabel.Text = text;

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

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 14);
        box.AddChild(header);
        stepLabel = MakeLabel("", 22, Colors.White);
        header.AddChild(stepLabel);
        stepDetail = MakeLabel("", 16, Dim);
        stepDetail.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        stepDetail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        header.AddChild(stepDetail);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        box.AddChild(scroll);
        moveList = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        moveList.AddThemeConstantOverride("h_separation", 5);
        moveList.AddThemeConstantOverride("v_separation", 5);
        scroll.AddChild(moveList);

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

    public void ShowSolution(int[] moves)
    {
        foreach (var chip in chips)
            chip.QueueFree();
        chips.Clear();
        for (int i = 0; i < moves.Length; i++)
        {
            int step = i;
            var chip = MakeButton(CubeState.MoveName(moves[i]), () => StepSelected?.Invoke(step));
            chip.CustomMinimumSize = new Vector2(40, 30);
            chip.AddThemeFontSizeOverride("font_size", 16);
            chip.TooltipText = $"Step {i + 1}";
            moveList.AddChild(chip);
            chips.Add(chip);
        }
        foreach (var b in new[] { firstButton, prevButton, playButton, nextButton, lastButton })
            b.Disabled = false;
    }

    // step = moves already made; the chip of the next move is highlighted.
    public void SetStep(int step, string title, string detail)
    {
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
        prevButton.Disabled = firstButton.Disabled = step == 0;
        nextButton.Disabled = lastButton.Disabled = step >= chips.Count;
    }

    public void SetPlaying(bool playing) => playButton.Text = playing ? "Pause" : "Play";

    public void ClearSolution(string message = "Paint your cube's colors, then press Solve.")
    {
        foreach (var chip in chips)
            chip.QueueFree();
        chips.Clear();
        stepLabel.Text = "Solution";
        stepDetail.Text = message;
        foreach (var b in new[] { firstButton, prevButton, playButton, nextButton, lastButton })
            b.Disabled = true;
        SetPlaying(false);
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
