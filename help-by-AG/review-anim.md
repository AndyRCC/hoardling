# Revisión independiente del sistema de animación de DragonPet

Revisión de solo lectura de `Motion.cs`, `GestureScheduler.cs`, `ColorTransition.cs`, `DragonRenderer.cs`, `DragonView.cs`, `DragonArt.cs`, sus tests unitarios y la hoja de fotogramas `out/gestures.png`, frente a `docs/superpowers/specs/2026-11-19-dragon-animations-design.md` y `docs/superpowers/audits/audit-log.md`.

---

## Hallazgos

### 1. El primer fotograma tras un cambio de estado salta hasta un 74 % de golpe por reutilizar el `dt` del estado anterior
- Gravedad: Importante
- Archivo: `src/DragonPet/Rendering/DragonRenderer.cs:131-143`, `189-201` y `src/DragonPet/Rendering/DragonView.cs:35-39`, `69-73`
- Escenario: El dragón está en `Sleeping` a 4 fps (ticks cada `0,25 s`; último tick en `t = 10,00`). En `t = 10,24`, Claude empieza a trabajar → `Visual` cambia a `Thinking` → `InvalidateVisual()` dispara `OnRender` en `t = 10,24` → `Animate` calcula `dt = Math.Clamp(10,24 - 10,00, 0, 0,25) = 0,24 s` y pasa ese `dt` entero a `AnimateWeights` con los objetivos **nuevos** (`FadeTime = 0,18 s`) → `1 - exp(-0,24 / 0,18) = 0,736`. En el primer fotograma, los lentes saltan el 74 % del recorrido, la burbuja de pensamiento salta directo a `OutBack(0,74) = 1,07` (ya por encima del 100 %), los ojos se abren un 74 % y `"z z"` cae al 26 %. Lo mismo pasa en `Idle → Typing/NeedsYou` (hasta un 43 % en el primer fotograma a 10 fps) y al volver de ocultar la ventana o suspender la PC (`dt = 0,25 s` → 75 %).
- Por qué importa: Aunque `_colors` y `_reactionStart` sí arrancan en `0`, todos los accesorios, ojos, bocas y el brazo levantado dan un salto visible justo en el primer fotograma de cada transición desde reposo o sueño.
- Arreglo sugerido: Cuando cambie cualquier objetivo de peso (`Pose`, `Palette`, `Glasses`, `Sweat` o `Exhausted`), aplicar `dt` a las fases continuas pero pasar `weightDt = 0` a `AnimateWeights` en ese fotograma (marcando `_moving = true` si algún peso difiere de su objetivo), para que el timer a 20 fps arranque suavemente desde 0 % en pasos de `0,05 s`.
- Test que lo demostraría: `StateChange_AfterSlowTick_StartsFadeFromZero`: `Animate(Sleeping, 0)` → `Animate(Thinking, 0.24)` → comprobar que los pesos de lentes/burbuja siguen en `0` (no `0,74`) y tras un tick de `0,05 s` avanzan ~24 %.

### 2. Un gesto interrumpido por cambio de estado se corta en seco en 1 fotograma (incumple el spec)
- Gravedad: Importante
- Archivo: `src/DragonPet.Core/GestureScheduler.cs:63-69` y `src/DragonPet/Rendering/DragonRenderer.cs:139`, `217-280`, `320-356`
- Escenario: El spec (§2) indica: «Si el estado cambia, el gesto en curso se corta suavemente». Sin embargo, `GestureScheduler.Update` cambia `_current = Gesture.None` al instante cuando `pose != _pose`, y `DragonRenderer` calcula todas las propiedades de gesto directamente desde `_gesture`: cortar `FireBurp` en `p = 0,5` baja `_flame.Opacity` de `1 → 0`, `_cheeks` de `1,4 → 1,0` y `_mouthOpen` de `1 → 0` en un solo fotograma; cortar `Hop` en el pico teletransporta al dragón 9 px hacia abajo; cortar `SnoreBubble`, `Yawn`, `WingStretch`, `TailWag` o `LookAround` hace desaparecer la burbuja/boca o devolver alas/cola/ojos a `0` de golpe.
- Por qué importa: Cada vez que Claude o Antigravity arrancan justo mientras el dragón eructa fuego, bosteza, salta o tiene la burbuja de ronquido, el gesto desaparece con un corte brusco en vez de fundirse.
- Arreglo sugerido: Al cancelarse un gesto no nulo, guardarlo como gesto saliente con un peso que decaiga de `1 → 0` en ~`0,15 s` (manteniendo `_moving = true`), o suavizar las salidas de gesto (`hop`, `_tilt`, `_tail`, `_look`, `_cheeks`, opacidades de efectos y bostezo) mediante `Motion.Approach(..., dt, 0.06)`.
- Test que lo demostraría: `PoseChange_MidFireBurp_FadesOutInsteadOfSnapping`: `Animate(Idle, 1.0, new GestureState(Gesture.FireBurp, 0.5))` seguido de `Animate(Thinking, 1.05)` mantiene la opacidad de la llama entre `0,2` y `0,9` en vez de `0`.

### 3. `GestureScheduler`: tras suspender la PC o ocultar la ventana, `_next = _end + pause` queda en el pasado y dispara otro gesto al instante sin pausa
- Gravedad: Importante
- Archivo: `src/DragonPet.Core/GestureScheduler.cs:74-92`
- Escenario: Un gesto empieza en `t = 100` (`_end = 103,2`). En `t = 101`, el usuario oculta el dragón (`DragonView._timer.Stop()` mientras `Stopwatch` sigue corriendo) o suspende el equipo y vuelve en `t = 500`. Al llamarse `Update(Sleeping, 500)`: `500 < 103,2` es `false` → línea 81 asigna `_next = 103,2 + Uniform(7, 15) ≈ 113` → línea 85 evalúa `500 < 113` (`false`) → línea 89 inicia **de inmediato** otro gesto en el mismo fotograma sin pasar por `Gesture.None` ni esperar pausa. Además, si `now < _start` o `now` es `NaN`, la línea 76 devuelve `Progress` negativo o elige un gesto nuevo cada llamada.
- Por qué importa: Al volver de ocultar el dragón o reanudar la PC tras suspender, el planificador puede encadenar gestos sin pausa (o devolver `Progress` fuera de `[0, 1]` ante anomalías del reloj).
- Arreglo sugerido: Calcular `_next = now + Uniform(table.Pause.Min, table.Pause.Max)` (desde `now`, no desde `_end`) y retornar `default` al cerrar el gesto anterior; ignorar `!double.IsFinite(now)` y acotar `Progress` con `Math.Clamp(..., 0, 1)`.
- Test que lo demostraría: `AfterLongClockJump_WaitsFullPauseBeforeNextGesture`: iniciar gesto en `t = 5`, llamar `Update(Sleeping, 500)` y afirmar `Kind == Gesture.None`, sin ningún gesto nuevo antes de `t = 507`.

### 4. `DragonRenderer.Animate` muta ~50 propiedades DP (con boxing) y ensucia subárboles invisibles en cada fotograma; `_sparkles` fuerza una capa offscreen en `SoftwareOnly`
- Gravedad: Importante
- Archivo: `src/DragonPet/Rendering/DragonRenderer.cs:152-356`, `401-402`, `543-549`
- Escenario:
  1. En cada fotograma (incluso en `Idle` o `Sleeping` sin gestos), `AnimateAccessories` sigue asignando valores **distintos en cada tick** a `_handLeft/Right.Y`, `_dots[0..2].Opacity`, `_sweatMove.Y` y `_zee1/2` aunque su `DrawingGroup` padre tenga `Opacity == 0`. Esos eventos `Freezable.Changed` suben hasta `_scene` y obligan a WPF a recorrer el árbol cada tick. Además, las ~42 asignaciones de `double` y 6 de `Matrix` boxean ~1,3 KB por fotograma (~13–27 KB/s) antes de que WPF compruebe si el valor cambió.
  2. `_sparkles` es un `DrawingGroup` de ~102×70 px (`SparkleSpots` de `(16,40)` a `(106,88)`) cuya `Opacity` oscila en `[0,3, 1,0)` de forma continua en `Fusion`, lo que obliga a WPF en `RenderMode.SoftwareOnly` a crear y componer una superficie intermedia offscreen 20 veces por segundo.
  3. `Body(_right, clip: RightHalfClip)` rasteriza siempre 11 geometrías recortadas (incluyendo cola, ala, pie y cuerno izquierdos que quedan 100 % fuera de `RightHalf`) incluso cuando `_left` y `_right` tienen exactamente los mismos colores.
- Por qué importa: Contradice el objetivo de cero asignaciones por fotograma y añade trabajo innecesario de CPU y GC en `RenderMode.SoftwareOnly` (presupuesto `< 1 %` CPU y `< 60 MB`).
- Arreglo sugerido: Omitir actualizaciones internas de grupos con peso `0` (`_wLaptop`, `_wThink`, `_wSweat`, `_wZzz`, efectos inactivos) y evitar reescribir propiedades sin cambio; mover la opacidad pulsante de los destellos a un `SolidColorBrush` en vez de `_sparkles.Opacity`; y poner `rightBody.Opacity = 0` cuando las paletas izquierda y derecha sean iguales.
- Test que lo demostraría: Medir con `GC.GetAllocatedBytesForCurrentThread()` un bucle de 1000 llamadas a `Animate(idle, t)` en estado estable (debe quedar cerca de 0 B en los grupos inactivos) y verificar visualmente `--render-transitions` en `Fusion`.

### 5. `DragonView.Visual`: actualizar los anillos en mitad de un gesto baja el timer de 20 fps a 4/10 fps, y cambios de `Sweat`/`Exhausted`/`Glasses` no suben a 20 fps
- Gravedad: Importante
- Archivo: `src/DragonPet/Rendering/DragonView.cs:26-41`
- Escenario:
  1. Mientras el dragón duerme ejecutando `SnoreBubble` (o en `Idle` ejecutando `FireBurp`) a 20 fps, llega un sondeo de cuota que cambia `ClaudeRing` de `38` a `39`. `lookChanged` (`Palette`/`Pose`) es `false`, así que la línea 38 ejecuta `SetInterval(IntervalFor(Sleeping))`, **bajando el timer de 20 fps a 4 fps** (y reiniciando la espera a 250 ms) justo en mitad del gesto.
  2. Si cambia `Sweat`, `Exhausted` o `Glasses` sin cambiar `Palette`/`Pose`, `lookChanged` es `false` y el fundido arranca a 4/10 fps hasta el siguiente `Tick()`.
  3. Además, `InvalidateVisual()` se llama en cualquier cambio de `Visual`, re-grabando `Render` y recreando las `StreamGeometry` de los anillos aunque los anillos no hayan cambiado.
- Por qué importa: Un sondeo periódico de cuota (cada 60 s en Antigravity o tras `Stop` en Claude) provoca un tirón visible si coincide con un gesto o transición, y los cambios de pose reconstruyen los arcos sin necesidad.
- Arreglo sugerido: Llamar a `InvalidateVisual()` solo si cambiaron `ClaudeRing`, `AgRing` o `RingsDimmed`; mantener `BlendInterval` (20 fps) si cambió cualquier propiedad visual distinta de los anillos o si `_renderer.NeedsFastFrames(_clock.Elapsed.TotalSeconds)` ya es `true`.
- Test que lo demostraría: `Visual_RingChangeDuringActiveGesture_Keeps20Fps`: con `SnoreBubble` activo en `Sleeping` (intervalo 50 ms), actualizar `ClaudeRing` y comprobar que el intervalo del timer sigue en 50 ms.

### 6. Doble boca al bostezar o eructar fuego estando agotado (`Exhausted`), y salto en el parpadeo (`BlinkScale`) al cambiar de pose
- Gravedad: Importante
- Archivo: `src/DragonPet/Rendering/DragonRenderer.cs:256-257`, `270-275`
- Escenario:
  1. Con `v.Exhausted == true` (límite al 100 %) en `Idle` —especialmente en AFK, donde `Yawn` y `FireBurp` suman el 60 % de los gestos—, `_wMouthWavy = 1` y `_wSmile = 0`. La línea 270 atenúa solo `_mouthSmile` por `(1 - yawn) * (1 - burpMouth)`, pero la línea 273 deja `_mouthWavy.Opacity = 1`, dibujando la boca ondulada **encima** de la boca de bostezo o de eructo.
  2. En la línea 256, `blink = _lastPose is Idle/Thinking/Typing ? BlinkScale(t) : 1`. Si la pose cambia a `NeedsYou`/`Happy`/`Sleeping` en mitad de un parpadeo (`BlinkScale == 0,12`), `_blink.ScaleY` salta de `0,12` a `1,0` en un fotograma (y al volver a `Idle` durante `phase ≈ 0,965`, salta de `1,0` a `0,12`).
- Por qué importa: Con algún límite al 100 %, cada bostezo o eructo en reposo muestra dos bocas superpuestas; y cambiar de estado durante un parpadeo da un destello seco en los ojos.
- Arreglo sugerido: Multiplicar `_mouthWavy.Opacity` (y `_mouthHappy.Opacity`) por `(1 - yawn) * (1 - burpMouth)`; suavizar `_blink.ScaleY` o gestionar el parpadeo con una fase propia que termine su ciclo de `0,3 s` sin cortarse.
- Test que lo demostraría: Preview/test con `Exhausted = true` y `Gesture.Yawn` (`p = 0,5`): afirmar `_mouthWavy.Opacity == 0` y `_mouthYawn.Opacity == 1`; y test de cambio de pose en `t = 4.2 * 0.965` comprobando que `_blink.ScaleY` no salta más de `0,2` en un fotograma.

### 7. `_cheeks` comparte un único `ScaleTransform` centrado en `(60, 61)`, sacando las mejillas hacia fuera de la cabeza en `FireBurp`
- Gravedad: Importante
- Archivo: `src/DragonPet/Rendering/DragonRenderer.cs:50`, `262-263`, `485-489` (visible en `out/gestures.png`, fila 1, fotogramas 1–2)
- Escenario: Un solo `ScaleTransform(1, 1, 60, 61)` envuelve tanto `CheekLeft` (centro `44, 61`) como `CheekRight` (centro `76, 61`). Al inflar con `ScaleX = 1,4`, cada mejilla se desplaza `6,4 px` hacia afuera (`x = 37,6` y `x = 82,4`, borde exterior `88,7 > 87` del contorno de la cabeza), separándose visualmente del rostro antes del eructo.
- Por qué importa: Se ve en `out/gestures.png` (fila 1, fotogramas 1–2): en vez de inflarse en su sitio, los coloretes se deslizan hacia los bordes de la cabeza.
- Arreglo sugerido: Usar dos `ScaleTransform` independientes centrados en `(44, 61)` y `(76, 61)` para `CheekLeft` y `CheekRight` respectivamente.
- Test que lo demostraría: Inspección visual de `out/gestures.png` fila 1 (fotogramas 1–2) verificando que las mejillas se agrandan en su posición sin salirse de `Head`.

### 8. `Motion` y `ColorTransition` propagan `NaN` ante entradas no finitas y `ColorTransition.Sample` recalcula OKLCh en canales sin cambio
- Gravedad: Menor
- Archivo: `src/DragonPet.Core/Motion.cs:7-35` y `src/DragonPet.Core/ColorTransition.cs:22`, `28`, `56-59`
- Escenario:
  1. Pasar `double.NaN` a `Motion.Approach` (`dt` o `timeConstant`), `Bell`, `OutBack`, `Window` o a `ColorTransition` (`durationSeconds` o `Ease`) salta las comprobaciones `<= 0`/`>= 1` (`Math.Clamp(NaN, 0, 1)` devuelve `NaN`) y retorna `NaN`. En `DragonRenderer.AnimateBody`, un solo `NaN` envenena permanentemente `_bobPeriod`, `_bobAmplitude` y `_wingAmplitude`.
  2. En `ColorTransition.Sample` (línea 58), durante cualquier transición hacia o desde `Fusion` (5 de los 10 canales tienen `_from[i] == _to[i]`), `Blend` igualmente ejecuta las conversiones completas sRGB ↔ OKLCh (`Pow`/`Cbrt`/`Atan2`/`Cos`/`Sin`) sobre colores idénticos.
- Por qué importa: Las utilidades de `DragonPet.Core` son matemáticas puras que deben ser inmunes a `NaN`, y el atajo `_from[i] == _to[i]` ahorra la mitad del cálculo OKLCh en transiciones de fusión.
- Arreglo sugerido: Usar comparaciones seguras frente a `NaN` (`!(dt > 0)`, `!(p > 0 && p < 1)`, `double.IsFinite`) en `Motion` y `ColorTransition`; en `Sample`, añadir `_from[i] == _to[i] ? _to[i] : Blend(...)`.
- Test que lo demostraría: `Motion_NaNInputs_ReturnFiniteFallback` y `ColorTransition_UnchangedChannel_StaysExact`.

### 9. Calidad de lectura y curvas en `out/gestures.png`: humo casi invisible, `Hop` aplasta en el aire, `LookUp` toca los lentes y `Mumble` apenas se distingue
- Gravedad: Menor
- Archivo: `src/DragonPet/Rendering/DragonRenderer.cs:217`, `224`, `238`, `259`, `277-279`, `613-619` y `src/DragonPet/Rendering/DragonArt.cs:104`, `128-131` (visible en `out/gestures.png`, filas 1, 4, 6, 7, 8, 9, 11 y 13)
- Escenario (observado directamente en `out/gestures.png`):
  1. **Humo (`FireBurp` fila 1 y `SmokePuff` fila 8):** `SmokeFill = #F2F6F8FA` con borde `0,35 px` es prácticamente invisible sobre fondos claros (`WhiteSmoke`).
  2. **`Hop` (filas 6 y 13):** `landing` aplasta en `p ∈ [0,8, 1,0]` con pico en `p = 0,9`, cuando `hop = 9 * Bell(0,9) = 2,8 px` sigue en el aire, y no hay anticipación antes del salto.
  3. **`Mumble` (fila 9):** escalar `Smile` (8×2 px) `±30 %` desplaza los extremos `~1 px`, casi ilegible a escala 100 %.
  4. **`LookUp` con lentes (fila 11):** `_look.Y = -2,5` sube el borde superior del ojo (`42,0`) hasta chocar con el aro superior de los lentes (`42,5`, trazo 2 px).
  5. **`SnoreBubble` (fila 7), `TailWag` (fila 4) y `SetArm`:** `SnoreNostril (66, 60)` está desplazado `(3, 2)` px respecto a la fosa nasal derecha `(63, 58)` y `p ∈ [0,9, 1,0]` mantiene 20 fps durante `0,32 s` sin nada en pantalla; `TailWag` (`±14°`) es muy discreto en tamaño real; `SetArm` rota el brazo alrededor de su centro `(0, 0)` en lugar de su hombro.
- Por qué importa: En el escritorio a escala 100 % (170 px), `SmokePuff` y `Mumble` pasan casi desapercibidos y `Hop`/`LookUp` pierden limpieza visual.
- Arreglo sugerido (valores concretos):
  - Humo: `SmokeFill = #D9C6D2DC`, `SmokePen = P("#788694", 0.65)`. Burbuja: `SnoreNostril = (63, 58)`, `BubblePenThin = P("#5FAEE0", 0.14)`, estallido en `[0.92, 1.0]`.
  - `Hop`: anticipación en `Window(p, 0, 0.16)` (`ScaleY -0.06`), salto en `Window(p, 0.14, 0.78)` (`9 px`) y aplastón de aterrizaje ya en el suelo en `Window(p, 0.76, 1.0)`.
  - `Mumble`: combinar `ScaleY ±0.5` con un pulso suave de `_mouthOpen` (`0.45 * Math.Abs(mumble)`) y `_tilt` `±1.5°`.
  - `LookUp`: `_look.Y = -1.8 * plateau` acompañando `_glassesMove.Y` con `-0.8 * plateau` cuando `_wGlasses > 0`; `TailWag` a `±20°`; en `SetArm`, rotar alrededor del hombro `(0, 4)` antes de trasladar.
- Test que lo demostraría: Regenerar `out/gestures.png` con `--render-gestures` y comprobar visualmente las filas 1, 4, 6, 7, 8, 9, 11 y 13.

### 10. Huecos de cobertura de tests en `Motion`, `GestureScheduler` y `ColorTransition`
- Gravedad: Menor
- Archivo: `tests/DragonPet.Core.Tests/MotionTests.cs`, `GestureSchedulerTests.cs`, `ColorTransitionTests.cs`
- Escenario: Faltan pruebas para casos importantes ya implementados:
  - `MotionTests`: independencia del framerate (`Approach` en 2 pasos de `dt/2` igual a 1 paso de `dt`), rama `Window` con `end <= start` (línea 35) y entradas `NaN`/`Infinity`.
  - `GestureSchedulerTests`: salto de reloj tras suspender/ocultar, `now` decreciente/`NaN`, reinicio del temporizador AFK al salir y volver a `Idle`, y verificar que **todos** los gestos permitidos de cada pose llegan a elegirse (hoy `OnlyGesturesThatFitThePose` pasaría aunque solo saliera uno) y que `DurationOf(g) > 0` para todo `Gesture != None`.
  - `ColorTransitionTests`: `Sample`/`IsAnimating` antes de `SetTarget`, cruce del tono por `±π` (p. ej. rojo ↔ magenta por el arco corto), mezcla con color acromático (`c < 0.02`, como `Sleepy.Belly`), `channels <= 0` y `durationSeconds <= 0`.
- Por qué importa: Son las invariantes que previenen regresiones silenciosas en pesos, AFK y mezcla de colores.
- Arreglo sugerido: Añadir un test unitario focalizado para cada caso en los tres archivos de test.
- Test que lo demostraría: `Approach_FrameRateIndependent`, `ReturnToIdle_ResetsAfkTimer`, `AllAllowedGestures_AppearOverLongRun`, `HueWrap_RedToMagenta_TakesShortArc` y `Sample_BeforeSetTarget_Safe`.

---

## Cosas que revisé y están bien

- **Mezcla perceptiva en OKLCh (`ColorTransition`)**: los coeficientes de la matriz Oklab directa e inversa son exactos, `Math.IEEERemainder` toma siempre el arco corto del tono, los extremos `0` y `1` devuelven el ARGB exacto sin redondeo y el cambio de objetivo a mitad de transición arranca desde el color visible sin saltos (se ve impecable en las filas 14 y 17 de `out/gestures.png`).
- **Transiciones principales en `out/gestures.png` (filas 14–17)**: entrada/salida de lentes y laptop, aparición con *overshoot* de las burbujas `"…"` y `"!"`, fundido de ojos abiertos ↔ cerrados y mezcla de colores menta → dorado → naranja se leen muy limpios y expresivos.
- **Gestos destacados (`FireBurp` llama, `Yawn`, `WingStretch`, `AdjustGlasses`)**: la silueta y orientación de la llama del eructo, la boca con lengua del bostezo, el estirón de alas y el ajuste de lentes funcionan muy bien visualmente a escala chibi.
- **Estabilidad de `NeedsFastFrames`**: `_moving` se reinicia en cada `AnimateWeights` y `Fade` redondea al objetivo con tolerancia `0,004` (~1 s con `FadeTime = 0,18 s`), `_colors.IsAnimating` tiene tolerancia `1e-9` y `_reactionStart` inicia en `-∞`, así que `NeedsFastFrames` siempre vuelve limpiamente a `false` y el timer regresa a 10 fps / 4 fps.
- **Escena retenida (`DragonRenderer.BuildScene` y `DragonArt`)**: toda la geometría de `DragonArt` está congelada (incluidos `LaptopScreen`, `ThinkPill`, `AlertBox` y `AlertMark`), `Render` no reconstruye el árbol del dragón, y el orden de `TransformGroup` (`_squash → _tilt → _bob` y `_blink → _look`) es correcto.
- **Determinismo y tablas de `GestureScheduler`**: acepta `Random` inyectado, respeta las tablas y rangos de pausa del spec para los 6 estados (sin gestos en `Happy`) y sube claramente la proporción de bostezos y fuego tras `AfkAfter = 120 s`.
