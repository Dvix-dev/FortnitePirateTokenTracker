# Fortnite Pirate Token Tracker

Herramienta local para Windows que analiza tus repeticiones de Fortnite (`.replay`) y rastrea la aparición del **Token pirata** y de los **cofres dorados** asociados al evento.

Lee únicamente los archivos de repetición que el propio juego genera en tu disco. No modifica Fortnite, no inyecta código, no lee memoria del proceso y no se conecta a Epic Games.

> **Estado del proyecto:** aplicación de consola + panel web local que analiza repeticiones reales, cuenta cofres con timestamps, atribuye cada cofre a un jugador, calcula su zona y exporta CSV/JSON. La identificación del premio (Token) sigue pendiente de un lector extendido; el detalle está en [Implementación actual](#19-implementación-actual).

---

## Índice

1. [Objetivo](#1-objetivo)
2. [Métricas que debe producir](#2-métricas-que-debe-producir)
3. [Contexto técnico del formato `.replay`](#3-contexto-técnico-del-formato-replay)
4. [Stack elegido y alternativas](#4-stack-elegido-y-alternativas)
5. [Requisitos del sistema](#5-requisitos-del-sistema)
6. [Estructura propuesta del repositorio](#6-estructura-propuesta-del-repositorio)
7. [Flujo de uso previsto](#7-flujo-de-uso-previsto)
8. [Interfaz de línea de comandos](#8-interfaz-de-línea-de-comandos)
9. [Detección del Token y del cofre dorado](#9-detección-del-token-y-del-cofre-dorado)
10. [Modelo de datos y archivos de salida](#10-modelo-de-datos-y-archivos-de-salida)
11. [Caché de repeticiones procesadas](#11-caché-de-repeticiones-procesadas)
12. [Análisis geográfico por zonas](#12-análisis-geográfico-por-zonas)
13. [Plan de ejecución por fases](#13-plan-de-ejecución-por-fases)
14. [Verificación y criterios de aceptación](#14-verificación-y-criterios-de-aceptación)
15. [Limitaciones y riesgos conocidos](#15-limitaciones-y-riesgos-conocidos)
16. [Privacidad de los datos](#16-privacidad-de-los-datos)
17. [Referencias verificadas](#17-referencias-verificadas)
18. [Glosario](#18-glosario)

---

## 1. Objetivo

Dado un directorio de repeticiones (por defecto `%LOCALAPPDATA%\FortniteGame\Saved\Demos`, o una ruta indicada por el usuario), la herramienta debe:

- Procesar **todas** las repeticiones de la carpeta en lote, sin intervención manual.
- Detectar cada apertura de cofre relevante y, dentro de esas, las que corresponden al **cofre dorado del Token pirata**.
- Distinguir lo que abre el jugador local de lo que abren otros jugadores cercanos.
- Agregar los resultados en un informe legible por consola y en archivos exportables.
- Evitar re-analizar repeticiones ya procesadas mediante una caché persistente.

El alcance es **solo análisis de datos históricos**. No hay overlay en vivo, ni automatización de juego, ni predicción de dónde saldrá el Token en la próxima partida.

## 2. Métricas que debe producir

| # | Métrica | Definición operativa |
|---|---------|----------------------|
| 1 | **Detección del Token / cofre dorado** | Cada vez que un cofre dentro del radio de relevancia configurado (por defecto ~1000 m) suelta el Token o activa el estado/anidación de cofre dorado especial, con timestamp, jugador y coordenadas. |
| 2 | **Tasa de aparición por partida** | Porcentaje de repeticiones analizadas en las que el Token apareció para *alguien*: el jugador local o cualquier otro jugador detectado en la repetición. |
| 3 | **Apariciones múltiples** | Casos en los que una misma partida (`MatchID` / archivo) contiene más de un cofre con Token, con sus timestamps, jugadores y distancia entre ellos. |
| 4 | **Análisis geográfico** | Coordenadas exactas `(X, Y, Z)` de cada cofre dorado detectado, agrupadas por proximidad y, si hay catálogo de mapa, asignadas a una zona con nombre. |
| 5 | **Personal vs. global** | Cofres totales abiertos por el jugador local, de esos cuántos con Token, y cuántos Tokens abrieron otros jugadores en sus partidas. |

## 3. Contexto técnico del formato `.replay`

Una repetición de Fortnite es un contenedor de Unreal Engine con:

- **Cabecera + checkpoints**: metadatos de la partida (versión, longitud, `MatchID`, jugadores, fecha).
- **Flujo de red grabado**: paquetes con *bunches*, que son actualizaciones parciales de propiedades de actores replicados.
- **Compresión Oodle**: los checkpoints y el flujo de datos están comprimidos con Oodle (Kraken/Mermaid/Selkie/Leviathan/LZNA/BitKnit). Sin descompresor, el archivo es ilegible.
- **Cifrado condicional**: la clave de cifrado del flujo de datos se escribe al **finalizar la partida**. Una repetición de una partida en curso no se puede abrir.

Propiedades y eventos que importan a este proyecto:

| Elemento | Rol en el análisis |
|----------|--------------------|
| `BuildingContainer` / `BaseContainer` | Actor del cofre. Expone estado de búsqueda y tipo de contenedor. |
| `bAlreadySearched` | Indica que el cofre ya fue abierto/buscado. Disparador principal de la apertura. |
| `SearchingPawn` / animación de búsqueda | Identifica **quién** abre el cofre (el pawn, no solo el índice de actor). |
| `FortPickup` | Ítems soltados por el cofre; permite ver qué objeto salió y cuándo. |
| Prize id / treasure handler | El cofre dorado del evento de tesoro aparece portando un identificador de premio; los *grants* de habilidad (`GA_TreasureHandler_*`) en el `FastArray` `ActivatableAbilities` del `PlayerState` confirman que el jugador recibió el premio. |
| `NetFieldExports` | Tabla que traduce nombres de clase/propiedad a tipos fuertes; base de toda detección fiable. |
| `ReplayMovement` / `FRepMovement` | Posición replicada de actores y pawns; fuente de las coordenadas `(X, Y, Z)`. |

**Unidades:** Unreal trabaja en centímetros. `1 m = 100 uu`, por lo que un radio de 1000 m equivale a **100 000 uu**. El radio se expone en metros en la CLI y se convierte internamente.

## 4. Stack elegido y alternativas

**Decisión: C# sobre .NET (actualmente .NET 10) con el paquete NuGet `FortniteReplayReader`.**

Motivos:

- Es el parser de repeticiones de Fortnite mantenido y más usado de la comunidad; expone `NetFieldExports`, eventos de contenedores y *pickups* ya deserializados.
- Depende de `Unreal.Core` (lectura de paquetes/bunches) y `OozSharp` (descompresión Oodle), de modo que no hay que reimplementar el formato binario.
- Todos los contadores de cofres recientes de la comunidad están construidos sobre este mismo paquete, lo que da ejemplos verificables de cómo se detectan cofres, tesoros y jugadores.
- Rendimiento: procesar decenas de repeticiones en lote es viable en un solo hilo, y la versión 3.1.0 redujo asignaciones y tiempos de parseo de forma notable.

Versiones confirmadas a fecha de esta documentación:

| Componente | Versión / requisito |
|------------|---------------------|
| `FortniteReplayReader` | 3.1.0 (publicada 2026-09-13) |
| Framework | `.NET 10.0` — la 3.x ya no soporta .NET 6/8 |
| `Unreal.Core` | 3.0.x |
| `OozSharp` | 3.0.x |
| Licencia del parser | MIT |

**Alternativas evaluadas:**

- **Node/TypeScript**: no existe un decodificador Oodle + parser de `NetFieldExports` igualmente maduro; habría que portar lógica de red de Unreal, con alto riesgo de desincronización en cada parche.
- **Python**: cómodo para el análisis y los gráficos, pero el parseo binario sería mucho más lento y sin cobertura de las clases nuevas. Se puede usar Python más adelante solo como consumidor de `resumen.json`/CSV para visualizaciones.

Se mantiene la puerta abierta a un **frontend web local ligero** (una página estática que lee los JSON/CSV generados) sin servidor ni base de datos.

## 5. Requisitos del sistema

- Windows 10/11 (probado en el equipo del usuario).
- **.NET SDK 10** (`dotnet --list-sdks` debe mostrar una versión 10.x). Si solo hay 8/9, hay dos caminos: instalar el SDK 10 o fijar el parser en la rama 2.4.x, que soporta .NET 9 pero no cubre los parches recientes.
- Fortnite con **grabación de repeticiones activada** (Ajustes → Juego → Repeticiones → Grabar repeticiones: Sí).
- La carpeta `%LOCALAPPDATA%\FortniteGame\Saved\Demos` con al menos un `.replay` completo.
- Opcional: `Oodle.dll` de la instalación local de Fortnite si la distribución del descompresor lo requiere.

## 6. Estructura propuesta del repositorio

```text
FortnitePirateTokenTracker/
├─ README.md                          # este documento
├─ .gitignore                         # excluye replays, caché y salidas
├─ src/
│  ├─ FortnitePirateTokenTracker.Cli/         # entrada, argumentos, informe de consola
│  ├─ FortnitePirateTokenTracker.Core/        # dominio: modelos, detección, agregación, zonas
│  └─ FortnitePirateTokenTracker.Parsing/     # adaptador sobre FortniteReplayReader
├─ tools/
│  └─ ReplayProbe/                    # volcado de depuración de un .replay (Paso 2)
├─ data/
│  └─ map_pois/
│     └─ <version_mapa>.json          # catálogo de zonas con centro y radio
├─ tests/
│  └─ FortnitePirateTokenTracker.Tests/
└─ (generados, fuera de control de versiones)
   ├─ resultados_token.csv
   ├─ resumen.json
   └─ processed_replays.json
```

Separación de responsabilidades: `Parsing` no sabe qué es un Token (solo traduce repetición → eventos neutros); `Core` aplica reglas y agrega; `Cli` presenta y exporta. Esto permite cambiar de parser sin tocar la lógica del evento.

## 7. Flujo de uso previsto

```powershell
# 1. Compilar
dotnet build -c Release

# 2. Analizar la carpeta por defecto de Fortnite
dotnet run --project src/FortnitePirateTokenTracker.Cli -- --all

# 3. Analizar una carpeta concreta, con radio de 1200 m y salida a otra ruta
dotnet run --project src/FortnitePirateTokenTracker.Cli -- `
  --path "D:\Replays" --radius 1200 --out ".\salida"
```

Al terminar, la herramienta imprime un informe y escribe los tres archivos de la sección 10. En ejecuciones posteriores solo se procesan las repeticiones nuevas o modificadas.

## 8. Interfaz de línea de comandos

| Flag | Por defecto | Descripción |
|------|-------------|-------------|
| `--path <dir>` | `%LOCALAPPDATA%\FortniteGame\Saved\Demos` | Carpeta de repeticiones (o un único `.replay`). |
| `--all` | activo | Procesa todos los archivos de la carpeta (recursivo opcional con `--recursive`). |
| `--since <fecha>` | — | Ignora repeticiones anteriores a la fecha (`YYYY-MM-DD`). |
| `--radius <metros>` | `1000` | Radio de relevancia para considerar un cofre cercano al jugador local. |
| `--min-confidence <nivel>` | `B` | Confianza mínima de detección a reportar (`A`, `B`, `C`). Ver sección 9. |
| `--no-cache` | desactivado | Fuerza el re-análisis completo. |
| `--cache <archivo>` | `processed_replays.json` | Ruta de la caché. |
| `--out <dir>` | directorio actual | Carpeta de salida de CSV/JSON. |
| `--csv-sep <carácter>` | `,` | Separador del CSV; usar `;` para Excel en español. |
| `--anonymize` | desactivado | Sustituye nombres de jugadores por identificadores estables. |
| `--verbose` | desactivado | Log por repetición y por cofre detectado. |
| `--dry-run` | desactivado | Parsea y detecta, pero no escribe archivos ni actualiza la caché. |

Códigos de salida: `0` correcto, `1` error de argumentos, `2` ninguna repetición encontrada, `3` una o más repeticiones no se pudieron deserializar (con `--strict` el fallo es fatal).

## 9. Detección del Token y del cofre dorado

La detección es **en cascada**, con un nivel de confianza asignado a cada hallazgo. Los niveles C nunca cuentan para la tasa oficial; solo se listan como candidatos a revisión.

| Nivel | Regla | Señal usada |
|-------|-------|-------------|
| **A** | El cofre abierto (o el `FortPickup` que genera) porta el identificador de premio/ítem del Token pirata. | prize id / `ItemDefinition` / `TemplateId` leído del contenedor o del pickup. |
| **B** | El cofre tiene estado de cofre dorado del evento **y** el `PlayerState` del buscador recibe el *grant* de tesoro en la misma ventana temporal (±3 s). | `bAlreadySearched`, tipo de contenedor, `GA_TreasureHandler_*` en `ActivatableAbilities`. |
| **C** | Heurística de contexto: contenedor abierto dentro del radio configurado, con animación de búsqueda, sin coincidencia de ítem reconocible. | `SearchAnimationCount` / `SearchingPawn`, posición, orden temporal. |

La identificación de **quién** abrió el cofre usa `SearchingPawn` cuando está disponible y, si no, el par (`bAlreadySearched` + proximidad del pawn al cofre en ese frame). El jugador local se determina por la repetición y por comparación con el `PlayerState` marcado como local en la cabecera.

Ambigüedades que el **Paso 2 (Discovery)** debe resolver contra una repetición real antes de fijar las reglas:

1. Nombre exacto de la clase de cofre dorado y del identificador del Token en la versión actual del juego.
2. Si el premio se anuncia al abrir el cofre o al recoger el ítem soltado.
3. Formato del prize id y estabilidad entre parches.
4. Cómo se comporta `bAlreadySearched` cuando un cofre se recarga y se vuelve a abrir en la misma partida (debe contar como apertura nueva).

El estado real de estas incógnitas se documentará en cada actualización del parser; un cambio de parche que rompa un nombre de clase debe degradar la detección, no lanzar excepciones.

## 10. Modelo de datos y archivos de salida

### 10.1 `resultados_token.csv`

Una fila por **cofre relevante detectado** (no solo los del Token), para poder calcular denominadores.

| Columna | Tipo | Descripción |
|---------|------|-------------|
| `match_id` | string | Identificador de la partida según la repetición. |
| `replay_file` | string | Nombre del archivo analizado. |
| `timestamp_ms` | int | Instante del evento respecto al inicio de la partida. |
| `tiempo_juego` | string | `mm:ss` legible del mismo instante. |
| `jugador` | string | Nombre del jugador que abrió el cofre. |
| `es_jugador_local` | bool | `true` si es el usuario que analiza. |
| `es_token` | bool | `true` si la detección concluye Token pirata. |
| `nivel_confianza` | `A`/`B`/`C` | Nivel de la regla aplicada. |
| `contenedor` | string | Clase/tipo de cofre. |
| `item_id` | string | Prize id / `TemplateId` cuando se conoce. |
| `x`, `y`, `z` | float | Coordenadas del cofre en unidades Unreal. |
| `distancia_jugador_m` | float\|vacío | Distancia al jugador local, si aplica. |
| `zona` | string | Zona asignada del catálogo, o `desconocida`. |
| `apertura_en_partida` | int | Índice de cofre del jugador en esa partida. |
| `token_en_partida` | int | Índice de Token en esa partida (1, 2, …). |

Codificación UTF-8 con BOM opcional (`--csv-bom`) para que Excel respete acentos.

### 10.2 `resumen.json`

```jsonc
{
  "schema_version": 1,
  "generado_en": "2026-11-15T18:32:10Z",
  "version_herramienta": "0.1.0",
  "version_parser": "FortniteReplayReader 3.1.0",
  "config": { "radio_m": 1000, "confianza_minima": "B" },
  "totales": {
    "repeticiones_encontradas": 0,
    "repeticiones_analizadas": 0,
    "repeticiones_omitidas_por_cache": 0,
    "repeticiones_con_error": 0,
    "cofres_detectados": 0,
    "cofres_abiertos_por_mi": 0,
    "cofres_token_mios": 0,
    "cofres_token_otros": 0,
    "partidas_con_token": 0,
    "tasa_partidas_con_token": 0.0,
    "partidas_con_multiples_tokens": 0
  },
  "zonas": [
    { "nombre": "Zona A", "x": 0.0, "y": 0.0, "repeticiones": 0, "tokens": 0 }
  ],
  "partidas": [
    {
      "match_id": "...",
      "replay_file": "...",
      "fecha": "...",
      "modo": "Battle Royale",
      "cofres_totales": 0,
      "cofres_mios": 0,
      "tokens_detectados": 0,
      "tokens_mios": 0,
      "jugadores_con_token": ["..."],
      "errores_deserializacion": 0
    }
  ]
}
```

`schema_version` permite que consumidores externos detecten cambios incompatibles.

### 10.3 Informe de consola

Bloque fijo al finalizar: repeticiones analizadas/omitidas/erróneas, cofres totales, Tokens propios y ajenos, tasa de partidas con Token, mejor y peor racha, top de zonas y, destacado, la lista de partidas con más de un Token con sus timestamps.

## 11. Caché de repeticiones procesadas

Archivo `processed_replays.json`, validado contra un `schema_version`.

Clave de entrada: `sha256(contenido) + tamaño + mtime` más `parser_version` y `detection_version`. Si cualquiera cambia, la entrada se invalida y la repetición se reprocesa: es lo que evita mostrar datos viejos tras un parche o una mejora de reglas.

Cada entrada guarda el resultado ya normalizado (eventos detectados y agregados de la partida), no el volcado crudo del parser, para que la caché sea pequeña. Las repeticiones con error se registran con su mensaje y se reintentan solo si cambia el archivo o la versión del parser (`--retry-failed`).

## 12. Análisis geográfico por zonas

1. Se recogen las coordenadas `(X, Y, Z)` de cada cofre dorado.
2. Se agrupan por proximidad con un radio de agrupación configurable (por defecto 50 m ≈ 5000 uu), de forma determinista: orden por `(X, Y)` antes de agrupar para que dos ejecuciones den el mismo resultado.
3. Si existe `data/map_pois/<version_mapa>.json`, cada grupo se asigna al POI cuyo centro esté más cerca, respetando un radio máximo; si no hay coincidencia, la zona queda como `desconocida` y se reporta su centroide.
4. El resumen incluye el número de Tokens por zona y el porcentaje sobre el total de Tokens detectados.

El catálogo de zonas es un archivo de datos versionado: cuando cambia el mapa, se añade un archivo nuevo y las repeticiones antiguas siguen resolviéndose con el catálogo de su fecha.

## 13. Plan de ejecución por fases

### Fase 1 — Investigación y elección del stack
- Revisar implementaciones 2025–2026 de parsers de repeticiones y contadores de cofres.
- Detectar qué SDK hay instalado (`.NET`, `Node.js`, `Python`) antes de comprometerse con un framework.
- Fijar la versión del parser y dejar documentado por qué.
- **Estado:** parcialmente cubierta por este documento; la comprobación del entorno se hará al iniciar la implementación.

### Fase 2 — Inspección de una repetición real (Discovery)
- Localizar el `.replay` más reciente en la carpeta de Fortnite (o pedir una ruta si no hay ninguno).
- Construir `tools/ReplayProbe` para volcar `NetFieldExports`, eventos de `BuildingContainer`, `FortPickup` y supply drops, con sus propiedades crudas.
- Confirmar empíricamente las cuatro ambigüedades de la sección 9 y anotar los nombres exactos de clases, propiedades y prize ids.
- **Entregable:** informe de discovery (`docs/discovery.md`) con los hallazgos y el ejemplo mínimo que prueba cada regla.

### Fase 3 — Construcción de la herramienta
- Modelo de dominio y detección en cascada con niveles de confianza.
- Procesamiento en lote con caché persistente y reanudación tras interrupción.
- Agregación de métricas, agrupación geográfica y exportación a CSV y JSON.
- Informe de consola y manejo de errores por archivo sin abortar el lote.

### Fase 4 — Verificación
- Ejecutar contra las repeticiones actuales del usuario y adjuntar los logs.
- Contraste manual: número de partidas, cofres abiertos y coordenadas coherentes; cero excepciones de deserialización.
- Casos límite: carpeta vacía, archivo corrupto, repetición de partida en curso (sin clave), partida sin cofres, partida con dos Tokens.

## 14. Verificación y criterios de aceptación

Se considera terminada la herramienta cuando:

- [ ] `dotnet build -c Release` compila sin warnings tratados como error.
- [ ] El lote procesa la carpeta completa y el resumen coincide con un recuento manual de al menos tres partidas.
- [ ] Una segunda ejecución no vuelve a analizar archivos ya cacheados (verificable en los logs).
- [ ] Ninguna repetición aborta el proceso: los errores se cuentan y se listan.
- [ ] `resultados_token.csv` abre correctamente en Excel y `resumen.json` valida contra `schema_version`.
- [ ] Las coordenadas de un cofre conocido caen en la zona esperada del mapa.
- [ ] Los casos de aparición múltiple muestran dos o más timestamps distintos y jugadores distintos.

Los tests unitarios cubren la agrupación por proximidad, la asignación de zonas, la invalidación de caché y las reglas de detección con eventos sintéticos. Las repeticiones reales **no** se versionan.

## 15. Limitaciones y riesgos conocidos

- **Dependencia de nombres de clase de Unreal.** Cada parche puede renombrar o reestructurar actores. Un nombre desconocido debe producir un aviso y degradar la detección, nunca un crash. Los contadores de la comunidad ya necesitan alias y lectura cruda de campos sin nombre exportado para seguir funcionando.
- **Repeticiones incompletas.** Si la partida no terminó de escribirse, no hay clave de cifrado y no se puede analizar.
- **Retención de Fortnite.** El juego borra repeticiones antiguas; el historial propio debe conservarse copiando los archivos o manteniendo la caché.
- **Radio de red.** "~1000 m" es un parámetro de relevancia, no una garantía: los datos replicados dependen de lo que el servidor envió a tu cliente. Un Token lejano puede no existir en tu repetición.
- **Modo de juego.** Solo tiene sentido en Battle Royale; otros modos se marcan y se excluyen de las tasas.
- **Duplicados.** Una recarga de cofre cuenta como apertura nueva; un mismo Token recogido y soltado no debe contarse dos veces (deduplicación por prize id + ventana temporal).
- **Rendimiento.** Miles de repeticiones requieren la caché; sin ella el análisis completo puede tardar minutos.

## 16. Privacidad de los datos

Las repeticiones contienen nombres de jugadores, identificadores de cuenta y posiciones. La herramienta:

- Se ejecuta **100 % en local**; no envía datos a ningún servicio.
- Escribe las salidas en la carpeta que el usuario indique.
- Ofrece `--anonymize` para compartir resultados sin exponer nombres.
- Excluye del control de versiones `*.replay`, la caché y los archivos de salida mediante `.gitignore`.

## 17. Referencias verificadas

- `Shiqan/FortniteReplayDecompressor` — parser C# de repeticiones de Fortnite, licencia MIT: <https://github.com/Shiqan/FortniteReplayDecompressor>
- `FortniteReplayReader` en NuGet (3.1.0, .NET 10): <https://www.nuget.org/packages/FortniteReplayReader>
- Documentación del parser (formato, checkpoints, Oodle): <https://fortnitereplaydecompressor.readthedocs.io/en/latest/overview>
- `xdoofy92/ChestCounter` — contador de cofres en C# con parches documentados sobre el parser y detección de tesoros: <https://github.com/xdoofy92/ChestCounter>
- `Aelphyr/Chest-Counter` — contador de cofres a partir de repeticiones: <https://github.com/Aelphyr/Chest-Counter>
- `LED-N/Fortnite-ChestCounter` — variante con overlay y notas de parches: <https://github.com/LED-N/Fortnite-ChestCounter>

## 18. Glosario

- **Repetición (`.replay`)**: grabación del flujo de red de una partida guardada por Fortnite.
- **Bunch**: actualización parcial de las propiedades de un actor dentro de un paquete de red.
- **NetFieldExport**: tabla que describe cómo deserializar propiedades de una clase replicada.
- **Oodle**: códec de compresión propietario usado por Unreal para comprimir checkpoints y datos.
- **Token pirata**: ítem especial del evento; se obtiene de un cofre dorado con premio asociado.
- **Cofre dorado**: contenedor especial del evento de tesoro que entrega el premio.
- **uu**: unidad de Unreal Engine; 100 uu = 1 metro.
- **MatchID**: identificador de la partida, estable entre la repetición y su archivo.

---

## 19. Implementación actual

Arquitectura entregada (atribución por posición):

```text
src/
├─ FortnitePirateTokenTracker.Core/     # configuración, catálogo, modelos, agregación, CSV
└─ FortnitePirateTokenTracker.App/      # lector, analizador con caché, servidor local, panel web
   ├─ Parsing/CapturingReader.cs        # subclase del lector oficial: captura grupos replicados
   ├─ Parsing/ReplayAnalyzer.cs         # lote + caché por tamaño/mtime
   ├─ Reporting/ScanRunner.cs           # informe + CSV
   ├─ Web/PanelServer.cs                # servidor HTTP mínimo sobre TcpListener
   └─ wwwroot/                          # panel (HTML/CSS/JS sin dependencias)
```

No se parchea ni se redistribuye el parser: `CapturingReader` hereda de `FortniteReplayReader.ReplayReader` y usa el gancho `OnExportRead` para observar cada grupo, delegando el parseo normal en la implementación base.

| Área | Estado |
|------|--------|
| Lectura de `.replay` (Oodle + cifrado) | Funciona con `FortniteReplayReader` 3.1.0 |
| Contenedores abiertos y timestamps | Funciona (`Tiered_Chest_Athena`, `AmmoBox`, …) |
| Metadatos (jugadores, jugador local, duración) | Funciona (89 jugadores en la partida de prueba) |
| Caché, CSV, `report.json` | Funciona |
| Panel web, configuración y guía | Funciona |
| Cosméticos por jugador (outfit, pico, mochila, coche) | Se leen del **pawn** (la versión actual ya no los replica en el `PlayerState`) |
| Atribución cofre → jugador | Funciona: `SearchingPawn` → última posición del pawn → trayectoria del jugador con el reloj alineado |
| Coordenadas del cofre | Funciona para los grupos tipados por este proyecto y para los que replican `Location` en el actor |
| Identificación del premio / Token | Pendiente: la heurística por pickup está lista, sin ids de premio confirmados |

## 20. Hallazgos del volcado real (2026-11-15)

Sobre la atribución de jugadores y zonas:

- **`PlayerPawn.PlayerState` llega vacío** en las repeticiones actuales: el enlace pawn → `PlayerState` no existe, así que el nombre se obtiene cruzando trayectorias (`PlayerData.Locations`), no por identificador.
- **Relojes distintos:** el frame del replay empieza en 0 y `ReplicatedWorldTimeSeconds` arranca en el reloj del servidor (~1100 s). El desfase se estima por partida casando la geometría de los cofres ya posicionados; sin ese ajuste la atribución por tiempo falla.
- **Cofre escalonado genérico:** la ruta `Tiered_Chest_Athena` está registrada por la librería con una clase sin `SearchingPawn`; sustituir el grupo en el registro no cambia la instanciación porque el mapeo compilado del parser conserva el tipo original. Esos cofres quedan sin jugador ni zona salvo que el actor replique `Location`.
- **Caché de cosméticos:** `PlayerData.Cosmetics` llega vacío; los cosméticos se leen de las propiedades del pawn (`Character`, `Backpack`, `Pickaxe`, `Glider`, `MusicPack`, `PetSkin`).

Sondeo sobre repeticiones reales de la carpeta por defecto:

- **Grupos de contenedor observados:** `Tiered_Chest_Athena`, `AlwaysSpawn_NormalChest`, `Tiered_Chest_5_Child`, `BGA_LootCannon_StandardChest_Pirate`, `BGA_ExtractionPointGameplay_Chest`, `BP_Container_WoodenCrates_*`. La librería solo tipa el primero; el resto aparece como grupo no leído.
- **Pickups:** 1 939 en una partida de 20 MB (63 ids únicos), dominados por munición y armas (`WID_*`). Ningún id contenía `Token`/`Pirate`/`Gold` en las muestras.
- **Volumen de aperturas:** 125 contenedores en una partida de 20 MB; el análisis completo de la carpeta da **10 196 aperturas en 99 repeticiones legibles** (~103 por partida).
- **Repetición ilegible:** una de cada cien (`UnsavedReplay-2026.11.07-21.16.14`) aborta con `Replay is marked encrypted but not yet marked as completed!`: la partida no se terminó de escribir y el archivo no tiene clave. Se registra como error y el lote continúa.
- **Modo Debug:** los grupos de cofre están marcados `minimalParseMode: Debug`; en modo `Minimal` no se leen. Se usa `Debug` con tolerancia a eventos desconocidos para no abortar el archivo.
- **Tiempo del frame:** la propiedad `CurrentTimeSeconds` no es accesible en 3.1.0 (de ahí que los proyectos de la comunidad la parcheen). Se resuelve leyendo la cabecera del demo frame y restaurando la posición con `Seek` antes de delegar.

## 21. Cómo ejecutarlo

Requiere el SDK de .NET 10. Si no está instalado en el sistema, se puede instalar de forma local y aislada:

```powershell
# SDK local en .dotnet/ (ignorado por git)
./.dotnet/dotnet.exe build -c Release

# Panel web (abre el navegador en http://localhost:5178)
./src/FortnitePirateTokenTracker.App/bin/Release/net10.0/TokenTracker.exe

# Análisis en lote desde consola
./src/FortnitePirateTokenTracker.App/bin/Release/net10.0/TokenTracker.exe analyze --limit 3

# Volcado de depuración de un archivo
./src/FortnitePirateTokenTracker.App/bin/Release/net10.0/TokenTracker.exe probe <archivo.replay>
```

Salidas en `data/`: `report.json`, `resultados_token.csv`, `config.json` y `cache/parsed-replays.json`.

## 22. Estado por sprint

| Sprint | Contenido | Estado |
|--------|-----------|--------|
| 1 | Solución, núcleo de datos, panel, configuración y guía | Completado |
| 2 | Lector con gancho, timestamps, lote, caché y exportación | Completado |
| 3 | Atribución jugador ↔ cofre, posición del cofre, premio/Token y cosméticos | Pendiente |

*Documento de especificación. Cualquier cambio de alcance debe reflejarse aquí primero.*
