# Tarea para Antigravity: revisión independiente del núcleo de DragonPet

## Reglas (importantes)

1. **Solo puedes crear o modificar archivos dentro de esta carpeta: `help-by-AG/`.**
   No edites, muevas ni borres nada fuera de ella. Otro agente (Claude) está trabajando
   en el resto del proyecto al mismo tiempo.
2. **No ejecutes `dotnet build`, `dotnet test` ni comandos de git.** Comparten carpetas
   `bin/`, `obj/` y el índice de git con el otro agente y lo romperían. Esta tarea es de
   lectura y análisis.
3. No toques `~/.claude`, ni credenciales, ni la configuración de Antigravity.
4. Escribe todo tu resultado en `help-by-AG/review-core.md`.

## Contexto del proyecto

DragonPet es una mascota de escritorio para Windows 11 (C#, .NET 10, WPF): un dragón chibi
que muestra con dos anillos el uso del límite de 5 horas de Claude y de Antigravity, una
tarjeta con el detalle al pasar el mouse, y anima si Claude Code o Antigravity están
trabajando (lentes, teclea o piensa; naranja = Claude, azul = Antigravity, bicolor = ambos).
Debe gastar muy pocos recursos (< 60 MB de RAM y < 1 % de CPU en reposo) y nunca debe
romper ni ensuciar Claude Code ni el `settings.json` del usuario.

Lee primero, en este orden:
- `docs/superpowers/specs/2026-11-18-dragon-pet-design.md` (qué debe hacer; es la autoridad)
- `docs/superpowers/plans/2026-11-18-dragon-pet.md` (cómo se construye; tareas 2 a 8)
- `docs/superpowers/audits/audit-log.md` (qué se revisó ya y qué riesgos se conocen)

Código a revisar (ya implementado y con tests en verde):
- `src/DragonPet.Core/*.cs`: parsers, trackers de actividad, máquina de estados, tarjeta,
  instalador de hooks, credenciales y cliente de uso de Claude, programador de sondeos,
  servidor HTTP mínimo de hooks y comprobación de procesos
- `tests/DragonPet.Core.Tests/*.cs`

## Lo que te pido

Una revisión de código como la haría un revisor senior que no escribió el código. Busca
sobre todo:

1. **Bugs reales**: entradas o secuencias de eventos que producen un resultado incorrecto,
   un cuelgue o una excepción no capturada. Por ejemplo: carreras entre hilos en
   `ClaudeActivityTracker` y `HookServer`, casos límite de fechas o zonas horarias en
   `TimeFormat` y `JsonRead.Time`, o el `settings.json` del usuario quedando mal.
2. **Desviaciones del spec**: algo que el spec pide y el código no hace, o hace distinto.
3. **Seguridad y privacidad**: tokens que podrían filtrarse, archivos del usuario que
   podrían perderse o corromperse.
4. **Recursos**: fugas de memoria, handles o tareas; cosas caras que se repiten cada segundo.
5. **Huecos de test**: comportamiento importante que ningún test cubre.

No hace falta comentar estilo ni nombres salvo que confundan.

## Formato de `help-by-AG/review-core.md`

Para cada hallazgo, ordenados de más a menos grave:

```
### N. Título corto
- Gravedad: Crítico | Importante | Menor
- Archivo: ruta:línea
- Escenario: entrada o secuencia concreta → resultado incorrecto
- Por qué importa: efecto para la persona que usa la mascota
- Arreglo sugerido: una o dos líneas
- Test que lo demostraría: nombre y aserción principal
```

Al final, una sección "Cosas que revisé y están bien", con una línea por área.
Si no encuentras nada importante, dilo claramente. No inventes hallazgos para rellenar.
