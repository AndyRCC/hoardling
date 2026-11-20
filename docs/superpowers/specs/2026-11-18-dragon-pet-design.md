# Dragón mascota: diseño

> Nota (2026-11-20): el proyecto se llama ahora **Hoardling**. Este documento conserva el nombre
> original (DragonPet), sus rutas (`src/DragonPet…`) y su URL de hooks (`/dragonpet`) tal como eran
> al escribirlo.

Fecha: 2026-11-18 · Estado: borrador para aprobación

## Intención

Una mascota de escritorio para Windows 11, parecida a la de Codex, con forma de dragón
chibi. Sirve para dos cosas sin tener que cambiar de ventana:

1. Ver de un vistazo cuánto llevas gastado del límite de 5 horas de **Claude** y de
   **Antigravity**, y el detalle (semana, reinicios) al pasar el mouse.
2. Saber si Claude Code o Antigravity están trabajando, esperando algo de ti o parados.

Tiene que gastar muy pocos recursos: va a estar abierta todo el día.

## Objetivos

- Dragón chibi animado (opción B elegida), siempre encima, arrastrable.
- En reposo: dos anillos alrededor del dragón, solo límite de 5 h.
  - Anillo exterior naranja: Claude, sesión de 5 h.
  - Anillo interior azul: Antigravity, el bucket de 5 h **más gastado**
    (Gemini o Claude/GPT).
- Al pasar el mouse: tarjeta con el detalle (opción 2 de la conversación).
- Estados animados según actividad (ver "Estados").
- Fusión bicolor (opción A) cuando los dos trabajan a la vez.
- Consumo objetivo: menos de 60 MB de RAM y menos de 1 % de CPU en reposo.

## No objetivos

- Pestaña de chat normal de Claude (claude.ai / Chat de la app): el usuario no la usa.
  Solo Claude Code (pestaña Code de la app de escritorio y terminal).
- Notificaciones de Windows, sonidos, varias mascotas, tienda de skins, macOS/Linux.
- Editar o controlar Claude/Antigravity desde la mascota (es solo de lectura).

## Anti-objetivos (sería un fracaso aunque funcione)

- Que gaste recursos notables (ventilador, batería) o que se vea pixelado/dentado.
- Que rompa o ensucie Claude Code: errores visibles en cada mensaje, hooks que bloquean,
  o que cierre la sesión de Claude Code del usuario.
- Que muestre datos viejos como si fueran actuales (si no hay dato, se ve gris).

## Restricciones

- Windows 11, .NET 10 Desktop Runtime ya instalado. Hay que descargar el **.NET 10 SDK**
  (winget `Microsoft.DotNet.SDK.10`, ~250 MB) solo para compilar.
- Las dos fuentes de límites **no son oficiales** y pueden cambiar con actualizaciones.
- Antigravity solo da datos mientras está abierto (su servidor local).
- El token de Claude Code en `~/.claude/.credentials.json` caducó (2026-06-19) porque el
  usuario usa la app de escritorio. El usuario hará `/login` una vez en la terminal.

## Enfoques considerados

| Enfoque | A favor | En contra |
|---|---|---|
| **C# + WPF (.NET 10)** ← elegido | Nativo, vectorial y suave, transparencia real, ~40–60 MB | Descargar el SDK |
| Python + tkinter | Ya instalado, sin descargas | Transparencia por color clave: bordes dentados |
| Tauri (web) | Se vería idéntico a los bocetos HTML | Rust + Build Tools (varios GB), WebView2 gasta 2–3× más |

Elegido por el usuario: C#, por optimizar recursos. Electron descartado (pesado).

## Decisiones del usuario

- Estilo: chibi (B). Reposo = anillos solo 5 h. Hover = tarjeta detallada (opción 2).
- Anillo azul = el bucket de 5 h de Antigravity más gastado.
- Si ni Claude ni Antigravity están abiertos: el dragón duerme.
- Trabajando: se pone lentes y escribe o piensa, al estilo Codex.
- Naranja = Claude trabajando. Azul = Antigravity trabajando. Los dos = fusión bicolor
  (mitad izquierda naranja, mitad derecha azul, destellos).

## Decisiones mías (propuestas, aceptadas sin objeción)

- Reposo en **verde menta**, para que el naranja signifique solo "Claude trabaja".
- Estado **"te necesita"** con burbuja "!", como Codex.

## Aspecto

El dragón es el chibi B dibujado con formas vectoriales WPF (`Path`, `Ellipse`), sin
imágenes. Partes animables: cuerpo (respira), alas (aletean), ojos (parpadean), manos
(teclean), cola. Accesorios que aparecen según el estado: lentes, laptop, burbuja de
pensamiento "…", burbuja "!", "z z", gota de sudor, destellos.

Paletas (cuerpo / sombra / ala / panza / púas):

| Paleta | Cuerpo | Sombra | Ala | Panza | Púas |
|---|---|---|---|---|---|
| Menta (reposo) | #8FD1A8 | #74BC8F | #5FA87E | #E8F6E0 | #4E9A6E |
| Dormido | #B7C4BC | #A2B2A8 | #93A69A | #EEF2EC | #8A9C91 |
| Claude | #F28C52 | #E57A45 | #E06E3E | #FFE3C2 | #D9653B |
| Antigravity | #5B9BE6 | #4A86D1 | #3F74C2 | #DDEBFB | #3566B0 |

Fusión: se dibuja el dragón naranja y encima el azul recortado a la mitad derecha.

Anillos: dos arcos concéntricos alrededor del dragón, con pista gris tenue. El arco va de
0 a 100 % de uso. **Solo se dibuja el anillo de una app abierta** (cambio pedido el 2026-11-19):
con la app cerrada, sus límites pueden gastarse en otro dispositivo y cualquier número sería
viejo. Cada anillo aparece y desaparece con un fundido. Si la app está abierta pero no hay dato
(cargando, error, sin login), su anillo se ve gris y vacío.

## Estados y prioridad

Se evalúa de arriba abajo; gana el primero que se cumple:

1. **Durmiendo**: ningún proceso de Claude ni de Antigravity. Paleta dormido, ojos
   cerrados, "z z", respiración lenta. Sin anillos (ninguna app abierta).
2. **Te necesita**: alguna app espera permiso o respuesta. Color de esa app (bicolor si
   son las dos), brazo levantado, burbuja "!".
3. **Trabajando**: color según quién trabaja (naranja, azul o bicolor), lentes.
   - Subestado **escribe** (laptop y tecleo) si Claude está usando una herramienta.
   - Subestado **piensa** (burbuja "…") si Claude está pensando entre herramientas.
   - Antigravity no distingue subestados: alterna piensa y escribe cada 4–6 s.
   - Los dos a la vez: bicolor con laptop.
4. **Listo**: durante 2 s después de que una app termina, salta contento. Luego, reposo.
5. **Reposo**: menta, parpadeo, respiración normal.

Capas que se suman a cualquier estado, según el anillo de 5 h más alto:

- 80 % o más: gota de sudor.
- 100 %: cara agotada (ojos en espiral o caídos) y la tarjeta muestra la cuenta atrás.
  También se pone agotado si cualquier límite **semanal** llega al 100 %, porque en ese
  caso esa app también queda bloqueada (decisión mía).

## Tarjeta al pasar el mouse

Aparece junto al dragón, del lado con espacio libre en pantalla. Contenido:

- **Claude** (plan, p. ej. Pro): sesión 5 h (% y "se reinicia en 2h 10m"), semana, y los
  límites semanales por modelo si la API los devuelve (Opus/Sonnet).
- **Antigravity** (plan): por cada grupo devuelto (Gemini, Claude y GPT, Argon), 5 h y
  semana con % y reinicio.
- Solo aparecen las secciones de las apps abiertas (igual que los anillos). Sin ninguna abierta,
  la tarjeta solo muestra la línea de estado y "Abre Claude o Antigravity para ver tus límites".
- Con Claude cerrado no se consulta su uso; al abrirlo se consulta enseguida y, si el último dato
  tiene más de 1 min, no se muestra hasta que llegue el nuevo.
- Línea de estado: "Claude: trabajando · Antigravity: cerrado", y "Actualizado hace 1 min".
- Si falta el login de Claude: "Inicia sesión: abre una terminal, escribe claude y luego /login".

## Fuentes de datos

### Procesos (abierto o cerrado)

Cada 5 s, `Process.GetProcessesByName`:
- Claude abierto si hay algún proceso `claude` (app de escritorio o CLI).
- Antigravity abierto si hay algún proceso `Antigravity`.

### Límites de Claude

- `GET https://api.anthropic.com/api/oauth/usage` con `Authorization: Bearer <accessToken>`
  y `anthropic-beta: oauth-2025-04-20`. Devuelve `five_hour`, `seven_day` y opcionales
  por modelo, con `utilization` (0–100) y `resets_at`.
- El token se lee de `~/.claude/.credentials.json` (`claudeAiOauth`).
- Si `expiresAt` ya pasó: refrescar con `POST https://platform.claude.com/v1/oauth/token`
  (`grant_type=refresh_token`, `client_id` de Claude Code `9d1c250a-e61b-44d9-88ed-5944d1962f5e`)
  y **reescribir el archivo de forma atómica**, conservando los demás campos. Antes de
  refrescar, releer el archivo por si Claude Code ya lo renovó.
- Si el refresco falla (401/400): estado "sin login", anillo gris, aviso en la tarjeta.
  No reintentar en bucle: reintentar solo cuando cambie el archivo de credenciales.
- Frecuencia: cada 5 min, y además ~20 s después de cada `Stop` de Claude, con un mínimo
  de 60 s entre llamadas. Si responde 429, esperar el doble.

### Actividad de Claude (hooks)

La mascota escucha en `http://127.0.0.1:47823/dragonpet`. En `~/.claude/settings.json` se
añaden hooks para estos eventos, que envían el JSON del evento a esa URL:
`UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PermissionRequest`, `Notification`,
`Stop`, `SubagentStop`, `SessionEnd`.

Estado por `session_id`:

| Evento | Estado de esa sesión |
|---|---|
| UserPromptSubmit, PostToolUse | piensa |
| PreToolUse | escribe; **te necesita** si `tool_name` es `AskUserQuestion` |
| PermissionRequest; Notification con `permission_prompt` o `elicitation_dialog` | te necesita |
| Notification `idle_prompt` | se ignora |
| Stop | listo, luego reposo |
| SessionEnd | se borra la sesión |

- Claude "trabaja" si alguna sesión está en piensa o escribe.
- Seguridad: si una sesión no manda eventos en 15 min, se considera parada.
- La mascota responde siempre `200` vacío y rápido. Nunca bloquea ni cambia decisiones
  de Claude Code.
- Si el puerto 47823 está ocupado, la mascota sigue funcionando sin actividad de Claude
  y lo indica en la tarjeta.
- **Instalación de hooks**: la mascota los añade al arrancar si faltan. Fusiona sin borrar
  nada del usuario y hace una copia `settings.json.dragonpet.bak` la primera vez. En el
  menú de la bandeja hay "Quitar hooks de Claude".
- Riesgo a verificar: si la mascota está cerrada, ¿Claude Code muestra un error por cada
  hook HTTP fallido? Si lo muestra, cambiar a hooks de tipo `command` que ejecutan
  `DragonPet.exe --hook` (sale en silencio y con código 0 si la mascota no está).

### Antigravity (límites y actividad)

- Descubrir el servidor local: buscar el proceso `language_server*` vía WMI, leer
  `--csrf_token` de su línea de comandos y sus puertos en escucha. Probar los puertos con
  HTTPS (certificado autofirmado, solo para 127.0.0.1) y quedarse con el que responde.
  Repetir el descubrimiento solo si cambia el PID o falla una llamada.
- Llamadas `POST /exa.language_server_pb.LanguageServerService/<Método>` con cuerpo `{}`,
  cabeceras `Content-Type: application/json`, `Connect-Protocol-Version: 1` y
  `X-Codeium-Csrf-Token`.
- **Límites**: `RetrieveUserQuotaSummary` cada 60 s. Grupos con buckets
  (`window` = `5h` o `weekly`, `remainingFraction`, `resetTime`).
  Uso = 100 × (1 − remainingFraction). Anillo azul = máximo uso entre buckets `5h`.
  Plan desde `GetUserStatus` (`planStatus.planInfo.planName`), una vez por arranque.
- **Actividad**: `GetAllCascadeTrajectories` cada 3 s mientras Antigravity está abierto.
  Trabaja si alguna conversación tiene `status` distinto de `CASCADE_RUN_STATUS_IDLE`.
  El valor exacto para "trabajando" o "esperando al usuario" **está sin verificar**: se
  confirma en vivo durante la implementación. Respaldo si no hay un estado útil: trabaja
  si `lastModifiedTime` de alguna conversación cambió en los últimos 10 s.
- Datos verificados el 2026-11-18 en esta PC: `RetrieveUserQuotaSummary`,
  `GetUserStatus` y `GetAllCascadeTrajectories` responden.

## Ventana y comportamiento

- Ventana WPF sin bordes, `AllowsTransparency`, `Topmost`, fuera de la barra de tareas y
  de Alt+Tab (`WS_EX_TOOLWINDOW`). Tamaño pequeño, unos 140 × 140 px a escala 100 %.
- Posición inicial: esquina inferior derecha, encima de la barra de tareas. Se arrastra
  con el botón izquierdo y la posición se guarda.
- Tarjeta: ventana o popup aparte, solo visible con el mouse encima. No roba el foco.
- Bandeja del sistema (`NotifyIcon`): Mostrar/ocultar, Iniciar con Windows (clave `Run`
  de HKCU), Quitar hooks de Claude, Salir.
- Una sola instancia (mutex con nombre).
- Ajustes en `%APPDATA%\DragonPet\settings.json` (posición, inicio con Windows).
- Interfaz en español.

## Presupuesto de recursos

- Animación con un solo temporizador y fotogramas limitados: 20 fps trabajando, 10 fps en
  reposo, 4 fps durmiendo. Nada de animaciones WPF a 60 fps.
- Un único `HttpClient` reutilizado. Sin bibliotecas externas salvo las del propio .NET.
- La consulta WMI (lenta) solo al descubrir el servidor de Antigravity, no en cada sondeo.
- Medir al final: RAM (Working Set) y CPU media en 5 minutos en reposo y trabajando.

## Verificación

- Pruebas unitarias de lo que es pura lógica: parsear respuestas de Claude y Antigravity
  (con muestras reales sin datos personales), máquina de estados y prioridad, fusión de
  `settings.json` sin pérdida.
- Pruebas en vivo:
  - Mandar un mensaje en Claude Code y comprobar piensa, escribe y listo.
  - Provocar una petición de permiso y comprobar "te necesita".
  - Poner a trabajar a Antigravity y observar los valores de `status`.
  - Los dos a la vez: fusión.
  - Cerrar ambas apps: duerme.
  - Cerrar la mascota con Claude Code abierto: comprobar que no aparecen errores molestos.
- Medición de recursos (ver arriba).

## Riesgos

- Endpoints no oficiales de Anthropic y Google: si cambian, el anillo queda gris con un
  aviso. Aislar cada fuente en su clase para arreglarla rápido.
- Refrescar el token de Claude Code: si en el futuro el usuario también usa la CLI, los
  dos comparten el archivo. Releer antes de refrescar y escribir de forma atómica.

## Lo que queda a criterio del constructor

- Estructura del proyecto y nombres de clases.
- Diseño visual exacto de la tarjeta (tamaños, tipografía; claro u oscuro según el tema
  de Windows) y forma exacta de la cara agotada y de la gota de sudor.
- Dónde se publica el `.exe` final y si se compila como archivo único.
