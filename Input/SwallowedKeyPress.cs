using System.Collections.Generic;
using Vintagestory.API.Client;

namespace GamepadCompanion.Input;

// GuiDialog.ignoreNextKeyPress: "el próximo carácter que me llegue, tiralo".
//
// Lo prenden los handlers de dos hotkeys de vanilla al abrir su diálogo, para
// que la letra de la tecla que los abrió no se escriba adentro:
//
//   - ModSystemSurvivalHandbook.OnSurvivalHandbookHotkey (el manual, H):
//     TryOpen() y después ignoreNextKeyPress = true.
//   - HudDialogChat.OnKeyCombinationToggle (el chat, T), si la tecla del
//     combo es imprimible.
//
// Con un teclado físico eso cierra: la tecla H genera su KeyDown, el hotkey
// abre el manual y prende el flag, y el KeyPress de la misma H — que el SO
// manda en la misma tanda de eventos — lo consume. Pero el mando dispara esos
// handlers SIN tecla física: HotKeyAction invoca el Handler directo (el
// "Manual" de la rueda por defecto), OpenLoadedGuiAction invoca
// OnKeyCombinationToggle (el "Chat"), y KeyPressAction manda un KeyDown sin
// KeyPress. Ningún carácter viene a consumir el flag, así que queda prendido y
// se come el PRIMERO que llegue después: la primera letra del teclado virtual,
// o la del teclado físico si el usuario lo agarra.
//
// El arreglo usa justamente esa diferencia de tiempos: los eventos físicos se
// despachan entre ticks del driver (en el bombeo de eventos de la ventana), y
// lo que hace el mando pasa ADENTRO del tick. Un flag que se prendió durante
// nuestro tick lo prendió algo que hicimos nosotros, y no hay tecla física que
// lo vaya a consumir: se apaga al final del mismo tick. Un flag que ya estaba
// prendido antes no es nuestro y no se toca.
//
// El único hueco es una tecla del teclado virtual en el MISMO tick en que una
// acción prendió el flag (con el layout clásico la rueda se abre sobre el
// teclado, y soltar LB y apretar A tendría que caer en el mismo frame): ese
// carácter se lo come el flag, igual que se lo comería a un teclado físico.
public static class SwallowedKeyPress
{
    // Antes de que el mando haga nada en este tick: qué diálogos ya tenían el
    // flag prendido. Casi siempre ninguno, y entonces no aloca.
    public static void Snapshot(IEnumerable<GuiDialog> dialogs,
                                HashSet<GuiDialog> into)
    {
        into.Clear();
        foreach (var dialog in dialogs)
            if (dialog is not null && dialog.ignoreNextKeyPress)
                into.Add(dialog);
    }

    // Después: apaga el flag de los que lo prendieron durante el tick.
    // Devuelve cuántos.
    public static int ForgetNew(IEnumerable<GuiDialog> dialogs,
                                HashSet<GuiDialog> flaggedBefore)
    {
        int forgotten = 0;
        foreach (var dialog in dialogs)
        {
            if (dialog is null || !dialog.ignoreNextKeyPress) continue;
            if (flaggedBefore.Contains(dialog)) continue;
            dialog.ignoreNextKeyPress = false;
            forgotten++;
        }
        return forgotten;
    }
}
