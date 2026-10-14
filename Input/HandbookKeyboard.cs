using System;
using System.Collections.Generic;
using GamepadCompanion.Gui;
using Vintagestory.API.Client;

namespace GamepadCompanion.Input;

// Abre el teclado virtual cuando el mando abre el manual en la página de
// búsqueda, y lo cierra con el manual. Pedido de pngwn en ModDB: "most of the
// time I intend to type something into the search bar", y hasta acá había que
// abrir la rueda una segunda vez para pedir el teclado (o soltar el mando y
// agarrar el teclado físico). Se apaga con KeyboardWithHandbook en el config.
//
// Tres reglas, cada una con su porqué:
//
//   1. Sólo si lo abrió el MANDO. Quien aprieta H en el teclado físico va a
//      escribir con ese teclado, y un teclado virtual encima sólo estorba: se
//      queda con todo el input del mando hasta cerrarlo. Se distingue por
//      CUÁNDO pasa: los eventos físicos se despachan entre ticks del driver, y
//      lo que hace el mando (un botón, la rueda, un clic del cursor virtual)
//      pasa ADENTRO del tick. Un manual que estaba cerrado al empezar el tick y
//      abierto al terminarlo lo abrimos nosotros.
//
//   2. Sólo en la página de búsqueda. Con un ítem bajo el cursor, el hotkey del
//      manual abre directo la página de ese ítem (OnSurvivalHandbookHotkey →
//      OpenDetailPageFor), y ahí no hay dónde escribir. Qué página muestra se
//      ve en su composer: el de la búsqueda es el único con el campo
//      "searchField". Pero GuiDialogHandbook recién asigna SingleComposer en
//      OnRenderGUI, así que en el tick en que se abre todavía tiene el de la vez
//      anterior; por eso se decide en el tick SIGUIENTE, ya dibujado una vez.
//
//   3. El teclado que abrimos nosotros se cierra con su manual. Si no, cerrar el
//      manual con Escape o con el mouse dejaba el teclado virtual abierto en el
//      mundo, quedándose con el mando y mandando cada letra como un hotkey. Si
//      el usuario ya lo había cerrado con B, deja de ser nuestro: lo que abra
//      después a mano desde la rueda no lo tocamos.
//
// El manual se reconoce por el nombre del tipo, subiendo por la herencia,
// porque el mod no referencia VSSurvivalMod.dll (lo mismo que se hace con
// VSImGuiDialog). Cubre el de supervivencia y GuiDialogCommandHandbook, que
// derivan los dos de GuiDialogHandbook. El nombre está en el apicheck de gpclab.
public sealed class HandbookKeyboard
{
    private const string HandbookTypeName = "Vintagestory.GameContent.GuiDialogHandbook";
    private const string SearchFieldKey   = "searchField";

    private readonly ICoreClientAPI capi;
    // El getter perezoso del driver: el teclado se compone recién la primera vez
    // que hace falta, y el caso de todos los frames ni lo toca.
    private readonly Func<VirtualKeyboardDialog> keyboard;
    private readonly Dictionary<Type, bool> handbookTypes = new();

    private GuiDialog? openAtTickStart;
    // El manual que abrió el mando en el tick anterior, esperando su decisión.
    private GuiDialog? decideFor;
    // El manual para el que abrimos el teclado (regla 3).
    private GuiDialog? owner;

    public HandbookKeyboard(ICoreClientAPI capi, Func<VirtualKeyboardDialog> keyboard)
    {
        this.capi = capi;
        this.keyboard = keyboard;
    }

    // Antes de que el mando haga nada en este tick.
    public void BeginTick(bool injecting)
        => openAtTickStart = injecting ? FindOpenHandbook() : null;

    // Después de todo lo del tick. Va en el `finally` del driver: corre en todos
    // los caminos, así que la foto de BeginTick nunca queda vieja.
    public void EndTick(bool injecting, bool enabled)
    {
        if (!injecting && owner is null && decideFor is null) return;

        GuiDialog? openNow = FindOpenHandbook();
        bool openedByUs = injecting && openAtTickStart is null && openNow is not null;
        // El caso de todos los frames: nada abierto por nosotros, nada pendiente.
        if (!openedByUs && owner is null && decideFor is null) return;

        var kb = keyboard();
        var step = Next(decideFor, owner, openAtTickStart, openNow,
                        injecting, enabled, kb.IsOpened(),
                        isOpen: d => d.IsOpened(), showsSearch: ShowsSearch);
        decideFor = step.DecideFor;
        owner     = step.Owner;
        // En este orden: si el manual de antes se cerró y el mando abrió otro en
        // el mismo par de ticks, el teclado termina abierto para el nuevo.
        if (step.Close) kb.TryClose();
        if (step.Open)  kb.TryOpen();
    }

    // Un tick de la máquina, sin el juego: el estado que viene del tick anterior
    // (decideFor, owner), el manual abierto al empezar y al terminar este tick, y
    // qué hacer con el teclado. Genérica y pura para poder probarla en gpclab
    // selftest con objetos de mentira.
    internal readonly record struct Step<T>(T? DecideFor, T? Owner, bool Open, bool Close)
        where T : class;

    internal static Step<T> Next<T>(T? decideFor, T? owner,
                                    T? openAtTickStart, T? openNow,
                                    bool injecting, bool enabled, bool keyboardOpen,
                                    Func<T, bool> isOpen, Func<T, bool> showsSearch)
        where T : class
    {
        bool open = false, close = false;

        // Regla 3. Si el usuario ya cerró el teclado, deja de ser nuestro.
        if (owner is not null)
        {
            if (!keyboardOpen) owner = null;
            else if (!isOpen(owner))
            {
                close = true;
                owner = null;
                keyboardOpen = false;
            }
        }

        // Regla 2, para el manual que abrió el mando en el tick anterior. Un
        // teclado que ya estaba abierto es del usuario: ni se reabre ni se
        // adopta.
        if (decideFor is not null)
        {
            if (injecting && enabled && !keyboardOpen
                && isOpen(decideFor) && showsSearch(decideFor))
            {
                open = true;
                owner = decideFor;
            }
            decideFor = null;
        }

        // Regla 1: ¿lo abrió el mando en ESTE tick? Se decide en el siguiente.
        if (injecting && openAtTickStart is null && openNow is not null)
            decideFor = openNow;

        return new Step<T>(decideFor, owner, open, close);
    }

    private GuiDialog? FindOpenHandbook()
    {
        foreach (var dialog in capi.Gui.OpenedGuis)
            if (dialog is not null && dialog.IsOpened() && IsHandbook(dialog.GetType()))
                return dialog;
        return null;
    }

    private bool IsHandbook(Type type)
    {
        if (!handbookTypes.TryGetValue(type, out bool yes))
        {
            for (Type? t = type; t is not null && !yes; t = t.BaseType)
                yes = t.FullName == HandbookTypeName;
            handbookTypes[type] = yes;
        }
        return yes;
    }

    // GetElement y no GetTextInput: el helper del engine castea, y un manual de
    // otro mod con un "searchField" de otro tipo nos tiraría adentro del tick.
    private static bool ShowsSearch(GuiDialog handbook)
        => handbook.SingleComposer?.GetElement(SearchFieldKey) is GuiElementTextInput;
}
