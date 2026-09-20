using GamepadCompanion.Input;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace GamepadCompanion.Actions;

// Implementaciones compartidas de comportamientos "built-in" que no son
// hotkeys ni dialogs. Las llaman tanto BuiltinAction (cuando el usuario
// bindea el code en un slot/botón) como ButtonMapper (para los defaults
// hardcoded de A/B/DPad). Mantenerlos acá evita duplicación y permite
// exponer el mismo set de acciones al picker.
public static class BuiltinActions
{
    private const int HotbarVisibleSlots = 10;

    // Suelta el item del slot activo del hotbar (no el slot bajo el mouse).
    // KeyDropItem vanilla prefiere currentHoveredSlot sobre ActiveHotbarSlot;
    // llamamos InventoryManager.DropItem(ActiveHotbarSlot) directo para que
    // funcione consistentemente desde gamepad (sin hover real).
    public static void DropItem(ICoreClientAPI capi)
    {
        var player = capi.World?.Player;
        var inv = player?.InventoryManager;
        var slot = inv?.ActiveHotbarSlot;
        if (slot?.Itemstack is null) return;
        if (inv!.DropItem(slot, fullStack: false))
            capi.World!.PlaySoundAt(
                new AssetLocation("sounds/player/quickthrow"),
                player!.Entity, player,
                randomizePitch: true);
    }

    // Comportamiento contextual del botón B: si hay un GuiDialog modal
    // abierto, lo cierra; sino, suelta el item activo del hotbar.
    public static void DropOrDismiss(ICoreClientAPI capi)
    {
        if (!TryDismissOpenDialog(capi))
            DropItem(capi);
    }

    public static void HotbarPrev(ICoreClientAPI capi) => ChangeHotbarSlot(capi, -1);
    public static void HotbarNext(ICoreClientAPI capi) => ChangeHotbarSlot(capi, +1);

    // Sentarse en el piso. `sitdown` SÍ es una hotkey de VS, y es rebindeable,
    // pero no tiene Handler: `SystemPlayerControl.OnKeyDown` compara el keycode
    // del evento contra `HotKeys["sitdown"].CurrentMapping.KeyCode` y listo. De
    // ahí salen las dos cosas raras que reportó pngwn en ModDB:
    //
    //   - No aparecía en la lista de la rueda ni en la de los botones:
    //     ConfigDialog.BuildEntryList sólo ofrece hotkeys CON Handler (las
    //     demás no se pueden disparar) y ésta no tiene.
    //   - Tampoco servía HotkeyDispatcher.Trigger, por lo mismo.
    //
    // Así que mandamos la tecla de verdad, resolviéndola del binding vivo en vez
    // de hardcodear G: quien la haya rebindeado en las opciones del juego
    // igual se sienta.
    public static void SitDown(ICoreClientAPI capi)
        => new KeyPressAction(SitKeyCode(capi),
                              label: Lang.Get("gamepadcompanion:builtin-sitdown"))
            .Execute(capi);

    // G es el default de vanilla, y el fallback si el hotkey no existiera (otra
    // versión del juego, o un mod que lo saque).
    public const int SitFallbackKeyCode = (int)GlKeys.G;

    public static int SitKeyCode(ICoreClientAPI capi)
        => capi.Input.GetHotKeyByCode("sitdown")?.CurrentMapping?.KeyCode
           ?? SitFallbackKeyCode;

    // Abre el teclado virtual. Lo resolvemos via ModLoader para no
    // acoplar Actions/* a GamepadInputDriver directamente. Tipo HUD,
    // así no roba focus del chat (caso de uso principal).
    public static void OpenVirtualKeyboard(ICoreClientAPI capi)
    {
        var mod = capi.ModLoader.GetModSystem<GamepadCompanionModSystem>();
        var dialog = mod?.Driver?.VirtualKeyboard;
        if (dialog is null) return;
        if (!dialog.IsOpened()) dialog.TryOpen();
    }

    // Modo precisión (baja la sensibilidad de cámara por PrecisionFactor).
    // Hasta 1.13 vivía hardcodeado en GamepadInputDriver sobre D-pad ↑, fuera
    // de ButtonMapper. Pasa a builtin porque los layouts lo mueven de botón, y
    // de paso se vuelve asignable a cualquier botón o slot de la rueda.
    public static void TogglePrecision(ICoreClientAPI capi)
    {
        var mod = capi.ModLoader.GetModSystem<GamepadCompanionModSystem>();
        mod?.Driver?.Toggles.TogglePrecision();
    }

    private static void ChangeHotbarSlot(ICoreClientAPI capi, int delta)
    {
        var inv = capi.World?.Player?.InventoryManager;
        if (inv is null) return;

        int n = HotbarVisibleSlots;
        int next = ((inv.ActiveHotbarSlotNumber + delta) % n + n) % n;
        inv.ActiveHotbarSlotNumber = next;
    }

    private static readonly string[] ImGuiToggleHotkeys = {
        "xSkillGilded_v2",
    };

    public static bool TryDismissOpenDialog(ICoreClientAPI capi)
    {
        foreach (var dialog in capi.Gui.OpenedGuis)
        {
            if (dialog is null) continue;
            if (!dialog.IsOpened()) continue;
            string name = dialog.GetType().Name;
            if (name == "ToggleHudOverlay" || name == "RadialMenuDialog") continue;
            if (dialog.DialogType != EnumDialogType.Dialog) continue;

            if (name == "VSImGuiDialog")
                return TryToggleImGuiMod(capi);

            dialog.OnEscapePressed();
            if (dialog.IsOpened()) dialog.TryClose();
            return true;
        }

        return false;
    }

    private static bool TryToggleImGuiMod(ICoreClientAPI capi)
    {
        foreach (var code in ImGuiToggleHotkeys)
        {
            var hk = capi.Input.GetHotKeyByCode(code);
            if (hk?.Handler is null) continue;
            var combo = hk.CurrentMapping;
            if (combo is null) continue;
            hk.Handler(combo);
            return true;
        }
        return false;
    }
}
