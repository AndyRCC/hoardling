# Tarea 4 para Antigravity: auditoría previa al lanzamiento en GitHub

Mismas reglas de siempre: **solo escribe dentro de `help-by-AG/`**, no ejecutes `dotnet build`,
`dotnet test` ni git, y no toques `~/.claude`, credenciales ni la configuración de Antigravity.
Resultado en `help-by-AG/review-release.md`.

Gracias por las tres revisiones anteriores: casi todo se aplicó. Mira el final de
`docs/superpowers/audits/audit-log.md` para no repetir lo ya resuelto.

## Contexto

El usuario quiere publicar DragonPet como repositorio en GitHub. Revisa el proyecto **completo**
con ojos de quien lo va a descargar y usar en su propio Windows, y de quien va a leer el
código. Esta vez, además de encontrar problemas, **propón el arreglo exacto**: un parche en
formato diff unificado o el bloque de código final, para que el otro agente lo aplique y lo
verifique con tests.

Partes nuevas desde tu última revisión, a las que conviene dedicar más atención:
- `src/DragonPet/Services/JobEscape.cs` y su uso en `App.xaml.cs`: se relanza fuera del *job
  object* de quien lo abrió (WMI `Win32_Process.Create` y, como respaldo,
  `CREATE_BREAKAWAY_FROM_JOB`) para no morir cuando se cierra Claude o una terminal.
- `App.xaml.cs`: hooks de Claude Code instalados solo mientras la mascota corre; `--quit`,
  `--log`, `--probe-ag`, `--render-*`; mutex de instancia única; `SessionEnding`.
- `src/DragonPet/Services/PetCoordinator.cs`: dos bucles (sondeo y publicación).
- `src/DragonPet.Core/ProcessCheck.cs`: Antigravity cuenta como abierto si existe su
  `language_server` en una ruta que contiene `\antigravity\`.
- `src/DragonPet.Core/ClaudeActivityTracker.cs`: se ignoran `SubagentStop` y los eventos con
  `agent_id` (un fork en segundo plano de la app de escritorio los enviaba después de cada `Stop`).

## Qué revisar

1. **Bugs** en todo `src/` y en los tests: casos borde, hilos, recursos y fallos al instalar
   en otra PC (otra ruta de Antigravity, sin Antigravity, sin Claude Code, sin `.claude`, otro
   idioma de Windows, varios monitores, escala de DPI distinta, usuario sin permisos de WMI).
2. **Seguridad:** servidor HTTP local en `127.0.0.1:47823`, que cualquier proceso local podría
   llamar; relanzamiento por WMI; lectura y escritura de `~/.claude/.credentials.json` y de
   `~/.claude/settings.json`; validación TLS hacia el servidor local de Antigravity; inyección en
   la línea de comandos al relanzar.
3. **Higiene para un repositorio público:**
   - datos personales del autor en cualquier archivo versionado (nombres de usuario, rutas
     `C:\Users\…`, correos, identificadores de sesión, IDs de organización);
   - secretos;
   - `.gitignore`;
   - que se pueda compilar desde cero solo con el README;
   - archivos de diagnóstico o muestras que no deberían publicarse;
   - falta de LICENSE (indícalo; la licencia la elige el usuario).
4. **Documentación:** que el README describa bien qué toca el programa en la PC del usuario,
   con advertencias claras: endpoints no oficiales, modifica `settings.json`, renueva tokens.
5. **Huecos de test** importantes que convenga cerrar antes de publicar.

Ordena por gravedad y no inventes hallazgos. Al final, una sección "Listo para publicar"
con lo que ya está bien.
