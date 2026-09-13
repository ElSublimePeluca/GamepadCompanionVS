# gpclab — banco de pruebas offline

Herramienta de desarrollo de GamepadCompanion. **No forma parte del mod**: `tools/` está
excluida de la compilación en `GamepadCompanion.csproj`, así que nada de esto entra al DLL
ni al zip del release.

Existe por una razón concreta: sólo una de las dos máquinas de desarrollo corre el juego, y
cada pregunta de píxeles ("¿entra `LT` en el cuadrado a GUIScale 0.5?") costaba un reinicio
completo del cliente. Acá se contesta en dos segundos, sin abrir el juego.

## Requisitos

El juego **extraído**. No hace falta que sea jugable ni que esté instalado: alcanza con el
tarball desempaquetado, porque lo único que se usa son los DLL y la carpeta de fuentes.

```
export VINTAGE_STORY=/ruta/a/vintagestory      # el directorio que contiene VintagestoryAPI.dll
```

Si la variable no está, se prueban `%APPDATA%/Vintagestory` y `~/.local/share/vintagestory`.
Si no aparece por ningún lado, la herramienta aborta con un mensaje — nunca sigue con
valores inventados.

## Comandos

```
dotnet run --project tools/gpclab -- apicheck [--update]
dotnet run --project tools/gpclab -- selftest
dotnet run --project tools/gpclab -- labels
dotnet run --project tools/gpclab -- boxes
dotnet run --project tools/gpclab -- render [salida.png]
```

### `apicheck`

Vuelca por reflection las firmas del engine de las que dependen los glifos (los seams de
Harmony, los campos que se leen, los valores de los que salen las cuentas de píxeles) y el
cursor virtual (los privados de los que salen los destinos del D-pad), y las compara contra
`apisurface.baseline.txt`, que está commiteado. Sale con código ≠ 0 si algo cambió.

**Correlo el día que actualizás el juego, antes de abrir el IDE.** Si algo cambió, es un
error el día correcto en vez de un issue de un usuario tres semanas después. Cuando el
cambio esté entendido y el código ajustado, actualizá el baseline en el mismo commit:

```
dotnet run --project tools/gpclab -- apicheck --update
```

Lo que **no** puede ver, y por eso el mod igual chequea en runtime: que el JIT inlinee un
método en el call site del engine, que otro mod le ponga un prefix con `return false`, o que
el cuerpo de un método cambie sin cambiar la firma.

### `selftest`

Pruebas de regresión del propio mod que no necesitan el juego abierto. Cubren caminos que
nadie ejercita a mano y que ya costaron bugs reales:

- **Round-trip de bindings a JSON.** El switch de serialización estaba duplicado en
  `SlotBindings` y en `ButtonBindings`; la copia de la rueda nunca aprendió `"holdkey"`, así
  que un "mantener tecla" asignado a un slot se **borraba** al guardar. Y no degradaba a tap:
  `HoldKeyAction` no hereda de `KeyPressAction`, así que no lo agarraba ninguna otra rama.
- **Recuperación de una config corrupta.** Sólo corre cuando el JSON ya está roto — o sea
  nunca durante el desarrollo — y si falla, el `ModLoader` saca al mod de `enabledSystems` y
  el gamepad entero queda muerto con un renglón en el log.
- **A dónde salta el D-pad con un diálogo abierto** (issue #9). Con la geometría medida en la
  captura del reporte: la salida del crafteo que el paso fijo de 52 px no alcanzaba, el cruce
  entre la grilla y el inventario, el cono que evita saltar a la mochila 600 px más abajo, un
  campo de texto ancho y la fila del selector de recetas. Lo que no se prueba acá es de qué
  elementos salen los destinos (`CursorTargets`): eso necesita diálogos compuestos de verdad.
- **A y RT son un mismo clic en los diálogos.** Apretarlos en cualquier orden tiene que dar un
  solo MouseDown y un solo MouseUp, y con un binding en A sólo clickea RT.

Sale con código ≠ 0 si algo falla. Corre en la laptop, sin el juego instalado.

### `labels`

Dos tablas:

- La de **glifos**: `(control, familia) → etiqueta`, con el ancho en píxeles de cada una a la
  tipografía del cartel. Sirve para elegir una etiqueta nueva sin adivinar.
- La de **textos del diálogo del mod**, medidos contra la caja que les toca **en los tres
  idiomas** y a GUIScale 1.0 y 1.5, leyendo los JSON de `assets/gamepadcompanion/lang/`.

La segunda existe porque los botones de VS se agrandan solos para que el label entre en una
línea y nadie los clippea, así que un label largo se dibuja fuera del panel, flotando sobre el
mundo (fue el issue #7). Y la barra de tabs no reparte el ancho: si la suma de los cuatro
labels pasa el interior del diálogo, aparecen flechitas de scroll. La primera corrida ya
encontró una línea de estado en castellano que medía 580 px en una caja de 448.

### `boxes`

Mide las tres cajas de ícono que llaman al mismo delegate, cada una con su tipografía, en
GUIScale 0.5 / 1.0 / 1.5, y dice para cada etiqueta candidata si entra o por cuántos píxeles
se pasa. Es el número que decide cómo se dimensiona la cápsula cuadrada.

Las tres cajas no son la misma, y ahí está la trampa:

| llamador | caja | fuente de la línea |
|---|---|---|
| cartel del bloque mirado | `scaled(30)` | 20 |
| hint del ítem en mano (`HudHotbar`) | `scaled(25)` | 16 |
| tag `<icon>` del tutorial | `scaled(18)` ó `scaled(15)` | 18 / 15 |

### `render`

Dibuja líneas del cartel en un PNG, con la tipografía, el contorno y el padding reales, sobre
cielo claro y sobre cueva oscura, a los tres GUIScale. Usa `HotkeyComponent.DrawHotkey` y
`IconUtil.DrawIcon` **del engine** y el painter **del mod**
(`Glyphs/MouseIconOverride.TryDrawSquareCapsule`), no reimplementaciones: un port a mano se
desincroniza en silencio con el próximo update, que es justo lo que esta herramienta existe
para evitar. Esa fidelidad ya se pagó sola — la primera corrida salió con la fuente por
defecto de Cairo, de 10 px, porque `TextDrawUtil.DrawTextLine` dibuja con la fuente que tenga
puesta el `Context` y no con la del `CairoFont` que recibe.

## Las tres trampas que ya están resueltas acá

1. **Los DLL se leen del `VINTAGE_STORY` vivo**, no de una copia en `bin/`. Por eso las
   `<Reference>` van con `<Private>false</Private>` y hay un `AssemblyResolve` en
   `Program.cs`. Con una copia local, `apicheck` podría pasar contra los DLL viejos justo el
   día que el juego se actualizó — el único día en que `apicheck` importa.

2. **La fuente.** `GuiStyle.StandardFontName` es `"sans-serif"`, o sea que la resuelve
   fontconfig. El juego se lanza con `FONTCONFIG_FILE=$VINTAGE_STORY/fonts.conf` y con el
   directorio de trabajo en la carpeta del juego (`Properties/launchSettings.json`), y ese
   `fonts.conf` declara la carpeta de fuentes con una ruta **relativa**. Sin las dos cosas,
   Cairo mide con el `sans-serif` del sistema y devuelve números plausibles y equivocados,
   que es peor que no tener harness. `gpclab` replica las dos, e imprime una **huella de la
   fuente** en cada corrida: si no coincide con la de la medición anterior, los números no
   son comparables.

   Detalle de .NET que hace falta saber: `Environment.SetEnvironmentVariable` escribe una
   copia manejada del entorno y **no** llama al `setenv()` de libc, así que fontconfig —
   que es nativo — no se enteraría. Hay que pisarlo por P/Invoke.

3. **Cairo nativo.** `cairo-sharp` declara sus `DllImport` con el nombre de Windows
   (`libcairo-2`); el juego resuelve eso en su propio arranque, que acá no existe. `gpclab`
   registra su propio `DllImportResolver` que mapea a `libcairo.so.2`.

## Pendiente

- Nada pendiente por ahora. El self-test de los parches de Harmony no vive acá sino en el mod
  (`Glyphs/GlyphPatcher.cs`), porque necesita el engine cargado: atraviesa el call site real
  construyendo un `HotkeyComponent` y pidiéndole la textura.
