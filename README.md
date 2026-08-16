# GamepadCompanion

Soporte nativo de gamepad para [Vintage Story](https://www.vintagestory.at/) 1.22+. Funciona como mod cliente, sin necesidad de Steam Input, Antimicro ni mappers externos.

> **Estado**: usable, probado a fondo en Linux con un GameSir Cyclone 2. Cross-platform/cross-controller compatible en teoría — ver [compatibilidad](#compatibilidad) abajo.

> **Idioma de UI**: localizado vía `Lang.Get()` con archivos en `assets/gamepadcompanion/lang/` — inglés (`en.json`, fallback), español latino (`es-419.json`) y español peninsular (`es-es.json`). El idioma se toma del cliente de VS. Para contribuir otro idioma alcanza con agregar el JSON correspondiente; PRs bienvenidos.

## Qué hace

- **Movimiento y cámara** con sticks. Sensibilidad horizontal/vertical configurable, dead zone ajustable, opción de invertir pitch.
- **Acciones contextuales**: B cierra el dialog abierto o suelta el item activo según contexto; A salta (siempre); X/Y/Back/Start mapeados a tool mode, inventario, mapa, menú.
- **Rueda radial** de 12 slots configurables (LB + stick derecho). Defaults para Personaje, Chat, Manual, Configurar, Teclado virtual; el resto se asigna desde el dialog.
- **Cursor virtual sobre GUIs**: cuando hay un dialog modal abierto aparece un cursor amarillo. Con RB el stick lo mueve continuo; sin RB el DPad lo salta por pasos del tamaño de un slot — pensado para navegar inventarios rápido. RT/LT clickean (izquierdo/derecho).
- **Toggles de Ctrl y Shift** con L3/R3, indicador en HUD esquina superior derecha. Sirven para shift+click en inventario, ctrl+click colocar, etc.
- **Modo precisión** con DPad ↑: divide la sensibilidad de cámara por una fracción (default 0.3x) para apuntar bloques específicos.
- **Acciones compuestas**: una sola slot del radial puede ejecutar varias acciones en secuencia.
- **Inyección de teclas individuales** (`KeyPressAction`): bindeás cualquier tecla del teclado a un slot/botón del gamepad. Útil para hotkeys que no aparecen en la lista vanilla.
- **Teclado virtual on-screen** para tipear comandos y chat con el gamepad. QWERTY + `/` `.` para `/comandos`. DPad navega, A escribe, B cierra.
- **Editor in-game** (`/gpconfig` o tecla Insert por default): tabs para Rueda, Botones del gamepad, y Sensibilidad. Persistencia automática a JSON.

## Requisitos

- Vintage Story **1.22+**
- Un gamepad compatible (ver compatibilidad)
- En Linux: el controller debe estar detectado por el kernel como joystick (`/dev/input/js*`). Esto incluye casi cualquier Xbox controller, GameSir, 8BitDo, etc.

## Compatibilidad

### Plataformas

| Plataforma | Estado | Notas |
|------------|--------|-------|
| Linux      | ✅ Probado a fondo | Probado en CachyOS (X11) |
| Windows    | ⚠️ Untested        | El stack es cross-platform (GLFW/OpenTK/VS API), debería funcionar pero no se verificó |
| macOS      | ⚠️ Untested        | Mismo caso que Windows |

### Controllers

El mod bypassea la "gamepad mapping" de GLFW (la SDL DB) y lee el joystick raw, detectando automáticamente uno de tres layouts en el primer poll:

- **Xpad** (default): XInput / Xbox controllers en Linux y Windows. Axes en orden LX,LY,LT,RX,RY,RT; botones A,B,X,Y,LB,RB,Back,Start,Guide,L3,R3.
- **DS4-DirectInput**: PS4 y compatibles en Windows (DirectInput). Firma: triggers signed en axes 3/4 (reposo = -1). Axes en orden LX,LY,RX,LT,RT,RY; face buttons Square,Cross,Circle,Triangle.
- **GameSir-PS4**: GameSir Cyclone 2 (y probablemente otros del mismo fabricante) en modo PS4. Detectado por nombre del dispositivo ("Chicken Run" o "GameSir"). Axes en orden xpad estándar pero con triggers signed; face buttons X y Y intercambiados en raw 2/3.

En los layouts PS4 el mod ignora los raw buttons 6/7 (L2/R2 como botón digital) para evitar acciones fantasma cuando se aprietan los triggers analógicos.

| Controller | Estado |
|------------|--------|
| GameSir Cyclone 2 (modo Xbox) | ✅ Probado a fondo (Linux) |
| GameSir Cyclone 2 (modo PS4)  | ✅ Probado (Linux) |
| Xbox 360 / One / Series | ✅ Layout xpad — debería funcionar |
| PS4-like genérico DirectInput (Wired Controller, etc.) | ✅ Detectado por firma de triggers signed |
| DualShock 4 / DualSense oficiales | ⚠️ Depende del driver — algunos firmwares caen en una de las heurísticas, otros no |
| Switch Pro Controller | ⚠️ Depende del modo |
| Joysticks vintage / no-xpad / no-PS4 | ❌ Layout desconocido, mapeos van a salir mal |

Si tu controller no funciona, podés diagnosticar con `/gpaxes` (dump raw de los axes). El log del cliente también imprime el layout detectado al conectar (`detected ... layout`) y la lista completa de joysticks presentes (`candidates at connect`).

### El personaje camina o gira solo, o el mod ignora tu control

El mod se queda con el primer joystick que el sistema le muestra, y no todo lo que el sistema
llama "joystick" es un gamepad: controladoras RGB de placa madre, teclados con teclas
programables y otros HID aparecen con la misma forma (botones + ejes). Si el mod agarra uno de
esos, el resultado típico es un personaje que camina en diagonal y una cámara que gira sola,
mientras tu control real ni figura.

Corré `/gpdevice` para ver todos los joysticks presentes: el listado dice cuál está en uso y por
qué descartó a los demás. Si el elegido no es el tuyo, `/gpdevice <número>` fuerza el correcto y
lo guarda en el config para las próximas sesiones (`/gpdevice auto` deshace la elección).

### Steam Input

Si lanzás Vintage Story desde Steam, **desactivá Steam Input para este juego** o vas a tener doble input (Steam manda kb/m sintéticos + el mod maneja el gamepad en paralelo → cámara al doble de velocidad, clicks duplicados, etc.).

Cómo hacerlo, sin afectar tus otros juegos:

1. Steam → librería → clic derecho en **Vintage Story** → **Properties** → **Controller**
2. "Override for Vintage Story" → **Disable Steam Input**
3. Cerrar y relanzar VS

El resto de las features de Steam (overlay, friends, tiempo de juego, screenshots) siguen funcionando — solo desactivás la capa de remapping de gamepad. Este mod es básicamente una alternativa a Steam Input *específica para VS*, con conocimiento de las hotkeys y dialogs del juego que Steam Input no puede tener. Usá uno o el otro, no los dos.

Si tu controller solo aparece como gamepad cuando Steam Input lo emula (típico de DualSense / DualShock 4 / Switch Pro), tenés dos caminos:

- **Steam Input en modo "Gamepad" / "X360 passthrough"** (no kb/m remap): Steam expone el controller como Xbox 360 virtual y el mod lo lee normal vía GLFW. Sin doble input.
- **Driver alternativo** fuera de Steam (DS4Windows en Win, `dualsensectl` / `hid-playstation` en Linux para que el kernel lo exponga como xpad) y desactivar Steam Input por completo.

## Instalación

1. Descargá el zip de [Releases](https://github.com/ElSublimePeluca/GamepadCompanionVS/releases) o cloná y compilá (ver abajo)
2. Copiá la carpeta a `~/.config/VintagestoryData/Mods/` (Linux) o `%appdata%/VintagestoryData/Mods/` (Windows)
3. Arrancá el juego con el gamepad conectado

## Configuración

### Layout default de botones

| Botón     | Acción default |
|-----------|----------------|
| A         | Saltar (hold) |
| B         | Cerrar dialog si hay uno abierto, sino soltar item activo |
| X         | Tool mode |
| Y         | Inventario |
| LB (hold) | Abrir radial menu |
| RB (hold) | Cursor virtual modo smooth |
| RT        | Click izquierdo / atacar / minar |
| LT        | Click derecho / interactuar / colocar |
| Back      | Mapa |
| Start     | Menú de pausa |
| L3        | Toggle Ctrl |
| R3        | Toggle Shift |
| DPad ↑    | Toggle modo precisión |
| DPad ↓    | Press G (sentarse) |
| DPad ←/→  | Cambiar slot del hotbar (o navegar slots en cursor mode) |

Todo configurable desde `/gpconfig`.

### Mantener una tecla (mods con modificador)

Varios mods usan una tecla como **modificador durante un click** (RKN Crafting: mantener Alt +
click derecho). Un binding de tecla normal no sirve: manda down y up en el mismo frame, así que
la tecla ya está soltada cuando llega el click.

Para eso está la entrada **`[Tecla: mantener mientras el botón esté apretado]`** en el picker
(tab Botones → botón → elegir esa entrada → apretar la tecla). La tecla queda apretada mientras
el botón del gamepad lo esté, por el pipeline real de teclado del engine, así que un mod la ve
igual que si viniera de un teclado físico. Un modificador pelado (Alt/Ctrl/Shift) se captura
siempre como "mantener", aunque se entre por la entrada de tecla individual.

**Receta para RKN Crafting:** bindear `AltLeft` como tecla a mantener en el botón que quieras
(A, por ejemplo), después mantener ese botón y usar LT. No hace falta remapear nada en RKN.

Ojo con un efecto de vanilla, no del mod: `AltLeft` es también la tecla **Lock/Unlock Mouse
Cursor** del juego, así que mientras la mantengas el mouse queda libre — la cámara del stick
derecho no gira y el HUD de slots de RKN aparece, que es exactamente lo que ese flujo necesita.
La puntería sigue siendo el centro de la pantalla mientras no muevas el mouse físico.

### Comandos chat

| Comando        | Qué hace |
|----------------|----------|
| `/gpconfig`    | Abre el dialog de configuración (también con tecla Insert) |
| `/gpdumphotkeys` | Lista todas las hotkeys registradas en el log |
| `/gpaxes`      | Dump raw de los axes del gamepad (debug) |
| `/gpdevice`    | Lista los joysticks que ve el juego; `/gpdevice <n>` fuerza uno, `/gpdevice auto` vuelve a autodetección |
| `/gpyaw <val>` | Set sensibilidad horizontal de cámara |
| `/gppitch <val>` | Set sensibilidad vertical |
| `/gpinvertpitch` | Toggle invertir pitch |
| `/gpguis`      | Dump dialogs abiertos (debug) |

### Archivo de config

`~/.config/VintagestoryData/ModConfig/gamepadcompanion.json` — generado en el primer arranque. Borrarlo regenera defaults. Desde el dialog hay un botón "Restaurar predeterminados" en la tab Rueda.

## Build desde código

Requiere .NET 10 SDK y la variable de entorno `VINTAGE_STORY` apuntando al directorio de instalación de VS:

```bash
export VINTAGE_STORY=/opt/vintagestory   # o donde tengas VS instalado
cd GamepadCompanion
dotnet build
```

El output queda en `bin/Debug/Mods/mod/` — copiá esa carpeta (o usá `--addModPath` al iniciar VS) para probar.

## Cómo funciona internamente (disclaimer)

Para que las GUIs vanilla del juego (inventario, knapping, anvil, dialogs varios) reaccionen al gamepad sin tener que parchearlas una por una, el mod **inyecta eventos sintéticos de mouse y teclado** directamente en el motor de Vintage Story:

- El cursor virtual escribe `ClientMain.MouseCurrentX/Y` y sincroniza la posición del cursor del SO vía `GLFW.SetCursorPos`.
- Los clicks con RT/LT invocan `OnMouseDown` / `OnMouseUp` con `EnumMouseButton.Left` / `Right`.
- El teclado virtual y `KeyPressAction` invocan `OnKeyDown`, `OnKeyPress` (para chars imprimibles) y `OnKeyUp` en `ClientMain`.
- Los toggles de Ctrl/Shift escriben en `ClientMain.KeyboardState[]` para que el resto del juego vea las modifier keys como presionadas.

Todo esto es legítimo dentro del modelo de mods de VS (la API está pública), pero implica que el mod tiene acceso al stack de input del cliente y puede generar eventos que el juego trata como si vinieran del usuario. **Si te incomoda ese patrón para tu setup, no uses el mod.** No envía nada a la red, no toca archivos fuera de `ModConfig/gamepadcompanion.json`, y no tiene bloques `unsafe`, pero es honesto decir cómo trabaja antes de que lo instales.

## Licencia

MIT — ver [LICENSE](LICENSE).
