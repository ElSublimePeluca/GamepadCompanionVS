using System;
using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace GamepadCompanion.Gui;

// Teclado virtual on-screen para ingresar texto con gamepad (chat,
// search inputs, etc). Tipo HUD para que NO robe focus del dialog
// debajo — así el chat sigue siendo el focused dialog y recibe los
// key events sintéticos que disparamos al presionar A.
//
// Layout: 3 filas QWERTY + 1 fila de teclas especiales (Espacio,
// Borrar, Enter, Cerrar). DPad navega el highlight; A presiona; B
// cierra el dialog (alias del [Cerrar]).
public sealed class VirtualKeyboardDialog : GuiDialog
{
    private const double DialogW    = 460;
    private const double TitleH     = 26;
    private const double KeyW       = 36;
    private const double KeyH       = 32;
    private const double KeyGap     = 4;
    private const double SpecialW   = 96;
    private const double Margin     = 12;
    private const double FooterPad  = 8;

    private static readonly double[] ColorIdle      = { 0.85, 0.85, 0.85, 1.0 };
    private static readonly double[] ColorSelected  = { 1.0,  0.84, 0.20, 1.0 };

    // Filas de letras. Posición = (row, col) ∈ Rows × Rows[row].Length.
    // Las filas inferiores se centran respecto a la primera (10 cols).
    // "/" y "." en la última fila para tipear comandos cómodamente.
    private static readonly string[][] Rows =
    {
        new[] { "q","w","e","r","t","y","u","i","o","p" },
        new[] { "a","s","d","f","g","h","j","k","l" },
        new[] { "z","x","c","v","b","n","m","/","." },
    };

    // Labels como lang keys; se resuelven en Compose (no en static init,
    // que podría correr antes de que el Lang del mod esté cargado).
    private static readonly (string LabelKey, int KeyCode, char Ch)[] Specials =
    {
        ("gamepadcompanion:vkb-space",     (int)GlKeys.Space,    ' '),
        ("gamepadcompanion:vkb-backspace", (int)GlKeys.BackSpace,'\b'),
        ("gamepadcompanion:vkb-enter",     (int)GlKeys.Enter,    '\n'),
        ("gamepadcompanion:vkb-close",     -1,                   '\0'),
    };

    private int row;
    private int col;
    // Cuando el cursor está en la fila de specials usamos row = Rows.Length
    // y col indexa Specials.
    private int SpecialRow => Rows.Length;

    public override EnumDialogType DialogType => EnumDialogType.HUD;
    public override string ToggleKeyCombinationCode => null!;
    public override double DrawOrder => 0.55;
    public override bool ShouldReceiveKeyboardEvents() => false;
    public override bool ShouldReceiveMouseEvents() => false;
    public override bool PrefersUngrabbedMouse => false;

    public VirtualKeyboardDialog(ICoreClientAPI capi) : base(capi)
    {
        Compose();
    }

    private void Compose()
    {
        double gridWidth  = 10 * KeyW + 9 * KeyGap;
        double gridHeight = Rows.Length * KeyH + (Rows.Length - 1) * KeyGap;
        double specialH   = KeyH;
        double totalH     = TitleH + Margin + gridHeight + FooterPad
                                  + specialH + Margin;

        var dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterBottom)
            .WithFixedAlignmentOffset(0, -90);
        var bgBounds = ElementBounds.Fixed(0, 0, DialogW, totalH);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        var titleBounds = ElementBounds.Fixed(0, 0, DialogW, TitleH);

        var compo = capi.Gui
            .CreateCompo("gpcompanion-vkb", dialogBounds)
            .AddShadedDialogBG(bgBounds, withTitleBar: false, alpha: 0.7f)
            .AddStaticText(Lang.Get("gamepadcompanion:virtual-keyboard"),
                           CairoFont.WhiteSmallText(),
                           EnumTextOrientation.Center, titleBounds)
            .BeginChildElements(bgBounds);

        double gridX = (DialogW - gridWidth) / 2;
        double gridY = TitleH + Margin;

        for (int r = 0; r < Rows.Length; r++)
        {
            double rowOffset = (gridWidth - (Rows[r].Length * KeyW
                                + (Rows[r].Length - 1) * KeyGap)) / 2;
            for (int c = 0; c < Rows[r].Length; c++)
            {
                double kx = gridX + rowOffset + c * (KeyW + KeyGap);
                double ky = gridY + r * (KeyH + KeyGap);
                bool selected = row == r && col == c;

                var bounds = ElementBounds.Fixed(kx, ky, KeyW, KeyH);
                var font = CairoFont.WhiteMediumText();
                font.Color = selected ? ColorSelected : ColorIdle;
                font.WithWeight(Cairo.FontWeight.Bold);
                if (selected)
                    font.WithStroke(new[] { 0.0, 0.0, 0.0, 0.9 }, 2.0);
                compo.AddStaticText(Rows[r][c].ToUpperInvariant(), font,
                                    EnumTextOrientation.Center, bounds);
            }
        }

        // Fila de specials
        double specialY = gridY + gridHeight + FooterPad;
        double specialsWidth = Specials.Length * SpecialW
                              + (Specials.Length - 1) * KeyGap;
        double specialX = (DialogW - specialsWidth) / 2;
        for (int s = 0; s < Specials.Length; s++)
        {
            double kx = specialX + s * (SpecialW + KeyGap);
            bool selected = row == SpecialRow && col == s;
            var bounds = ElementBounds.Fixed(kx, specialY, SpecialW, specialH);
            var font = CairoFont.WhiteSmallText();
            font.Color = selected ? ColorSelected : ColorIdle;
            font.WithWeight(Cairo.FontWeight.Bold);
            if (selected)
                font.WithStroke(new[] { 0.0, 0.0, 0.0, 0.9 }, 2.0);
            compo.AddStaticText("[" + Lang.Get(Specials[s].LabelKey) + "]", font,
                                EnumTextOrientation.Center, bounds);
        }

        compo.EndChildElements();
        SingleComposer = compo.Compose();
    }

    public void OnGamepadTick(GamepadState current, GamepadState previous)
    {
        bool moved = false;
        if (current.WasPressed(GamepadButton.DPadLeft, previous))  { col -= 1; moved = true; }
        if (current.WasPressed(GamepadButton.DPadRight, previous)) { col += 1; moved = true; }
        if (current.WasPressed(GamepadButton.DPadUp, previous))    { row -= 1; moved = true; }
        if (current.WasPressed(GamepadButton.DPadDown, previous))  { row += 1; moved = true; }

        if (moved) ClampAndRecompose();

        if (current.WasPressed(GamepadButton.A, previous))
            PressSelected();

        if (current.WasPressed(GamepadButton.B, previous))
            TryClose();
    }

    private void ClampAndRecompose()
    {
        if (row < 0) row = 0;
        int maxRow = SpecialRow;
        if (row > maxRow) row = maxRow;

        int rowLen = row < Rows.Length ? Rows[row].Length : Specials.Length;
        if (col < 0) col = 0;
        if (col >= rowLen) col = rowLen - 1;

        Compose();
    }

    private void PressSelected()
    {
        if (row == SpecialRow)
        {
            var spec = Specials[col];
            if (spec.KeyCode < 0)
            {
                TryClose();
                return;
            }
            DispatchKey(spec.KeyCode, spec.Ch, isText: spec.Ch != '\0' && spec.Ch != '\b' && spec.Ch != '\n');
            return;
        }

        char ch = Rows[row][col][0];
        int keyCode = MapCharToKeyCode(ch);
        DispatchKey(keyCode, ch, isText: true);
    }

    private static int MapCharToKeyCode(char ch) => ch switch
    {
        '/' => (int)GlKeys.Slash,
        '.' => (int)GlKeys.Period,
        _   => (int)Enum.Parse<GlKeys>(ch.ToString().ToUpperInvariant()),
    };

    // VS separa OnKeyDown (físico, hotkeys/navegación) y OnKeyPress
    // (carácter, inserción en text inputs). El OS dispara ambos como
    // callbacks independientes; el text input del chat solo escucha
    // KeyPress para insertar args.KeyChar. Para que el teclado virtual
    // escriba en el chat necesitamos llamar a los dos en secuencia.
    // Para teclas que NO son caracteres (Backspace, Enter, Escape), solo
    // OnKeyDown — KeyPress con KeyChar de control inserta basura.
    private void DispatchKey(int keyCode, char ch, bool isText)
    {
        if (capi.World is not ClientMain cm) return;

        var down = new KeyEvent { KeyCode = keyCode, KeyChar = ch };
        cm.OnKeyDown(down);

        if (isText)
        {
            var press = new KeyEvent { KeyCode = ch, KeyChar = ch };
            cm.OnKeyPress(press);
        }

        var up = new KeyEvent { KeyCode = keyCode, KeyChar = ch };
        cm.OnKeyUp(up);
    }
}
