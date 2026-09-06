namespace GamepadCompanion.Glyphs;

using System;

// La ventana de reescritura. El postfix de KeyCombination.PrimaryAsString sólo
// transforma mientras hay una ventana abierta EN ESTE HILO, y las ventanas las
// abren únicamente los otros dos parches: el de HotkeyComponent.GenHotkeyTexture
// (los tags <hk> en prosa) y el de DrawWorldInteractionUtil.drawHelp (el cartel).
//
// La propiedad estructural que hace que valga la pena: el parche que HACE EL
// TRABAJO es inerte por default, y quien lo habilita es OTRO parche
// independiente. Si un update del juego renombra drawHelp o GenHotkeyTexture, el
// postfix deja de tener efecto SOLO — sin código de detección y sin fallback. El
// modo de falla normal pasa de "sustituye en lugares donde no debe" a "no hace
// nada".
//
// Fuera de la ventana no se sustituye nada, y por eso quedan intactos POR
// CONSTRUCCIÓN, no por una lista de excepciones:
//   · Ajustes > Controles y su detector de conflictos, que comparan strings de
//     KeyCombination.ToString() como si fueran identidad;
//   · HotkeyManager.IsHotKeyRegistered / GetHotkeyByKeyCombination, ídem;
//   · los números del hotbar, el chat, el editor de macros, el cartel de la cama;
//   · el MENÚ PRINCIPAL: hay una sola clase HotkeyComponent en el proceso y el
//     parche es estático, pero fuera de una sesión de mundo GlyphRuntime.Session
//     es null y los cuerpos son no-ops.
internal static class GlyphScope
{
    [ThreadStatic] private static int depth;
    [ThreadStatic] private static bool inSign;
    [ThreadStatic] private static bool signConvertible;

    internal readonly struct Token
    {
        internal readonly bool PrevInSign;
        internal readonly bool PrevConvertible;
        internal Token(bool inSign, bool convertible)
        {
            PrevInSign = inSign;
            PrevConvertible = convertible;
        }
    }

    // depth > 0 ⇒ el postfix transforma. En el cartel exige además que la línea
    // ENTERA sea convertible: media línea en glifos y media en teclado se lee
    // peor que la línea entera en teclado.
    internal static bool Open => depth > 0 && (!inSign || signConvertible);

    // Los lee el painter del ícono de mouse, que no pasa por el postfix pero sí
    // tiene que respetar la misma regla de todo-o-nada por línea.
    internal static bool InSign => inSign;
    internal static bool SignConvertible => signConvertible;

    internal static Token Enter(bool sign, bool convertible = true)
    {
        var previous = new Token(inSign, signConvertible);
        depth++;
        inSign = sign;
        signConvertible = convertible;
        return previous;
    }

    internal static void Exit(Token token)
    {
        if (depth > 0) depth--;
        inSign = token.PrevInSign;
        signConvertible = token.PrevConvertible;
    }

    // Red contra una ventana que quedó abierta (un finalizer que no corrió por lo
    // que sea). Lo llama el renderer del mod, que corre en el mismo hilo que
    // compone la GUI. Sin esto, una ventana filtrada sí contaminaría Ajustes.
    internal static void ResetForFrame()
    {
        depth = 0;
        inSign = false;
        signConvertible = true;
    }
}
