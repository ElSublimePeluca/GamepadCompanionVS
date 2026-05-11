using System;
using System.Collections.Generic;
using System.Linq;
using GamepadCompanion.Actions;
using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace GamepadCompanion.Gui;

// Editor de configuración del mod. Dos tabs:
//   - Rueda:        editor de los 8 slots del radial.
//   - Sensibilidad: dead zone, sensibilidad de cámara, invertir pitch.
// onChanged se invoca tras cualquier cambio (slot o sensibilidad). El
// ModSystem lo cablea con StoreModConfig para persistir en JSON.
public sealed class ConfigDialog : GuiDialog
{
    private const double DialogW    = 480;
    private const double DialogH    = 580;
    private const double TitleH     = 30;
    private const double TabBarH    = 30;
    private const double FooterH    = 36;
    private const double Margin     = 16;

    // Tab body: layout para "Rueda". 12 filas a 28px = 336px, dejando
    // espacio dentro de DialogH=580 para el botón Restaurar y el footer.
    private const double WheelRowH       = 26;
    private const double WheelRowGap     = 2;
    private const double WheelSlotLabelW = 70;
    private const double WheelSlotBtnW   = 360;
    private const double WheelResetGap   = 12;
    private const double WheelResetH     = 32;

    // Tab body: layout para "Sensibilidad".
    private const double SensRowH       = 32;
    private const double SensRowGap     = 10;
    private const double SensLabelW     = 140;
    private const double SensControlW   = 280;

    // Tab body: layout para "Botones". 12 filas a 28px = 336px que justo
    // entra en el body disponible (DialogH - title - tabbar - footer - margins).
    private const double BtnRowH        = 26;
    private const double BtnRowGap      = 2;
    private const double BtnLabelW      = 100;
    private const double BtnPickerW     = 260;

    private const int TabWheel = 0;
    private const int TabSensitivity = 1;
    private const int TabButtons = 2;

    private readonly SlotBindings bindings;
    private readonly ButtonBindings buttonBindings;
    private readonly GamepadCompanionConfig config;
    private readonly Action? onChanged;

    private int currentTab = TabWheel;

    // En el picker que muestra ConfigDialog para elegir la acción de un
    // slot, agregamos dos entries virtuales después de "ninguno":
    //   - idx 1: Combinación (abre CompositeBuilderDialog)
    //   - idx 2: Tecla individual (abre KeyCaptureDialog)
    // Los base entries (hotkey/dialog/builtin) empiezan en idx 3.
    // El builder de combinaciones recibe solo los base — no se anidan
    // composites ni se anidan key presses.
    // Sin emojis porque la fuente Cairo no tiene glifos extendidos.
    private const string CompositeEntryLabel = "[Combinar varias acciones...]";
    private const string KeyPressEntryLabel  = "[Asignar una tecla individual...]";
    private const int    MetaEntryCount      = 2; // composite + keypress

    private enum EntryKind { None, HotKey, OpenDialog, Builtin }
    private string[] entryCodes       = Array.Empty<string>();
    private string[] entryNames       = Array.Empty<string>();
    private EntryKind[] entryKinds    = Array.Empty<EntryKind>();
    private string[] entryDialogTypes = Array.Empty<string>();

    public override string ToggleKeyCombinationCode => null!;

    public ConfigDialog(
        ICoreClientAPI capi,
        SlotBindings bindings,
        ButtonBindings buttonBindings,
        GamepadCompanionConfig config,
        Action? onChanged = null) : base(capi)
    {
        this.bindings = bindings;
        this.buttonBindings = buttonBindings;
        this.config = config;
        this.onChanged = onChanged;
        BuildEntryList();
        Compose();
    }

    private void BuildEntryList()
    {
        var dialogByToggle = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in capi.Gui.LoadedGuis)
        {
            if (d is null) continue;
            var code = d.ToggleKeyCombinationCode;
            if (string.IsNullOrEmpty(code)) continue;
            dialogByToggle.TryAdd(code, d.GetType().Name);
        }

        var list = capi.Input.HotKeys
            .Select(kv =>
            {
                var hk = kv.Value;
                string code = kv.Key;
                string name = hk.Name ?? code;
                if (hk.Handler is not null)
                    return (Code: code, Name: name,
                            Kind: EntryKind.HotKey, DialogType: "");
                if (dialogByToggle.TryGetValue(code, out var dialogType))
                    return (Code: code, Name: name,
                            Kind: EntryKind.OpenDialog, DialogType: dialogType);
                return (Code: code, Name: name,
                        Kind: EntryKind.None, DialogType: "");
            })
            .Where(t => t.Kind != EntryKind.None);

        // BuiltinActions del mod (drop item, dismiss, hotbar nav, etc.)
        // Las prefijamos con "[Mod] " para distinguirlas de las hotkeys
        // vanilla en la lista ordenada.
        var builtins = BuiltinAction.Catalog
            .Select(b => (Code: b.Code, Name: $"[Mod] {b.Label}",
                          Kind: EntryKind.Builtin, DialogType: ""));

        list = list.Concat(builtins)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var codes = new List<string> { "" };
        var names = new List<string> { "— (ninguno)" };
        var kinds = new List<EntryKind> { EntryKind.None };
        var types = new List<string> { "" };
        foreach (var (code, name, kind, dialogType) in list)
        {
            codes.Add(code);
            names.Add(name);
            kinds.Add(kind);
            types.Add(dialogType);
        }
        entryCodes = codes.ToArray();
        entryNames = names.ToArray();
        entryKinds = kinds.ToArray();
        entryDialogTypes = types.ToArray();
    }

    private void Compose()
    {
        var dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle);
        var bgBounds = ElementBounds.Fixed(0, 0, DialogW, DialogH);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        var titleBarBounds = ElementBounds.Fixed(0, 0, DialogW, TitleH);
        var tabBarBounds = ElementBounds.Fixed(
            Margin, TitleH + Margin, DialogW - 2 * Margin, TabBarH);

        var tabs = new[]
        {
            new GuiTab { Name = "Rueda",        DataInt = TabWheel,
                         Active = currentTab == TabWheel },
            new GuiTab { Name = "Botones",      DataInt = TabButtons,
                         Active = currentTab == TabButtons },
            new GuiTab { Name = "Sensibilidad", DataInt = TabSensitivity,
                         Active = currentTab == TabSensitivity },
        };

        var compo = capi.Gui
            .CreateCompo("gpcompanion-config", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar("GamepadCompanion - Configuración",
                               OnTitleBarClose, bounds: titleBarBounds)
            .BeginChildElements(bgBounds)
            .AddHorizontalTabs(tabs, tabBarBounds, OnTabClicked,
                               CairoFont.WhiteSmallText(),
                               CairoFont.WhiteSmallText(), "tabs");

        double bodyY = TitleH + Margin + TabBarH + Margin;
        switch (currentTab)
        {
            case TabWheel:       ComposeWheelTab(compo, bodyY); break;
            case TabButtons:     ComposeButtonsTab(compo, bodyY); break;
            case TabSensitivity: ComposeSensitivityTab(compo, bodyY); break;
        }

        var closeBounds = ElementBounds.Fixed(
            Margin, DialogH - FooterH - Margin,
            DialogW - 2 * Margin, FooterH);
        compo.AddSmallButton("Cerrar",
                             () => { TryClose(); return true; },
                             closeBounds);

        compo.EndChildElements();
        SingleComposer = compo.Compose();

        // El control de tabs requiere setear el activo después del Compose
        // (DataInt en GuiTab es solo seed; el state activo lo guarda el
        // elemento internamente).
        SingleComposer.GetHorizontalTabs("tabs").activeElement = currentTab;
    }

    private void ComposeWheelTab(GuiComposer compo, double startY)
    {
        double y = startY;
        for (int i = 0; i < SlotBindings.SlotCount; i++)
        {
            int slot = i;
            var labelBounds = ElementBounds
                .Fixed(Margin, y + 4, WheelSlotLabelW, WheelRowH);
            var btnBounds = ElementBounds
                .Fixed(Margin + WheelSlotLabelW + 8, y, WheelSlotBtnW, WheelRowH);

            string slotLabel = bindings[slot]?.Label ?? "— (ninguno)";

            compo.AddStaticText($"Slot {slot + 1}",
                                CairoFont.WhiteSmallText(), labelBounds);
            compo.AddSmallButton(slotLabel,
                                 () => { OpenPicker(slot); return true; },
                                 btnBounds, EnumButtonStyle.Normal);

            y += WheelRowH + WheelRowGap;
        }

        // Botón para restaurar el layout default de la rueda. Útil cuando
        // expandimos SlotCount o cambian los defaults y el config viejo
        // ya quedó persistido — sin esto el usuario tendría que borrar el
        // JSON a mano.
        var resetBounds = ElementBounds.Fixed(
            Margin, y + WheelResetGap,
            DialogW - 2 * Margin, WheelResetH);
        compo.AddSmallButton("Restaurar predeterminados",
                             () => { RestoreWheelDefaults(); return true; },
                             resetBounds, EnumButtonStyle.Normal);
    }

    private void RestoreWheelDefaults()
    {
        var defaults = SlotBindings.BuildDefault();
        for (int i = 0; i < SlotBindings.SlotCount; i++)
            bindings.Set(i, defaults[i]);
        onChanged?.Invoke();
        Compose();
    }

    private void ComposeButtonsTab(GuiComposer compo, double startY)
    {
        double y = startY;
        foreach (var btn in ButtonBindings.Configurable)
        {
            var thisBtn = btn;   // capture
            var labelBounds = ElementBounds
                .Fixed(Margin, y + 4, BtnLabelW, BtnRowH);
            var btnBounds = ElementBounds
                .Fixed(Margin + BtnLabelW + 8, y, BtnPickerW, BtnRowH);

            string current = buttonBindings[thisBtn]?.Label
                             ?? "— predeterminado —";

            compo.AddStaticText(ButtonDisplayName(thisBtn),
                                CairoFont.WhiteSmallText(), labelBounds);
            compo.AddSmallButton(current,
                                 () => { OpenButtonPicker(thisBtn); return true; },
                                 btnBounds, EnumButtonStyle.Normal);

            y += BtnRowH + BtnRowGap;
        }
    }

    // Nombres en inglés cortos. Cairo no renderiza las flechas
    // unicode (salen como cuadrados, igual issue que el emoji 🔗).
    private static string ButtonDisplayName(Input.GamepadButton btn) =>
        btn switch
        {
            Input.GamepadButton.A          => "A",
            Input.GamepadButton.B          => "B",
            Input.GamepadButton.X          => "X",
            Input.GamepadButton.Y          => "Y",
            Input.GamepadButton.Back       => "Back / Select",
            Input.GamepadButton.Start      => "Start / Menu",
            Input.GamepadButton.DPadUp     => "DPad Up",
            Input.GamepadButton.DPadDown   => "DPad Down",
            Input.GamepadButton.DPadLeft   => "DPad Left",
            Input.GamepadButton.DPadRight  => "DPad Right",
            _                              => btn.ToString(),
        };

    private void OpenButtonPicker(Input.GamepadButton btn)
    {
        var pickerNames = BuildPickerNames();
        int currentIdx = CurrentButtonPickerIndex(btn);
        var picker = new HotKeyPickerDialog(
            capi,
            $"{ButtonDisplayName(btn)}: elegir acción",
            pickerNames,
            currentIdx,
            pickedIdx => ApplyButtonPick(btn, pickedIdx),
            // En la tab Botones, "vaciar" significa volver al
            // comportamiento default hardcoded del ButtonMapper
            // (drop/dismiss en B, etc.), no "no hace nada".
            clearButtonLabel: "Restaurar predeterminado");
        picker.TryOpen();
    }

    private void ApplyButtonPick(Input.GamepadButton btn, int pickerIdx)
    {
        if (pickerIdx <= 0)
        {
            buttonBindings.Set(btn, null);
            onChanged?.Invoke();
            Compose();
            return;
        }
        if (pickerIdx == 1)
        {
            OpenButtonCompositeBuilder(btn);
            return;
        }
        if (pickerIdx == 2)
        {
            OpenKeyCapture(action =>
            {
                if (action is null) return;
                buttonBindings.Set(btn, action);
                onChanged?.Invoke();
                Compose();
            });
            return;
        }
        int baseIdx = pickerIdx - MetaEntryCount;
        buttonBindings.Set(btn, BaseEntryToAction(baseIdx));
        onChanged?.Invoke();
        Compose();
    }

    private void OpenButtonCompositeBuilder(Input.GamepadButton btn)
    {
        var initial = buttonBindings[btn] as CompositeAction;
        var builder = new CompositeBuilderDialog(
            capi,
            entryNames,
            BaseEntryToAction,
            BaseIndexOfAction,
            initial,
            result =>
            {
                buttonBindings.Set(btn, result);
                onChanged?.Invoke();
                Compose();
            });
        builder.TryOpen();
    }

    private int CurrentButtonPickerIndex(Input.GamepadButton btn)
    {
        var action = buttonBindings[btn];
        if (action is CompositeAction) return 1;
        if (action is KeyPressAction) return 2;
        int baseIdx = action is null ? 0 : BaseIndexOfAction(action);
        return baseIdx == 0 ? 0 : baseIdx + MetaEntryCount;
    }

    private void ComposeSensitivityTab(GuiComposer compo, double startY)
    {
        double y = startY;

        // Yaw sensitivity
        AddSensitivityRow(compo, ref y, "Sensibilidad horizontal", "yaw",
            (int)config.YawSensitivity, 100, 5000, 50, "",
            v => { config.YawSensitivity = v; onChanged?.Invoke(); return true; });

        // Pitch sensitivity
        AddSensitivityRow(compo, ref y, "Sensibilidad vertical", "pitch",
            (int)config.PitchSensitivity, 100, 5000, 50, "",
            v => { config.PitchSensitivity = v; onChanged?.Invoke(); return true; });

        // Dead zone (escalado *100 para que el slider trabaje en int)
        AddSensitivityRow(compo, ref y, "Dead zone (%)", "deadzone",
            (int)(config.Deadzone * 100), 0, 50, 1, "%",
            v => { config.Deadzone = v / 100f; onChanged?.Invoke(); return true; });

        // Invert pitch (switch)
        var labelBounds = ElementBounds
            .Fixed(Margin, y + 6, SensLabelW, SensRowH);
        var switchBounds = ElementBounds
            .Fixed(Margin + SensLabelW + 8, y, 30, 30);
        compo.AddStaticText("Invertir pitch",
                            CairoFont.WhiteSmallText(), labelBounds);
        compo.AddSwitch(v =>
            {
                config.InvertPitch = v;
                onChanged?.Invoke();
            },
            switchBounds, "invert");
        y += SensRowH + SensRowGap;
    }

    private void AddSensitivityRow(
        GuiComposer compo, ref double y,
        string label, string key,
        int currentValue, int minValue, int maxValue, int step, string unit,
        ActionConsumable<int> onNewValue)
    {
        var labelBounds = ElementBounds
            .Fixed(Margin, y + 6, SensLabelW, SensRowH);
        var sliderBounds = ElementBounds
            .Fixed(Margin + SensLabelW + 8, y, SensControlW, SensRowH);

        compo.AddStaticText(label, CairoFont.WhiteSmallText(), labelBounds);
        compo.AddSlider(onNewValue, sliderBounds, key);
        y += SensRowH + SensRowGap;
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        // Switch y sliders requieren SetValue/SetValues post-compose.
        ApplySensitivityWidgetState();
    }

    private void ApplySensitivityWidgetState()
    {
        if (currentTab != TabSensitivity) return;
        SingleComposer.GetSlider("yaw")
            .SetValues((int)config.YawSensitivity, 100, 5000, 50, "");
        SingleComposer.GetSlider("pitch")
            .SetValues((int)config.PitchSensitivity, 100, 5000, 50, "");
        SingleComposer.GetSlider("deadzone")
            .SetValues((int)(config.Deadzone * 100), 0, 50, 1, "%");
        SingleComposer.GetSwitch("invert").SetValue(config.InvertPitch);
    }

    private void OnTabClicked(int idx)
    {
        currentTab = idx;
        Compose();
        ApplySensitivityWidgetState();
    }

    private void OpenPicker(int slot)
    {
        // Picker para slot: incluye la entrada virtual "Combinación".
        var pickerNames = BuildPickerNames();
        int currentPickerIdx = CurrentPickerIndex(slot);
        var picker = new HotKeyPickerDialog(
            capi,
            $"Slot {slot + 1}: elegir acción",
            pickerNames,
            currentPickerIdx,
            pickedIdx => ApplyPick(slot, pickedIdx));
        picker.TryOpen();
    }

    // Construye [ninguno, composite, base[1..]] para el picker del slot.
    private string[] BuildPickerNames()
    {
        var arr = new string[entryNames.Length + MetaEntryCount];
        arr[0] = entryNames[0];               // "ninguno"
        arr[1] = CompositeEntryLabel;
        arr[2] = KeyPressEntryLabel;
        for (int i = 1; i < entryNames.Length; i++)
            arr[i + MetaEntryCount] = entryNames[i];
        return arr;
    }

    // Traduce: pickerIdx 0=none, 1=composite, 2=keypress, 3+ = base[idx-2].
    private void ApplyPick(int slot, int pickerIdx)
    {
        if (pickerIdx <= 0)
        {
            bindings.Set(slot, null);
            onChanged?.Invoke();
            Compose();
            return;
        }
        if (pickerIdx == 1)
        {
            OpenCompositeBuilder(slot);
            return;
        }
        if (pickerIdx == 2)
        {
            OpenKeyCapture(action =>
            {
                if (action is null) return;     // cancel
                bindings.Set(slot, action);
                onChanged?.Invoke();
                Compose();
            });
            return;
        }
        int baseIdx = pickerIdx - MetaEntryCount;
        bindings.Set(slot, BaseEntryToAction(baseIdx));
        onChanged?.Invoke();
        Compose();
    }

    private void OpenKeyCapture(Action<KeyPressAction?> onCaptured)
    {
        new KeyCaptureDialog(capi, onCaptured).TryOpen();
    }

    private void OpenCompositeBuilder(int slot)
    {
        var initial = bindings[slot] as CompositeAction;
        var builder = new CompositeBuilderDialog(
            capi,
            entryNames,                       // base, sin composite
            BaseEntryToAction,
            BaseIndexOfAction,
            initial,
            result =>
            {
                bindings.Set(slot, result);
                onChanged?.Invoke();
                Compose();
            });
        builder.TryOpen();
    }

    private IGameAction? BaseEntryToAction(int baseIdx)
    {
        if (baseIdx <= 0 || baseIdx >= entryCodes.Length) return null;
        string code = entryCodes[baseIdx];
        string name = entryNames[baseIdx];
        return entryKinds[baseIdx] switch
        {
            EntryKind.HotKey     => new HotKeyAction(code, name),
            EntryKind.OpenDialog => new OpenLoadedGuiAction(
                                       entryDialogTypes[baseIdx], name),
            EntryKind.Builtin    => new BuiltinAction(code),
            _                    => null,
        };
    }

    private int BaseIndexOfAction(IGameAction action)
    {
        // Matchear por (Kind, Code) — el code solo no basta porque
        // entrenamiento de hotkey y builtin podrían coincidir en string.
        EntryKind matchKind;
        string? matchCode;
        switch (action)
        {
            case HotKeyAction hk:
                matchKind = EntryKind.HotKey;
                matchCode = hk.Code;
                break;
            case OpenLoadedGuiAction og:
                matchKind = EntryKind.OpenDialog;
                matchCode = FindCodeByDialogType(og.DialogTypeName);
                break;
            case BuiltinAction bi:
                matchKind = EntryKind.Builtin;
                matchCode = bi.Code;
                break;
            default:
                return 0;
        }
        if (matchCode is null) return 0;
        for (int i = 1; i < entryCodes.Length; i++)
            if (entryKinds[i] == matchKind && entryCodes[i] == matchCode)
                return i;
        return 0;
    }

    private int CurrentPickerIndex(int slot)
    {
        var action = bindings[slot];
        if (action is CompositeAction) return 1;
        if (action is KeyPressAction) return 2;
        int baseIdx = action is null ? 0 : BaseIndexOfAction(action);
        return baseIdx == 0 ? 0 : baseIdx + MetaEntryCount;
    }

    private string? FindCodeByDialogType(string dialogType)
    {
        for (int i = 1; i < entryCodes.Length; i++)
            if (entryKinds[i] == EntryKind.OpenDialog &&
                entryDialogTypes[i] == dialogType)
                return entryCodes[i];
        return null;
    }

    private void OnTitleBarClose() => TryClose();
}
