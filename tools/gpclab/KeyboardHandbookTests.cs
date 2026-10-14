using System;
using System.Collections.Generic;
using GamepadCompanion.Input;
using Vintagestory.API.Client;

namespace GamepadCompanion.Lab;

// El teclado virtual con el manual abierto (reporte de pngwn en ModDB contra 1.15.0).
//
// Tres cosas que salieron del mismo reporte:
//
//   1. Elegir "Teclado virtual" en la rueda con el manual abierto no hacía nada hasta
//      cerrarlo: la rueda difería la acción con RegisterCallback, que no corre con el juego
//      pausado, y el manual pausa el single player. Eso vive en el driver y necesita el
//      juego; acá se prueba lo que destapó y lo que se pidió.
//   2. Ya con el teclado andando, la primera letra se perdía: abrir el manual (o el chat)
//      desde el mando deja prendido GuiDialog.ignoreNextKeyPress, que espera un carácter
//      físico que nunca llega (SwallowedKeyPress).
//   3. El pedido: que el teclado se abra solo cuando el mando abre el manual en la
//      búsqueda, y que se cierre con él (HandbookKeyboard).
internal static class KeyboardHandbookTests
{
    private static int failures;

    public static int Run()
    {
        Console.WriteLine("\n  el teclado virtual se abre con el manual");
        Opening();
        Console.WriteLine("\n  y se cierra con él");
        Closing();
        Console.WriteLine("\n  la primera letra después de abrir el manual o el chat");
        Swallow();
        return failures;
    }

    private static void Opening()
    {
        var sim = new Sim();
        var book = new Book();
        sim.Tick(book, () => book.Open = true);
        Check("en el tick en que el mando lo abre, todavía no (el composer es el de la vez anterior)",
              !sim.Keyboard);
        sim.Tick(book);
        Check("en el siguiente sí", sim.Keyboard && sim.Opens == 1);
        sim.Tick(book);
        sim.Tick(book);
        Check("y una sola vez", sim.Opens == 1);

        // La tecla H del teclado físico abre el manual ENTRE ticks.
        sim = new Sim();
        book = new Book { Open = true };
        sim.Tick(book);
        sim.Tick(book);
        Check("abierto desde el teclado físico, no", !sim.Keyboard);

        // Con un ítem bajo el cursor, el hotkey abre la página de ese ítem.
        sim = new Sim();
        book = new Book { Search = false };
        sim.Tick(book, () => book.Open = true);
        sim.Tick(book);
        Check("en la página de un ítem no hay dónde escribir: no", !sim.Keyboard);

        sim = new Sim { Enabled = false };
        book = new Book();
        sim.Tick(book, () => book.Open = true);
        sim.Tick(book);
        Check("con la opción apagada, no", !sim.Keyboard);

        sim = new Sim();
        book = new Book();
        sim.Tick(book, () => book.Open = true);
        book.Open = false;                      // Escape físico antes de decidir
        sim.Tick(book);
        Check("si el manual se cerró antes de decidir, no", !sim.Keyboard);

        sim = new Sim();
        book = new Book();
        sim.Tick(book, () => book.Open = true);
        sim.Injecting = false;                  // alt-tab, o el mando se desconectó
        sim.Tick(book);
        sim.Injecting = true;
        sim.Tick(book);
        Check("sin foco no, y tampoco después al volver", !sim.Keyboard);

        sim = new Sim { Keyboard = true };
        book = new Book();
        sim.Tick(book, () => book.Open = true);
        sim.Tick(book);
        Check("un teclado que ya estaba abierto ni se reabre ni se adopta", sim.Opens == 0);
        book.Open = false;
        sim.Tick(book);
        Check("así que cerrar el manual no lo cierra", sim.Keyboard && sim.Closes == 0);
    }

    private static void Closing()
    {
        var sim = new Sim();
        var book = new Book();
        sim.Tick(book, () => book.Open = true);
        sim.Tick(book);
        book.Open = false;                      // Escape del teclado físico, o el mouse
        sim.Tick(book);
        Check("cerrar el manual cierra el teclado que se abrió con él",
              !sim.Keyboard && sim.Closes == 1);

        sim = new Sim();
        book = new Book();
        sim.Tick(book, () => book.Open = true);
        sim.Tick(book);
        sim.Tick(book, () => sim.Keyboard = false);     // B cierra el teclado
        sim.Tick(book, () => sim.Keyboard = true);      // y lo reabre a mano desde la rueda
        book.Open = false;
        sim.Tick(book);
        Check("el que el usuario reabrió a mano no se toca", sim.Keyboard && sim.Closes == 0);

        sim = new Sim();
        book = new Book();
        sim.Tick(book, () => book.Open = true);
        sim.Tick(book);
        sim.Tick(book, () => sim.Keyboard = false);     // B al teclado
        sim.Tick(book, () => book.Open = false);        // B al manual
        sim.Tick(book, () => book.Open = true);         // y de nuevo desde la rueda
        sim.Tick(book);
        Check("cerrarlo con B y volver a abrirlo con el mando lo vuelve a abrir",
              sim.Keyboard && sim.Opens == 2 && sim.Closes == 0);
    }

    private static void Swallow()
    {
        // Prendido antes del tick (una tecla física que abrió algo sin carácter que lo
        // consuma): no lo prendió el mando, no es nuestro.
        var stale = new Dialog { ignoreNextKeyPress = true };
        var book  = new Dialog();
        var chat  = new Dialog();
        var all   = new List<GuiDialog> { stale, book, null!, chat };
        var before = new HashSet<GuiDialog>();

        SwallowedKeyPress.Snapshot(all, before);
        book.ignoreNextKeyPress = true;         // HotKeyAction("handbook") → OnSurvivalHandbookHotkey
        int forgotten = SwallowedKeyPress.ForgetNew(all, before);

        Check("el flag que prendió el mando durante el tick se apaga al final del tick",
              !book.ignoreNextKeyPress && forgotten == 1);
        Check("el que ya estaba prendido antes no se toca", stale.ignoreNextKeyPress);
        Check("los demás quedan como estaban", !chat.ignoreNextKeyPress);

        // Una letra del teclado virtual que abre el chat (la T) lo consume ella misma: el
        // KeyPress va en el mismo despacho que el KeyDown, igual que con un teclado físico.
        SwallowedKeyPress.Snapshot(all, before);
        chat.ignoreNextKeyPress = true;         // KeyDown(T) → OnKeyCombinationToggle
        chat.ignoreNextKeyPress = false;        // KeyPress('t') → GuiDialog.OnKeyPress
        Check("y si el carácter ya lo consumió, no queda nada que apagar",
              SwallowedKeyPress.ForgetNew(all, before) == 0);
    }

    // Un manual de mentira: si está abierto, y si muestra la búsqueda o la página de un ítem.
    private sealed class Book
    {
        public bool Open;
        public bool Search = true;
    }

    // El estado de HandbookKeyboard y el del teclado, tick a tick, con la misma cuenta que
    // EndTick. Lo que el mando hace ADENTRO del tick va en `during`; lo físico se simula
    // tocando el estado entre dos Tick.
    private sealed class Sim
    {
        private Book? decideFor, owner;
        public bool Keyboard;
        public int Opens, Closes;
        public bool Injecting = true, Enabled = true;

        public void Tick(Book book, Action? during = null)
        {
            Book? atStart = Injecting && book.Open ? book : null;   // BeginTick
            during?.Invoke();
            Book? atEnd = book.Open ? book : null;
            var step = HandbookKeyboard.Next(decideFor, owner, atStart, atEnd,
                                             Injecting, Enabled, Keyboard,
                                             isOpen: b => b.Open, showsSearch: b => b.Search);
            decideFor = step.DecideFor;
            owner = step.Owner;
            if (step.Close) { Keyboard = false; Closes++; }
            if (step.Open)  { Keyboard = true;  Opens++; }
        }
    }

    // GuiDialog sólo guarda el capi en el constructor, así que se puede instanciar sin juego.
    private sealed class Dialog : GuiDialog
    {
        public Dialog() : base(null!) { }
        public override string ToggleKeyCombinationCode => null!;
    }

    private static void Check(string what, bool ok)
    {
        if (!ok) failures++;
        Console.WriteLine($"    [{(ok ? "ok" : "NO")}] {what}");
    }
}
