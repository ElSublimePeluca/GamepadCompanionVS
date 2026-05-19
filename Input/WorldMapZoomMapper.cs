using Vintagestory.API.Client;

namespace GamepadCompanion.Input;

// Zoom del worldmap (Vintagestory.GameContent.GuiDialogWorldMap) con DPad↑/↓.
// El dialog vanilla escucha MouseWheelEventArgs y delega al GuiElementMap
// interno; emitiendo el evento conseguimos zoom sin tocar APIs internas
// del dialog ni agregar dependencia a VSEssentials.dll.
//
// Detección por nombre de tipo para evitar referenciar VSEssentials (donde
// vive GuiDialogWorldMap). El nombre del tipo es estable desde hace varios
// releases y la API no expone una forma neutral de identificarlo.
//
// Solo zoomea cuando el WorldMap está en modo Dialog (full-screen). En modo
// HUD (minimap pinned a la esquina) el usuario está jugando in-world y DPad
// debe seguir haciendo lo de siempre, no zoomear un mapita decorativo.
public sealed class WorldMapZoomMapper
{
    private const string WorldMapDialogTypeName = "GuiDialogWorldMap";

    private readonly ICoreClientAPI capi;

    public WorldMapZoomMapper(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    // Retorna true si el WorldMap full-screen está abierto y se debe saltar
    // el step DPad↑/↓ del cursor virtual (porque ya hicimos zoom con esos
    // mismos presses).
    public bool Apply(GamepadState current, GamepadState previous)
    {
        var dialog = FindWorldMapFullScreen();
        if (dialog is null) return false;

        if (current.WasPressed(GamepadButton.DPadUp,   previous)) Emit(dialog, +1);
        if (current.WasPressed(GamepadButton.DPadDown, previous)) Emit(dialog, -1);
        return true;
    }

    private GuiDialog? FindWorldMapFullScreen()
    {
        foreach (var dlg in capi.Gui.OpenedGuis)
        {
            if (dlg is null || !dlg.IsOpened()) continue;
            if (dlg.DialogType != EnumDialogType.Dialog) continue;
            if (dlg.GetType().Name == WorldMapDialogTypeName) return dlg;
        }
        return null;
    }

    private static void Emit(GuiDialog dialog, int delta)
    {
        var args = new MouseWheelEventArgs
        {
            delta = delta,
            deltaPrecise = delta,
            value = delta,
            valuePrecise = delta,
        };
        dialog.OnMouseWheel(args);
    }
}