# Tarea 2 para Antigravity: revisión de la capa de Windows de DragonPet

Las mismas reglas que en `help-by-AG/TASK.md`: **solo escribe dentro de `help-by-AG/`**, no
ejecutes `dotnet build`, `dotnet test` ni git, y no toques `~/.claude` ni credenciales.
Escribe el resultado en `help-by-AG/review-app.md`, con el mismo formato de hallazgos.

Gracias por la revisión anterior: se arreglaron 17 de 19 hallazgos. El resumen de qué se
aceptó y qué no (y por qué) está al final de `docs/superpowers/audits/audit-log.md`.
Léelo para no repetir lo ya resuelto.

## Qué revisar ahora

`src/DragonPet/` (WPF, .NET 10, Windows 11):
- `App.xaml.cs`: arranque, instancia única, hooks, cableado de ventanas, bandeja y coordinador, salida
- `Services/PetCoordinator.cs`: bucle de 1 s en segundo plano, sondeos y publicación a la UI
- `Services/AntigravityClient.cs`, `Services/LanguageServerLocator.cs`: WMI y GetExtendedTcpTable
- `Services/SettingsStore.cs`, `Services/StartupRegistry.cs`
- `Windows/PetWindow.cs`, `Windows/CardWindow.cs`, `Windows/TrayIcon.cs`, `Windows/NativeMethods.cs`
- `Rendering/DragonRenderer.cs`, `Rendering/DragonView.cs`, `Rendering/DragonArt.cs`, `Rendering/DragonIcon.cs`

## En qué fijarte sobre todo

1. **Hilos**: nada de la UI debe tocarse fuera del Dispatcher. Busca carreras entre el bucle
   del coordinador, el servidor de hooks y la UI.
2. **Recursos** (el objetivo es menos de 60 MB de RAM y menos de 1 % de CPU en reposo):
   fugas de handles (HICON, HwndSource, Process, ManagementObject), objetos creados por
   fotograma, timers que siguen corriendo con la ventana oculta, y trabajo caro repetido cada
   segundo.
3. **P/Invoke**: estructuras mal declaradas (`NOTIFYICONDATAW`, `MIB_TCPROW_OWNER_PID`),
   uso de `GetWindowLongPtr` y `SetWindowLongPtr`, y el icono creado con
   `CreateIconFromResourceEx`.
4. **Comportamiento de escritorio**: la ventana no debe robar el foco ni aparecer en la barra
   de tareas ni en Alt+Tab; la tarjeta no debe interceptar clics; arrastrar debe guardar la
   posición; con varios monitores y DPI distintos debe colocarse bien.
5. **Cierre**: salir desde la bandeja debe liberar el icono, el puerto 47823 y el mutex sin
   colgarse.

No hace falta comentar estilo. Si algo está bien, dilo en la sección final.
