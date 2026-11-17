using System;
using Godot;

// The cube unfolded flat (see CubeState for the layout). Click or drag over the cells to paint
// them; every sticker can be reached here without turning the 3D view.
public partial class NetView : Control
{
    const int Cell = 20;
    const int Gap = 2;
    const int FaceGap = 4;

    // Top-left cell of each face in the 12 x 9 grid, in U R F D L B order.
    static readonly Vector2I[] FaceOrigin = { new(3, 0), new(6, 3), new(3, 3), new(3, 6), new(0, 3), new(9, 3) };

    public event Action<int> StickerClicked;

    readonly int[] colors = new int[54];
    int hover = -1;
    int lastPainted = -1;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(12 * (Cell + Gap) + 3 * FaceGap, 9 * (Cell + Gap) + 2 * FaceGap);
        MouseFilter = MouseFilterEnum.Stop;
        MouseExited += () =>
        {
            hover = -1;
            QueueRedraw();
        };
    }

    public void SetColors(int[] newColors)
    {
        Array.Copy(newColors, colors, 54);
        QueueRedraw();
    }

    Rect2 CellRect(int sticker)
    {
        Vector2I origin = FaceOrigin[sticker / 9];
        int col = origin.X + sticker % 3, row = origin.Y + sticker % 9 / 3;
        return new Rect2(col * (Cell + Gap) + origin.X / 3 * FaceGap, row * (Cell + Gap) + origin.Y / 3 * FaceGap, Cell, Cell);
    }

    int StickerAt(Vector2 point)
    {
        for (int i = 0; i < 54; i++)
            if (CellRect(i).HasPoint(point))
                return i;
        return -1;
    }

    public override void _Draw()
    {
        Font font = GetThemeDefaultFont();
        for (int i = 0; i < 54; i++)
        {
            Rect2 rect = CellRect(i);
            DrawRect(rect, Palette.Get(colors[i]));
            if (i == hover)
                DrawRect(rect.Grow(1f), Colors.White, false, 2f);
            if (i % 9 == 4)
                DrawString(font, rect.Position + new Vector2(0f, Cell - 5f), CubeState.FaceLetters[i / 9].ToString(),
                    HorizontalAlignment.Center, Cell, 13, Palette.TextOn(colors[i]));
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } button)
        {
            lastPainted = StickerAt(button.Position);
            if (lastPainted >= 0)
                StickerClicked?.Invoke(lastPainted);
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion motion)
        {
            int at = StickerAt(motion.Position);
            if (at != hover)
            {
                hover = at;
                QueueRedraw();
            }
            if ((motion.ButtonMask & MouseButtonMask.Left) != 0 && at >= 0 && at != lastPainted)
            {
                lastPainted = at;
                StickerClicked?.Invoke(at);
            }
        }
    }
}
