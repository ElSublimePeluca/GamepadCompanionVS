using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using GamepadCompanion.Actions;
using GamepadCompanion.Glyphs;
using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace GamepadCompanion.Gui;

// Editor de configuración del mod. Cuatro tabs:
//   - Rueda:        editor de los 12 slots del radial.
//   - Botones:      override de la acción de cada botón.
//   - Sensibilidad: dead zone, sensibilidad de cámara, invertir pitch.
//   - Ayudas:       familia de glifos de los hints del juego, con vista previa.
// onChanged se invoca tras cualquier cambio (slot o sensibilidad). El
// ModSystem lo cablea con StoreModConfig para persistir en JSON.
public sealed class ConfigDialog : GuiDialog
{
    private const double DialogW    = 480;
    private const double DialogH    = 580;
    // El fondo del dialog reserva exactamente GuiStyle.TitleBarHeight arriba
    // (GuiElementDialogBackground), así que la barra tiene que medir eso o
    // queda una costura de 1px entre la barra y el cuerpo.
    private static readonly double TitleH = GuiStyle.TitleBarHeight;
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
    // 185/250 y no 140/280: "Sensibilidad horizontal" (173) e "Intercambiar
    // LT/RT" (147) no entraban en 140, y AddStaticText no recorta sino que
    // ENVUELVE, así que la segunda línea se comía la fila de abajo. Venía así
    // desde antes de la tab Ayudas, en los tres idiomas. El control sigue
    // entrando: 16 + 185 + 8 + 250 = 459, contra 464 de borde útil.
    private const double SensLabelW     = 185;
    private const double SensControlW   = 250;
    private const double SensResetGap   = 16;
    private const double SensResetH     = 32;

    // Tab body: layout para "Botones". Una fila de layout arriba y 12 de
    // botones (A/B/X/Y, los dos bumpers, Back/Start y el D-pad) a 28px = 336px,
    // que entran en el body disponible (DialogH - title - tabbar - footer -
    // margins = 435).
    private const double BtnRowH        = 26;
    private const double BtnRowGap      = 2;
    private const double BtnLabelW      = 100;
    private const double BtnPickerW     = 260;
    private const double BtnLayoutGap   = 14;

    // Tab body: layout para "Ayudas". La columna de etiqueta es ancha a
    // propósito: AddStaticText no recorta, ENVUELVE, y una etiqueta que no entra
    // se come la fila de abajo. Los anchos salen medidos en los tres idiomas con
    // `dotnet run --project tools/gpclab -- labels`; si tocás un texto de esta
    // tab, volvé a correrlo.
    private const double HintRowH     = 30;
    private const double HintRowGap   = 10;
    private const double HintLabelW   = 210;
    private const double HintControlW = 230;
    private const double HintPreviewH = 46;

    private const int TabWheel = 0;
    private const int TabSensitivity = 1;
    private const int TabButtons = 2;
    private const int TabHints = 3;

    private readonly SlotBindings bindings;
    private readonly ButtonBindings buttonBindings;
    private readonly GamepadCompanionConfig config;
    // Puede ser null: si el subsistema de glifos no arrancó, el resto del
    // diálogo tiene que seguir funcionando igual.
    private readonly GlyphSession? glyphs;
    private readonly Action? onChanged;
    // Cambiar de layout toca más cosas que el JSON (el botón de la rueda, los
    // glifos), así que lo aplica el ModSystem y el diálogo sólo lo pide.
    private readonly Action<GamepadLayoutKind>? onLayoutChanged;

    private int currentTab = TabWheel;

    // En el picker que muestra ConfigDialog para elegir la acción de un
    // slot, agregamos tres entries virtuales después de "ninguno":
    //   - idx 1: Combinación (abre CompositeBuilderDialog)
    //   - idx 2: Tecla individual (abre KeyCaptureDialog, tap)
    //   - idx 3: Mantener tecla (abre KeyCaptureDialog en modo hold)
    // Los base entries (hotkey/dialog/builtin) empiezan en idx 4.
    // "Mantener tecla" se ofrece también en la rueda por simetría de índices,
    // aunque ahí un slot no tiene edge de release y degrada a tap.
    // El builder de combinaciones recibe solo los base — no se anidan
    // composites ni se anidan key presses.
    // Sin emojis porque la fuente Cairo no tiene glifos extendidos.
    private static string CompositeEntryLabel =>
        Lang.Get("gamepadcompanion:picker-composite-entry");
    private static string KeyPressEntryLabel =>
        Lang.Get("gamepadcompanion:picker-keypress-entry");
    private static string HoldKeyEntryLabel =>
        Lang.Get("gamepadcompanion:picker-holdkey-entry");
    private const int MetaEntryCount = 3; // composite + keypress + holdkey

    private enum EntryKind { None, HotKey, OpenDialog, Builtin }
    private string[] entryCodes       = Array.Empty<string>();
    private string[] entryNames       = Array.Empty<string>();
    private EntryKind[] entryKinds    = Array.Empty<EntryKind>();
    private string[] entryDialogTypes = Array.Empty<string>();

    public override string ToggleKeyCombinationCode => null!;

    // internal y no public: recibe la GlyphSession, que es interna. El diálogo
    // sólo lo construye el ModSystem.
    internal ConfigDialog(
        ICoreClientAPI capi,
        SlotBindings bindings,
        ButtonBindings buttonBindings,
        GamepadCompanionConfig config,
        GlyphSession? glyphs = null,
        Action? onChanged = null,
        Action<GamepadLayoutKind>? onLayoutChanged = null) : base(capi)
    {
        this.bindings = bindings;
        this.buttonBindings = buttonBindings;
        this.config = config;
        this.glyphs = glyphs;
        this.onChanged = onChanged;
        this.onLayoutChanged = onLayoutChanged;
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
        var names = new List<string> { Lang.Get("gamepadcompanion:entry-none") };
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
        // Sin FitToChildren a propósito: DialogW/alto ya incluyen los
        // márgenes, y shrink-wrapear el fondo al contenido le comía el
        // margen derecho e inferior mientras el title bar conservaba el
        // ancho declarado — el fondo quedaba más angosto que la barra y
        // asomaba por la derecha (issue #7).
        var bgBounds = ElementBounds.Fixed(0, 0, DialogW, DialogH);

        var titleBarBounds = ElementBounds.Fixed(0, 0, DialogW, TitleH);
        var tabBarBounds = ElementBounds.Fixed(
            Margin, TitleH + Margin, DialogW - 2 * Margin, TabBarH);

        var tabs = new[]
        {
            new GuiTab { Name = Lang.Get("gamepadcompanion:tab-wheel"),
                         DataInt = TabWheel,
                         Active = currentTab == TabWheel },
            new GuiTab { Name = Lang.Get("gamepadcompanion:tab-buttons"),
                         DataInt = TabButtons,
                         Active = currentTab == TabButtons },
            new GuiTab { Name = Lang.Get("gamepadcompanion:tab-sensitivity"),
                         DataInt = TabSensitivity,
                         Active = currentTab == TabSensitivity },
            new GuiTab { Name = Lang.Get("gamepadcompanion:tab-hints"),
                         DataInt = TabHints,
                         Active = currentTab == TabHints },
        };

        var compo = capi.Gui
            .CreateCompo("gpcompanion-config", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(Lang.Get("gamepadcompanion:config-title"),
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
            case TabHints:       ComposeHintsTab(compo, bodyY); break;
        }

        var closeBounds = ElementBounds.Fixed(
            Margin, DialogH - FooterH - Margin,
            DialogW - 2 * Margin, FooterH);
        compo.AddSmallButton(Lang.Get("gamepadcompanion:close"),
                             () => { TryClose(); return true; },
                             closeBounds);

        compo.EndChildElements();
        SingleComposer = compo.Compose();

        // El control de tabs requiere setear el activo después del Compose
        // (DataInt en GuiTab es solo seed; el state activo lo guarda el
        // elemento internamente).
        //
        // Ojo: GuiElementHorizontalTabs.activeElement es la POSICIÓN en el
        // array de tabs, no el DataInt. SetValue(i) hace `handler(tabs[i]
        // .DataInt)` pero guarda `activeElement = i`. Nuestro orden visual
        // (Rueda, Botones, Sensibilidad) no coincide con el valor de las
        // constantes (Sensibilidad=1, Botones=2), así que asignar currentTab
        // directo resaltaba la tab equivocada: elegir Sensibilidad mostraba
        // el contenido correcto pero dejaba iluminada Botones. Issue #1.
        // El switch de la tab Ayudas necesita SetValue post-compose, igual que los
        // de Sensibilidad. Va acá adentro y no en OnGuiOpened porque a esta tab
        // se la recompone también al elegir un estilo en el picker, y ahí el
        // switch volvería a arrancar apagado.
        ApplyHintsWidgetState();

        int activeIdx = Array.FindIndex(tabs, t => t.DataInt == currentTab);
        SingleComposer.GetHorizontalTabs("tabs").activeElement =
            activeIdx < 0 ? 0 : activeIdx;
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

            // Los labels vienen de hotkeys de terceros y de composites de
            // hasta 4 pasos: sin recortar, el botón crece solo y se sale del
            // panel. Ver GuiTextFit.
            string slotLabel = GuiTextFit.EllipsizeButton(
                bindings[slot]?.Label ?? Lang.Get("gamepadcompanion:entry-none"),
                WheelSlotBtnW);

            compo.AddStaticText(Lang.Get("gamepadcompanion:slot-label", slot + 1),
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
        compo.AddSmallButton(Lang.Get("gamepadcompanion:restore-defaults"),
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

        // Primero el preset: es el que decide qué dice "— por defecto —" en
        // todas las filas de abajo, así que leerlo primero es lo que hace que
        // el resto de la tab se entienda.
        compo.AddStaticText(Lang.Get("gamepadcompanion:layout-label"),
                            CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(Margin, y + 4, BtnLabelW, BtnRowH));
        compo.AddSmallButton(
            GuiTextFit.EllipsizeButton(GamepadLayout.NameOf(CurrentLayoutKind), BtnPickerW),
            () => { CycleLayout(); return true; },
            ElementBounds.Fixed(Margin + BtnLabelW + 8, y, BtnPickerW, BtnRowH),
            EnumButtonStyle.Normal);
        y += BtnRowH + BtnLayoutGap;

        foreach (var btn in ButtonBindings.Configurable)
        {
            var thisBtn = btn;   // capture
            var labelBounds = ElementBounds
                .Fixed(Margin, y + 4, BtnLabelW, BtnRowH);
            var valueBounds = ElementBounds
                .Fixed(Margin + BtnLabelW + 8, y, BtnPickerW, BtnRowH);

            compo.AddStaticText(ButtonDisplayName(thisBtn),
                                CairoFont.WhiteSmallText(), labelBounds);

            // El botón que el layout usa para la rueda no es asignable, pero la
            // fila se muestra igual: es la única manera de ver DÓNDE quedó la
            // rueda después de cambiar de preset. Texto y no botón, para que se
            // lea como "acá no hay nada que elegir".
            if (buttonBindings.Layout?.IsWheel(thisBtn) == true)
            {
                compo.AddStaticText(Lang.Get("gamepadcompanion:button-wheel"),
                                    CairoFont.WhiteDetailText(),
                                    ElementBounds.Fixed(valueBounds.fixedX, y + 5,
                                                        BtnPickerW, BtnRowH));
                y += BtnRowH + BtnRowGap;
                continue;
            }

            compo.AddSmallButton(GuiTextFit.EllipsizeButton(ButtonValueLabel(thisBtn),
                                                           BtnPickerW),
                                 () => { OpenButtonPicker(thisBtn); return true; },
                                 valueBounds, EnumButtonStyle.Normal);

            y += BtnRowH + BtnRowGap;
        }
    }

    private GamepadLayoutKind CurrentLayoutKind =>
        buttonBindings.Layout?.Kind ?? GamepadLayout.Parse(config.Layout);

    // Lo que dice la fila: el binding del usuario, o el default con el nombre
    // de lo que hace. Decir sólo "— por defecto —" era justamente lo que no
    // dejaba ver qué cambió al pasar de un layout al otro.
    private string ButtonValueLabel(Input.GamepadButton btn)
    {
        if (buttonBindings[btn] is { } user) return user.Label;

        string? label = buttonBindings.Layout?.Default(btn)?.Label;
        return label is null
            ? Lang.Get("gamepadcompanion:button-default")
            : Lang.Get("gamepadcompanion:button-default-of", label);
    }

    private static readonly GamepadLayoutKind[] LayoutOrder =
        { GamepadLayoutKind.Modern, GamepadLayoutKind.Classic };

    // El botón cicla entre los presets en vez de abrir un picker: con dos
    // opciones, una lista de un solo ítem (el picker esconde el índice 0, que
    // usa como "quitar") no diría nada, y el efecto se ve solo — las 12 filas
    // de abajo se recomponen mostrando el default nuevo de cada botón. Y es
    // reversible con otro click, así que no hace falta confirmar.
    private void CycleLayout()
    {
        int idx = Array.IndexOf(LayoutOrder, CurrentLayoutKind);
        var next = LayoutOrder[(idx + 1) % LayoutOrder.Length];
        onLayoutChanged?.Invoke(next);
        Compose();
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
            // LB/RB y no "LeftBumper": es lo que dicen los glifos del mod y lo
            // que está impreso en el mando.
            Input.GamepadButton.LeftBumper  => "LB",
            Input.GamepadButton.RightBumper => "RB",
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
            Lang.Get("gamepadcompanion:pick-action-title", ButtonDisplayName(btn)),
            pickerNames,
            currentIdx,
            pickedIdx => ApplyButtonPick(btn, pickedIdx),
            // En la tab Botones, "vaciar" significa volver al
            // comportamiento default hardcoded del ButtonMapper
            // (drop/dismiss en B, etc.), no "no hace nada".
            clearButtonLabel: Lang.Get("gamepadcompanion:restore-default"));
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
        if (pickerIdx == 2 || pickerIdx == 3)
        {
            OpenKeyCapture(action =>
            {
                if (action is null) return;
                buttonBindings.Set(btn, action);
                onChanged?.Invoke();
                Compose();
            }, holdMode: pickerIdx == 3);
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
        if (action is HoldKeyAction) return 3;
        int baseIdx = action is null ? 0 : BaseIndexOfAction(action);
        return baseIdx == 0 ? 0 : baseIdx + MetaEntryCount;
    }

    // Tab "Ayudas": qué familia de íconos se muestra en los hints del propio
    // juego (el cartel del bloque mirado, el hint del ítem en mano, el manual).
    private void ComposeHintsTab(GuiComposer compo, double startY)
    {
        double y = startY;

        // Fila 1: estilo.
        compo.AddStaticText(Lang.Get("gamepadcompanion:hints-style"),
                            CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(Margin, y + 6, HintLabelW, HintRowH));
        compo.AddSmallButton(GuiTextFit.EllipsizeButton(StyleButtonLabel(), HintControlW),
                             () => { OpenStylePicker(); return true; },
                             ElementBounds.Fixed(Margin + HintLabelW + 8, y, HintControlW, HintRowH),
                             EnumButtonStyle.Normal);
        y += HintRowH + HintRowGap;

        // Fila 2: si las acciones de la rueda también muestran glifo.
        compo.AddStaticText(Lang.Get("gamepadcompanion:hints-wheel"),
                            CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(Margin, y + 6, HintLabelW, HintRowH));
        compo.AddSwitch(v =>
            {
                config.GlyphWheel = v;
                onChanged?.Invoke();
                glyphs?.Resolver.Invalidate();
            },
            ElementBounds.Fixed(Margin + HintLabelW + 8, y, 30, 30), "hintswheel");
        y += HintRowH + HintRowGap;

        // Fila 3: vista previa. Es la inversión que más rinde de toda la tab:
        // sin ella, cada iteración sobre el dibujo cuesta reiniciar el juego,
        // encontrar un cofre y mirarlo, en la única máquina que corre el juego.
        compo.AddStaticText(Lang.Get("gamepadcompanion:hints-preview"),
                            CairoFont.WhiteSmallText(),
                            ElementBounds.Fixed(Margin, y, HintLabelW, HintRowH));
        y += 26;
        compo.AddStaticCustomDraw(
            ElementBounds.Fixed(Margin, y, DialogW - 2 * Margin, HintPreviewH),
            (ctx, _, bounds) => DrawHintPreview(ctx, bounds));
        y += HintPreviewH + HintRowGap;

        // Fila 4: estado. Que no haya mando conectado es justo el momento más
        // común en que alguien abre esta pantalla, así que se dice.
        var status = new List<string>();
        if (glyphs is null)
            status.Add("Glyph hints failed to start — see the client log.");
        else if (!glyphs.Provider.IsConnected)
            status.Add(Lang.Get("gamepadcompanion:hints-no-gamepad"));
        status.Add(Lang.Get("gamepadcompanion:hints-toggle-note", GlyphLabels.ToggleMark));

        foreach (string line in status)
        {
            compo.AddStaticText(line, CairoFont.WhiteDetailText(),
                                ElementBounds.Fixed(Margin, y, DialogW - 2 * Margin, 22));
            y += 20;
        }
    }

    // Con `auto` se muestra entre paréntesis lo que resolvió: la mitad de los
    // reportes posibles sobre glifos son "me muestra los botones equivocados", y
    // verlo acá los contesta antes de que existan.
    private string StyleButtonLabel()
    {
        GlyphStyle configured = GlyphStyles.Parse(config.GlyphStyle);
        return configured switch
        {
            GlyphStyle.Off => Lang.Get("gamepadcompanion:hints-style-off"),
            GlyphStyle.Auto when glyphs is { Resolver.Active: true } g
                => Lang.Get("gamepadcompanion:hints-style-auto-resolved", g.Resolver.Style.ToString()),
            GlyphStyle.Auto => Lang.Get("gamepadcompanion:hints-style-auto"),
            _ => configured.ToString(),
        };
    }

    // El índice 0 del picker es a la vez la primera entrada y lo que devuelve su
    // botón de limpiar, así que ahí va "Automático", que es el default.
    private static readonly GlyphStyle[] StyleOrder =
        { GlyphStyle.Auto, GlyphStyle.Xbox, GlyphStyle.PlayStation, GlyphStyle.Nintendo, GlyphStyle.Off };

    private void OpenStylePicker()
    {
        string[] names = StyleOrder.Select(st => st switch
        {
            GlyphStyle.Off  => Lang.Get("gamepadcompanion:hints-style-off"),
            GlyphStyle.Auto => StyleButtonLabel(),
            _               => st.ToString(),
        }).ToArray();

        GlyphStyle current = GlyphStyles.Parse(config.GlyphStyle);
        int currentIdx = Math.Max(0, Array.IndexOf(StyleOrder, current));

        new HotKeyPickerDialog(
            capi,
            Lang.Get("gamepadcompanion:hints-style-title"),
            names,
            currentIdx,
            idx =>
            {
                config.GlyphStyle = GlyphStyles.Serialize(
                    StyleOrder[(uint)idx < StyleOrder.Length ? idx : 0]);
                onChanged?.Invoke();
                glyphs?.Resolver.Invalidate();
                Compose();
            },
            clearButtonLabel: Lang.Get("gamepadcompanion:hints-style-auto")).TryOpen();
    }

    // Una línea de cartel de verdad: misma fuente, mismo contorno, mismo avance
    // fijo del ícono, y el ícono sale por capi.Gui.Icons.DrawIcon, o sea por el
    // MISMO delegate que dibuja en el juego. No es una maqueta parecida — si el
    // painter decide rendirse y dejar el mouse de vanilla, acá se ve eso.
    //
    // Y se dibuja DENTRO de una ventana de reescritura, igual que drawHelp: sin
    // eso la cápsula de la tecla sale de vanilla ("Shift") mientras el cartel de
    // verdad ya muestra el botón, y la vista previa pasa a mentir. Es lo que
    // pasaba entre la etapa 2 y la 3.
    private void DrawHintPreview(Context ctx, ElementBounds bounds)
    {
        // El Save va FUERA del try y el Restore en el finally: el contexto es
        // compartido con el resto del diálogo, y si algo tira después del
        // Translate el resto de la composición se dibujaría corrido.
        ctx.Save();
        // sign: true porque esto ES una línea de cartel: así el arte de las caras
        // usa el mismo avance compacto y el ícono de mouse respeta la misma regla
        // de línea que allá.
        GlyphScope.Token scope = GlyphScope.Enter(sign: true);
        try
        {
            double[] color = (double[])GuiStyle.DialogDefaultTextColor.Clone();
            for (int i = 0; i < 3; i++) color[i] = (color[i] + 1.0) / 2.0;
            CairoFont font = CairoFont.WhiteMediumText().WithColor(color).WithFontSize(20f)
                                      .WithStroke(GuiStyle.DarkBrownColor, 2.0);

            // Se dibuja desde x = 0 y se traslada: HotkeyComponent.DrawHotkey
            // mete un "+" de separación cuando recibe x > 0, y la primera
            // cápsula de la línea no lleva ninguno.
            ctx.Translate(bounds.drawX, bounds.drawY);
            font.SetupContext(ctx);

            double lh = GuiElement.scaled(30.0);
            double textH = font.GetFontExtents().Height;
            double plusW = font.GetTextExtents("+").Width;
            double x = 0.0;

            string? modifier = capi.Input.GetHotKeyByCode("shift")?.CurrentMapping?.PrimaryAsString();
            if (!string.IsNullOrEmpty(modifier))
                x = HotkeyComponent.DrawHotkey(capi, modifier, x, 0.0, ctx, font,
                                               lh, textH, plusW, 5.0, 10.0, color);

            capi.Gui.Icons.DrawIcon(ctx, "rightmousebutton", x, 1.0, lh, lh, color);
            x += lh + 5.0 + 1.0;          // el avance fijo del llamador real

            capi.Gui.Text.DrawTextLine(ctx, font,
                ": " + Lang.Get("gamepadcompanion:hints-preview-action"),
                x - 4.0, (lh - textH) / 2.0 + 2.0);
        }
        catch (Exception e)
        {
            // Que la vista previa falle no puede llevarse el diálogo de config.
            capi.Logger.Warning("GamepadCompanion: hint preview failed to draw.");
            capi.Logger.Warning(e);
        }
        finally
        {
            GlyphScope.Exit(scope);
            ctx.Restore();
        }
    }

    private void ComposeSensitivityTab(GuiComposer compo, double startY)
    {
        double y = startY;

        // Yaw sensitivity
        AddSensitivityRow(compo, ref y,
            Lang.Get("gamepadcompanion:sens-yaw"), "yaw",
            (int)config.YawSensitivity, 100, 5000, 50, "",
            v => { config.YawSensitivity = v; onChanged?.Invoke(); return true; });

        // Pitch sensitivity
        AddSensitivityRow(compo, ref y,
            Lang.Get("gamepadcompanion:sens-pitch"), "pitch",
            (int)config.PitchSensitivity, 100, 5000, 50, "",
            v => { config.PitchSensitivity = v; onChanged?.Invoke(); return true; });

        // Dead zone (escalado *100 para que el slider trabaje en int)
        AddSensitivityRow(compo, ref y,
            Lang.Get("gamepadcompanion:sens-deadzone"), "deadzone",
            (int)(config.Deadzone * 100), 0, 50, 1, "%",
            v => { config.Deadzone = v / 100f; onChanged?.Invoke(); return true; });

        // Invert pitch (switch)
        var labelBounds = ElementBounds
            .Fixed(Margin, y + 6, SensLabelW, SensRowH);
        var switchBounds = ElementBounds
            .Fixed(Margin + SensLabelW + 8, y, 30, 30);
        compo.AddStaticText(Lang.Get("gamepadcompanion:sens-invert-pitch"),
                            CairoFont.WhiteSmallText(), labelBounds);
        compo.AddSwitch(v =>
            {
                config.InvertPitch = v;
                onChanged?.Invoke();
            },
            switchBounds, "invert");
        y += SensRowH + SensRowGap;

        // Swap triggers (switch)
        var swapLabelBounds = ElementBounds
            .Fixed(Margin, y + 6, SensLabelW, SensRowH);
        var swapSwitchBounds = ElementBounds
            .Fixed(Margin + SensLabelW + 8, y, 30, 30);
        compo.AddStaticText(Lang.Get("gamepadcompanion:sens-swap-triggers"),
                            CairoFont.WhiteSmallText(), swapLabelBounds);
        compo.AddSwitch(v =>
            {
                config.SwapTriggers = v;
                onChanged?.Invoke();
            },
            swapSwitchBounds, "swaptriggers");
        y += SensRowH + SensRowGap;

        // Volver a los valores de fábrica de esta tab. Los sliders van de
        // 100 a 5000 y es fácil dejarlos en un valor injugable sin saber
        // cuál era el default; hasta ahora la única salida era borrar el
        // JSON de config a mano. Issue #4.
        var resetBounds = ElementBounds.Fixed(
            Margin, y + SensResetGap,
            DialogW - 2 * Margin, SensResetH);
        compo.AddSmallButton(Lang.Get("gamepadcompanion:restore-defaults"),
                             () => { RestoreSensitivityDefaults(); return true; },
                             resetBounds, EnumButtonStyle.Normal);
    }

    // Los defaults viven en GamepadCompanionConfig como initializers de
    // propiedad, así que una instancia nueva ES la tabla de defaults — no
    // hace falta duplicar los números acá y no se pueden desincronizar.
    private void RestoreSensitivityDefaults()
    {
        var defaults = new GamepadCompanionConfig();
        config.YawSensitivity   = defaults.YawSensitivity;
        config.PitchSensitivity = defaults.PitchSensitivity;
        config.Deadzone         = defaults.Deadzone;
        config.InvertPitch      = defaults.InvertPitch;
        config.SwapTriggers     = defaults.SwapTriggers;
        config.PrecisionFactor  = defaults.PrecisionFactor;
        onChanged?.Invoke();
        Compose();
        ApplySensitivityWidgetState();
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

    private void ApplyHintsWidgetState()
    {
        if (currentTab != TabHints) return;
        SingleComposer.GetSwitch("hintswheel").SetValue(config.GlyphWheel);
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
        SingleComposer.GetSwitch("swaptriggers").SetValue(config.SwapTriggers);
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
            Lang.Get("gamepadcompanion:pick-action-title",
                     Lang.Get("gamepadcompanion:slot-label", slot + 1)),
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
        arr[3] = HoldKeyEntryLabel;
        for (int i = 1; i < entryNames.Length; i++)
            arr[i + MetaEntryCount] = entryNames[i];
        return arr;
    }

    // Traduce: pickerIdx 0=none, 1=composite, 2=keypress, 3=holdkey,
    // 4+ = base[idx-3].
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
        if (pickerIdx == 2 || pickerIdx == 3)
        {
            OpenKeyCapture(action =>
            {
                if (action is null) return;     // cancel
                bindings.Set(slot, action);
                onChanged?.Invoke();
                Compose();
            }, holdMode: pickerIdx == 3);
            return;
        }
        int baseIdx = pickerIdx - MetaEntryCount;
        bindings.Set(slot, BaseEntryToAction(baseIdx));
        onChanged?.Invoke();
        Compose();
    }

    private void OpenKeyCapture(Action<IGameAction?> onCaptured,
                                bool holdMode = false)
    {
        new KeyCaptureDialog(capi, onCaptured, holdMode).TryOpen();
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
        if (action is HoldKeyAction) return 3;
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
