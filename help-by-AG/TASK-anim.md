# Tarea 3 para Antigravity: revisión del nuevo sistema de animación

Mismas reglas que en `help-by-AG/TASK.md`: **solo escribe dentro de `help-by-AG/`**, no
ejecutes `dotnet build`, `dotnet test` ni git, y no toques `~/.claude`. Escribe el resultado en
`help-by-AG/review-anim.md`, con el mismo formato de hallazgos (gravedad, archivo:línea,
escenario, por qué importa, arreglo y test).

Gracias por las dos revisiones anteriores: se aplicaron casi todas. El resumen está en
`docs/superpowers/audits/audit-log.md`.

## Contexto

El usuario pidió que las animaciones no cambien de golpe, que el dragón haga gestos mientras
duerme y que en cada estado (sobre todo AFK) haga cosas como eructar fueguito. El diseño está
en `docs/superpowers/specs/2026-11-19-dragon-animations-design.md`. Léelo primero.

Código nuevo o reescrito:
- `src/DragonPet.Core/Motion.cs`: suavizado exponencial, curvas, rebote
- `src/DragonPet.Core/GestureScheduler.cs`: elige gestos por estado con pausas aleatorias y modo AFK
- `src/DragonPet.Core/ColorTransition.cs`: mezcla de colores en OKLCh
- `src/DragonPet/Rendering/DragonRenderer.cs`: escena retenida de `DrawingGroup`, pesos que se
  funden, fases acumuladas, squash and stretch y efectos (fuego, humo, burbuja de ronquido, bostezo)
- `src/DragonPet/Rendering/DragonView.cs`: timer, y 20 fps solo durante movimiento
- `src/DragonPet/Rendering/DragonArt.cs`: geometrías nuevas
- Tests: `tests/DragonPet.Core.Tests/MotionTests.cs`, `GestureSchedulerTests.cs`, `ColorTransitionTests.cs`

Hay una hoja de fotogramas en `out/gestures.png`: una fila por gesto y 4 filas de transiciones
al final. El orden de las filas está en `Diagnostics.RenderGestures`.

## En qué fijarte

1. **Saltos visibles:** cualquier propiedad que pueda cambiar de golpe entre fotogramas al
   cambiar de estado o al empezar o terminar un gesto (por ejemplo, un gesto cortado a la mitad
   por un cambio de estado; parpadeo o bostezo; el brazo que se levanta; ojos y bocas).
2. **Rendimiento** (objetivo: < 1 % de CPU y < 60 MB): asignaciones por fotograma, opacidades
   intermedias que obliguen a capas caras, que el timer se quede a 20 fps sin necesidad, que
   `NeedsFastFrames` nunca vuelva a false, y el trabajo en `Animate`.
3. **Corrección:** `GestureScheduler` (bordes de tiempo, reloj que salta tras suspender la PC,
   AFK), `Motion` y `ColorTransition` (números raros o NaN), y que la escena retenida no comparta
   un `Transform` de forma problemática.
4. **Calidad de la animación:** si algún gesto se lee mal (por los fotogramas) o tiene una curva
   pobre, propón valores concretos.
5. **Huecos de test** importantes.

Si algo está bien, dilo en la sección final. No inventes hallazgos.
