# Dragón mascota: animaciones vivas (addendum al diseño)

> Nota (2026-11-20): el proyecto se llama ahora **Hoardling**. Este documento conserva el nombre
> original (DragonPet), sus rutas (`src/DragonPet…`) y su URL de hooks (`/dragonpet`) tal como eran
> al escribirlo.

Fecha: 2026-11-19 · Pedido del usuario: "las animaciones cambian de golpe, hazlas más
dinámicas; cuando duerma, que haga gestos; en cada estado, sobre todo si estoy AFK, que haga
algo (por ejemplo eructar fueguito)". Las decisiones concretas me las dejó a mí.

## 1. Nada cambia de golpe

- **Accesorios con entrada y salida:**
  - Los lentes bajan desde arriba y se desvanecen.
  - La laptop sube desde abajo mientras los brazos se apartan.
  - Las burbujas (pensamiento y "!") salen con un pequeño rebote, el *overshoot*.
  - La "z z", el sudor y los destellos aparecen y se van con fundido.
- **Caras:** los ojos (abiertos, cerrados, felices, cansados) y las bocas se funden de una a otra.
- **El brazo de "te necesita"** sube girando desde su posición normal, no aparece ya levantado.
- **Movimiento continuo:** la respiración, el aleteo y el tecleo usan fase acumulada y amplitud
  suavizada. Cambiar de ritmo ya no hace saltar al dragón.
- **Reacción al cambiar de estado:** un pequeño estirón y aplastón (*squash and stretch*) de
  ~0,35 s cada vez que cambia la pose o el color.
- El suavizado es exponencial con una constante de tiempo de ~0,18 s (los colores siguen con su
  mezcla OKLCh de 0,6 s).

## 2. Gestos ocasionales por estado

Un planificador elige gestos al azar, con pausas aleatorias entre ellos. Si el estado cambia,
el gesto en curso se corta suavemente.

| Estado | Cada | Gestos |
|---|---|---|
| Reposo | 6–14 s | mirar a los lados, menear la cola, estirar las alas, bostezo, **eructo de fueguito**, saltito |
| Reposo de más de 2 min (AFK) | 4–9 s | sobre todo bostezos y eructos de fuego; menos el resto |
| Durmiendo | 7–15 s | **burbuja de ronquido** que crece y explota, humito por la nariz, murmullo, cola que se mueve, estirón |
| Piensa | 5–10 s | mira hacia arriba, menea la cola |
| Escribe | 6–12 s | se acomoda los lentes, ráfaga de tecleo rápido |
| Te necesita | continuo | saluda con el brazo levantado; saltito cada 4–7 s |
| Listo | — | ya salta; sin gestos extra |

## 3. Recursos

- La escena se construye una sola vez como `DrawingGroup` retenidos. Cada fotograma solo cambia
  propiedades (opacidad y transformaciones), sin crear objetos.
- La vista va a 20 fps solo mientras hay una transición o un gesto. Si no, sigue a 10 fps en
  reposo y 4 fps durmiendo.
- Objetivo sin cambios: menos de 60 MB de memoria privada y menos de 1 % de CPU.

## 4. Pruebas

- Núcleo (con tests):
  - `Motion` (suavizado, curvas y rebote).
  - `GestureScheduler`: determinista con una semilla; respeta las pausas; corta al cambiar de
    estado; solo elige gestos válidos para cada estado; en AFK sube la proporción de
    bostezos y fuego.
- Visual: `--render-gestures` dibuja fotogramas de cada gesto y de las transiciones para
  revisarlos a ojo.
- Revisión independiente del código de animación con Antigravity (Gemini 4 Neon Extra).
