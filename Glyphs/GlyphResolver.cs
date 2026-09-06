namespace GamepadCompanion.Glyphs;

using System;
using System.Collections.Generic;
using GamepadCompanion.Actions;
using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

// El mapa inverso: "qué botón del mando hace esta tecla".
//
// Se indexa por KEYCODE NUMÉRICO, nunca por el nombre de tecla que muestra el
// juego. GlKeyNames cae en GLFW.GetKeyName, que devuelve el carácter del layout
// físico del teclado: en AZERTY, GlKeys.Q se muestra "A". Cualquier tabla
// string → glifo está rota fuera de QWERTY-US.
//
// Todas las fuentes se colapsan contra el binding VIVO (capi.Input.HotKeys), así
// que si el usuario rebindea `toolmodeselect` en Ajustes > Controles el hint se
// entera solo, sin ninguna tabla paralela que mantener.
internal sealed class GlyphResolver
{
    private readonly ICoreClientAPI capi;
    private readonly GamepadCompanionConfig config;
    private readonly IGamepadProvider provider;
    // Los bindings llegan como funciones y no como el GamepadInputDriver entero
    // por dos motivos: son lo ÚNICO que el resolver necesita de él, y se
    // reasignan al recargar el config, así que hay que leerlos frescos en cada
    // construcción del mapa. De paso el resolver queda probable sin el juego
    // abierto (ver `gpclab selftest`).
    private readonly Func<ButtonBindings> buttonBindings;
    private readonly Func<SlotBindings> wheelBindings;

    private readonly Dictionary<int, GlyphBinding> byKeyCode = new();
    private readonly GlyphBinding?[] byMouse = new GlyphBinding?[3];
    private readonly HashSet<int> vetoed = new();
    private readonly List<string> collisions = new();
    private bool dirty = true;
    // Identidad del mando con la que se armó el mapa actual. Ver EnsureBuilt.
    private string? builtDevice;
    private int builtVendor;

    public GlyphResolver(ICoreClientAPI capi, GamepadCompanionConfig config,
                         IGamepadProvider provider,
                         Func<ButtonBindings> buttonBindings,
                         Func<SlotBindings> wheelBindings)
    {
        this.capi = capi;
        this.config = config;
        this.provider = provider;
        this.buttonBindings = buttonBindings;
        this.wheelBindings = wheelBindings;
    }

    // Sube en cada Invalidate. Lo usa el diagnóstico para que se vea que el mapa
    // se está rearmando cuando debe.
    public int Revision { get; private set; }

    public event Action? Changed;

    public GlyphStyle ConfiguredStyle => GlyphStyles.Parse(config.GlyphStyle);

    // La familia que efectivamente se dibuja. Con `auto`, sale del device.
    public GlyphStyle Style => ConfiguredStyle == GlyphStyle.Auto
        ? GlyphStyles.FromDevice(provider.DeviceName, provider.VendorId)
        : ConfiguredStyle;

    // El disparador es "hay mando conectado", NO "el mando fue el último input".
    // Los dos candidatos a señal de mouse físico (capi.Input.MouseX/Y y
    // MouseDeltaX/Y) los escribe este mismo mod (VirtualCursor.SyncOsCursor y
    // CameraMapper), así que un tracker basado en ellos apagaría los glifos justo
    // mientras el jugador navega la GUI con el stick.
    public bool Active => ConfiguredStyle != GlyphStyle.Off && provider.IsConnected;

    // Sólo lo usa el self-test del patcher: fuerza una etiqueta conocida para
    // poder distinguir "el parche no quedó puesto" de "no hay mando conectado".
    // Se pone y se saca dentro de StartClientSide, antes de que se dibuje nada.
    internal string? ProbeLabel;

    public void Invalidate()
    {
        dirty = true;
        Revision++;
        Changed?.Invoke();
    }

    // Único punto de decisión para una cápsula de tecla.
    public string? LabelForCombination(KeyCombination? mapping)
    {
        if (ProbeLabel is not null) return ProbeLabel;
        if (!Active || mapping is null) return null;

        // Hotkeys con FLAG de modificador (dropitems = Ctrl+Q, coordinateshud,
        // blockinfohud, macroeditor, hotbarslot11-14): no se tocan. El dibujo
        // agrega las cápsulas "Ctrl"/"Alt"/"Shift" como literales antes de la
        // tecla, y no hay forma de suprimirlas sin reimplementar el método; si
        // sustituyéramos sólo la tecla saldría "[Ctrl] + [D-Down]", que es
        // mentira. Son ~16 de los 148 tags <hk>, y el fallback (la tecla real)
        // sigue siendo verdadero.
        if (mapping.Ctrl || mapping.Alt || mapping.Shift) return null;

        EnsureBuilt();

        if (mapping.IsMouseButton(mapping.KeyCode))
        {
            int button = mapping.KeyCode - KeyCombination.MouseStart;
            return (uint)button < 3 ? byMouse[button]?.Label : null;
        }

        // Para KeyCode == 50 no hace falta nada especial: el corto-circuito
        // `KeyCode == 50 → "Esc"` de KeyCombination ocurre río arriba, pero acá
        // vemos el keycode crudo, y la entrada 50 existe porque
        // Start → escapemenudialog → GlKeys.Escape.
        return byKeyCode.TryGetValue(mapping.KeyCode, out var binding) ? binding.Label : null;
    }

    // Para el ícono de mouse del cartel: 0 = izquierdo, 2 = derecho.
    public string? LabelForMouse(int button)
    {
        if (ProbeLabel is not null) return ProbeLabel;
        if (!Active || (uint)button >= 3) return null;
        EnsureBuilt();
        return byMouse[button]?.Label;
    }

    // ¿Se puede convertir la línea ENTERA del cartel? La unidad de percepción es
    // la línea: "[Shift] + [LT]: Poner en la pila" enseña dos vocabularios en
    // 30 px y se lee peor que la línea entera en teclado. El prefix de drawHelp
    // es el único lugar donde se ve el WorldInteraction completo antes de que se
    // dibuje nada, así que la decisión se toma ahí y vale para toda la línea,
    // incluido el ícono de mouse — que no pasa por el postfix.
    //
    // Se recorre lo mismo que recorre drawHelp: sus hotkeys (los que no existan
    // los saltea igual) y, si la interacción usa un botón de mouse, ese botón.
    public bool WholeLineConvertible(WorldInteraction? wi)
    {
        if (!Active || wi is null) return false;

        string[] codes = wi.HotKeyCodes
                         ?? (wi.HotKeyCode is null ? Array.Empty<string>() : new[] { wi.HotKeyCode });
        foreach (string code in codes)
        {
            if (capi.Input.GetHotKeyByCode(code)?.CurrentMapping is not KeyCombination m) continue;
            if (LabelForCombination(m) is null) return false;
        }

        if (wi.MouseButton == EnumMouseButton.Left  && LabelForMouse(0) is null) return false;
        if (wi.MouseButton == EnumMouseButton.Right && LabelForMouse(2) is null) return false;
        return true;
    }

    // ── diagnóstico ───────────────────────────────────────────────────────────

    public IReadOnlyList<string> Collisions { get { EnsureBuilt(); return collisions; } }

    public IEnumerable<KeyValuePair<int, GlyphBinding>> KeyEntries
    {
        get { EnsureBuilt(); return byKeyCode; }
    }

    public IEnumerable<(int Button, GlyphBinding Binding)> MouseEntries
    {
        get
        {
            EnsureBuilt();
            for (int i = 0; i < byMouse.Length; i++)
                if (byMouse[i] is GlyphBinding b) yield return (i, b);
        }
    }

    // ── construcción ──────────────────────────────────────────────────────────

    private void EnsureBuilt()
    {
        // Además de la bandera, se rearma si cambió el MANDO, aunque nadie haya
        // llamado a Invalidate. Cambiar un Cyclone 2 de modo Xbox a modo PS4 lo
        // re-enumera con otro nombre y otro vendor id, y de eso depende la
        // familia de glifos: si por lo que sea se pierde el flanco de conexión,
        // el mapa quedaría con las etiquetas del mando anterior mientras la
        // propiedad Style ya reporta la familia nueva. Una comparación de string
        // por lookup es barata al lado de una incoherencia de esa clase.
        if (!dirty
            && string.Equals(builtDevice, provider.DeviceName, StringComparison.Ordinal)
            && builtVendor == provider.VendorId)
            return;

        dirty = false;
        builtDevice = provider.DeviceName;
        builtVendor = provider.VendorId;
        byKeyCode.Clear();
        byMouse[0] = byMouse[1] = byMouse[2] = null;
        vetoed.Clear();
        collisions.Clear();

        GlyphStyle style = Style;

        // (1) GATILLOS. TriggerMapper no pasa por hotkeys: llama
        //     ClientMain.UpdateMouseButtonState directo, RT → click izquierdo y
        //     LT → click derecho. El swap lo aplica el driver sobre el
        //     GamepadState antes de todos los mappers, así que se lee en vivo.
        GamepadInput leftClick  = config.SwapTriggers ? GamepadInput.TriggerLeft  : GamepadInput.TriggerRight;
        GamepadInput rightClick = config.SwapTriggers ? GamepadInput.TriggerRight : GamepadInput.TriggerLeft;
        byMouse[0] = new GlyphBinding(GlyphLabels.Label(leftClick,  style), leftClick,  GlyphSource.Trigger);
        byMouse[2] = new GlyphBinding(GlyphLabels.Label(rightClick, style), rightClick, GlyphSource.Trigger);

        // Si el usuario rebindeó primarymouse/secondarymouse a una TECLA, el
        // camino del ícono no se dispara nunca y el hint sale como cápsula de
        // texto. Registramos también ese keycode para no perder la línea.
        ClaimMouseFallback("primarymouse",   leftClick,  style);
        ClaimMouseFallback("secondarymouse", rightClick, style);

        // (2) TOGGLES L3/R3. ToggleManager escribe KEYCODES CRUDOS, no códigos de
        //     hotkey, así que indexar por keycode cubre de una `shift` Y `sneak`
        //     (los dos son LShift) y `ctrl` Y `sprint` (los dos LControl).
        //     Van marcados como toggle: no se mantienen, se togglean.
        Claim((int)GlKeys.LShift,   GamepadInput.StickRightPress, GlyphSource.Toggle, style, isToggle: true);
        Claim((int)GlKeys.LControl, GamepadInput.StickLeftPress,  GlyphSource.Toggle, style, isToggle: true);

        // (3) BOTONES. El override del usuario le gana al default del mod, igual
        //     que en ButtonMapper.ExecuteOrDefault.
        ButtonBindings buttons = buttonBindings();
        foreach (GamepadButton button in ButtonBindings.Configurable)
        {
            if (GlyphInputs.FromButton(button) is not GamepadInput input) continue;

            switch (buttons[button])
            {
                case HotKeyAction hk:
                    ClaimByHotkeyCode(hk.Code, input, GlyphSource.UserButton, style);
                    break;
                case KeyPressAction kp:
                    Claim(kp.KeyCode, input, GlyphSource.UserButton, style);
                    break;
                case HoldKeyAction hd:
                    Claim(hd.KeyCode, input, GlyphSource.UserButton, style);
                    break;
                // OpenLoadedGuiAction y BuiltinAction no tienen una tecla detrás;
                // CompositeAction tiene varias y sería ambiguo. No inventamos.
                case not null:
                    break;
                case null:
                    if (ButtonMapper.DefaultHotkeyCode(button) is string code)
                        ClaimByHotkeyCode(code, input, GlyphSource.DefaultButton, style);
                    else if (button == GamepadButton.DPadDown)
                        Claim(ButtonMapper.SitDefaultKeyCode, input, GlyphSource.DefaultButton, style);
                    break;
            }
        }

        // (4) A = saltar SIEMPRE, aunque A tenga override: MovementMapper lo
        //     proyecta aparte de ButtonMapper.
        ClaimByHotkeyCode("jump", GamepadInput.FaceSouth, GlyphSource.Movement, style);

        // (5) RUEDA (LB + dirección). Se muestra "LB" a secas: el índice del slot
        //     no lo ve el jugador en la rueda, así que "LB+3" no sería accionable.
        if (config.GlyphWheel)
        {
            SlotBindings wheel = wheelBindings();
            for (int slot = 0; slot < SlotBindings.SlotCount; slot++)
                ClaimWheelSlot(wheel[slot], style);
        }
    }

    private void ClaimMouseFallback(string hotkeyCode, GamepadInput input, GlyphStyle style)
    {
        if (capi.Input.GetHotKeyByCode(hotkeyCode)?.CurrentMapping is not KeyCombination m) return;
        if (m.Ctrl || m.Alt || m.Shift) return;
        if (m.IsMouseButton(m.KeyCode)) return;      // camino normal, ya cubierto
        Claim(m.KeyCode, input, GlyphSource.Trigger, style);
    }

    private void ClaimByHotkeyCode(string? hotkeyCode, GamepadInput input,
                                   GlyphSource source, GlyphStyle style)
    {
        if (string.IsNullOrEmpty(hotkeyCode)) return;
        if (capi.Input.GetHotKeyByCode(hotkeyCode)?.CurrentMapping is not KeyCombination m) return;
        if (m.Ctrl || m.Alt || m.Shift) return;
        if (m.IsMouseButton(m.KeyCode)) return;
        Claim(m.KeyCode, input, source, style);
    }

    private void ClaimWheelSlot(IGameAction? action, GlyphStyle style)
    {
        switch (action)
        {
            case HotKeyAction hk:
                ClaimByHotkeyCode(hk.Code, GamepadInput.ShoulderLeft, GlyphSource.Wheel, style);
                break;
            case KeyPressAction kp:
                Claim(kp.KeyCode, GamepadInput.ShoulderLeft, GlyphSource.Wheel, style);
                break;
            case HoldKeyAction hd:
                Claim(hd.KeyCode, GamepadInput.ShoulderLeft, GlyphSource.Wheel, style);
                break;
        }
    }

    // Regla anti-colisión: si dos fuentes del MISMO nivel reclaman el mismo
    // keycode con etiquetas distintas, ese keycode vuelve a vanilla y queda
    // vetado para el resto de la construcción. Un hint verdadero de teclado vale
    // más que un glifo ambiguo: el glifo ambiguo no es información incompleta,
    // es información falsa.
    private void Claim(int keyCode, GamepadInput input, GlyphSource source,
                       GlyphStyle style, bool isToggle = false)
    {
        if (keyCode < 0 || vetoed.Contains(keyCode)) return;

        var candidate = new GlyphBinding(GlyphLabels.Label(input, style, isToggle), input, source);

        if (!byKeyCode.TryGetValue(keyCode, out GlyphBinding previous))
        {
            byKeyCode[keyCode] = candidate;
            return;
        }
        if (previous.Label == candidate.Label) return;
        if (candidate.Priority < previous.Priority) { byKeyCode[keyCode] = candidate; return; }
        if (candidate.Priority > previous.Priority) return;

        byKeyCode.Remove(keyCode);
        vetoed.Add(keyCode);
        collisions.Add(
            $"{(GlKeys)keyCode} ({keyCode}): {previous.Label} ({previous.Source}) vs " +
            $"{candidate.Label} ({candidate.Source}) → se muestra la tecla");
    }
}
