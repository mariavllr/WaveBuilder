# Benchmark de coste computacional (runtime)

Pipeline nuevo para medir el coste de resolución de los tres solvers del artículo con una definición de tiempo única y verificable. Solo cubre runtime. Las métricas de calidad (JS, entropía, diversidad) siguen en `WFCQualityMetrics.cs` y no se han modificado.

Ficheros (en `Assets/Scripts/TESTS/RuntimeBenchmark/`):

| Fichero | Contenido |
|---|---|
| `Core/RBTypes.cs` | Problema compilado (`CompiledTileset`), niveles (`LevelSpec`), instancias (`ProblemInstance`, `InstanceBuilder`) y semillas |
| `Core/RBSolverThisWork.cs` | "This work": port de `MyWFC.cs` con las correcciones descritas abajo |
| `Core/RBSolverGumin.cs` | Gumin Simple Tiled Model 3D, port fiel de `Model.cs` original |
| `Core/RBSolverDeBroglie.cs` | Adaptador de DeBroglie 2.1.0 (sin modificar la librería), modos `matched` y `native` |
| `Core/RBEngine.cs` | Motor: único cronómetro, protocolo de runs, CSV y estadísticos |
| `Core/RBPlan.cs` | Matriz de configuraciones A y B, manifest |
| `RuntimeBenchmarkRunner.cs` | MonoBehaviour que compila los tilesets con el `TilePreprocessor` del framework y lanza el motor |
| `Editor/RuntimeBenchmarkMenu.cs` | Menú `WFC ▸ Runtime Benchmark` |
| `analyze_runtime.py` | Tablas A y B (Markdown) a partir de `summary.csv` |

El núcleo (`Core/`) no depende de UnityEngine. Se ha compilado y probado también fuera de Unity (CoreCLR y Mono 6.8) con los tres tilesets reconstruidos a partir de los prefabs.

## 1. Problemas encontrados en la medición anterior

1. **Retries contabilizados de forma asimétrica (versión del artículo, commit `348bc4e`).** El `Stopwatch` de `CalculateExecutionTime` arrancaba en `onStartGeneration` y no se reiniciaba tras una contradicción. En REFACTOR, cada reintento ejecutaba `Regenerate()`: destruía e instanciaba de nuevo todas las celdas como GameObjects, aplicaba las restricciones con AC-3 sobre objetos `Cell`, reinicializaba AC-4 y escribía un `Debug.LogError`. Todo eso entraba en el tiempo de los reintentos, pero no en el del primer intento. DeBroglie tenía el mismo fallo con un coste mucho menor, y Gumin solo reinicializaba arrays. Esto explica la cola de la media señalada por el revisor 2 (M1).
2. **Restricciones fuera del cronómetro.** La versión sin commitear excluía la inicialización y la propagación inicial de las restricciones. Eso impedía medir el coste directo de las restricciones en la ablación.
3. **Port de Gumin no fiel** (comparado con `Model.cs`): colapsaba celdas ya decididas con un barrido O(N) adicional por celda, no baneaba en `Clear()` las tiles sin vecinos posibles y reservaba memoria en cada intento.
4. **Dominios y adyacencias distintos.** DeBroglie incluía LIMIT en el modelo y los otros dos no. El `TilePreprocessor` de Gumin en la escena tenía `excludedNeighborConstraint = 0`, así que Gumin no tenía negative rules y los otros dos sí. Los topes de reintentos también eran distintos (100, 10 y 10000), y si se agotaban el arnés se quedaba colgado.
5. **Error en "This work" (`MyWFC`).** El baneo inicial de tiles sin vecino posible comprobaba la existencia del vecino en la dirección `d`, pero leía el soporte de la dirección opuesta. Con un vecino en el borde del volumen podía banear tiles válidas o dejar tiles imposibles.
6. **Pares de celdas preasignadas no comprobados.** Ni `MyWFC` ni la representación con máscara comprobaban si dos celdas fijas adyacentes son compatibles entre sí (por ejemplo, una tile fija junto al anillo LIMIT). Ahora se comprueba en `InitAttempt` (dentro del cronómetro).
7. **Fixed tiles inactivas.** Ningún prefab ha tenido nunca `fixedTile > 0` en todo el historial de git, así que el mecanismo no tuvo efecto en los experimentos del artículo.
8. **Semillas no registradas.** `new System.Random()` sin semilla hacía las ejecuciones irreproducibles. En Mono 6.8 no se repiten secuencias entre instancias consecutivas, así que no hay sesgo demostrado, pero tampoco había trazabilidad.
9. **Primera decisión de DeBroglie sobre un estado no arco-consistente.** Sin ninguna restricción activa, `DeBroglie.Clear()` deja en cola los baneos iniciales (tiles sin vecino posible) y el primer `Step()` observa una celda antes de propagarlos. En una prueba fuera de Unity, 59 de las contradicciones de DeBroglie en A ocurrieron en la decisión nº 1 (por ejemplo, desert: 37–38/50 de éxito en el primer intento frente a 50/50 de ThisWork y Gumin). ThisWork y Gumin propagan antes de decidir. Se neutraliza con una restricción vacía de la API pública (`InitialPropagationConstraint`), que hace que `Clear()` propague; no modifica la lógica de resolución. La variante sin esta corrección se puede añadir con `includeDeBroglieLazyInit`.
10. **Stopping rule inversa.** Se generaban 50 éxitos, se descartaba el primero y el éxito se calculaba como 49/(49+contradicciones).

## 2. Diseño experimental

**Benchmark A: `A_UNRESTRICTED`** (ThisWork, Gumin, DeBroglie). Los tres solvers reciben el mismo `CompiledTileset`, así que dominio, variantes, pesos y adyacencias son idénticos por construcción. No hay negative rules, celdas preasignadas, capas, límite ni tiles fijas. Los bordes del volumen no imponen restricción en ningún solver (topología no periódica). Matriz: 3 solvers × 3 tilesets × 3 tamaños = 27 configuraciones.

**Benchmark B: ablación de restricciones** (ThisWork, DeBroglie):

| ID | Capas (SOLID y=0, EMPTY y=Y−1) | Límite (LIMIT, perímetro de y=1) | Tiles fijas | Negative rules |
|---|---|---|---|---|
| `B0_BASELINE` | | | | |
| `B1_LAYERS` | ✓ | | | |
| `B2_LAYERS_BOUNDARY` | ✓ | ✓ | | |
| `B3_LAYERS_BOUNDARY_FIXED` | ✓ | ✓ | ✓ | |
| `B4_LAYERS_BOUNDARY_FIXED_NEGATIVE` | ✓ | ✓ | ✓ | ✓ |

Matriz: 2 solvers × 3 tilesets × 3 tamaños × 5 niveles = 90 combinaciones (45 grupos de configuración). B0 es exactamente la configuración de A y usa las mismas semillas, así que reproduce las mismas soluciones (sirve de control de deriva temporal).

**Correspondencia con el artículo.**
- Probabilistic weighting ↔ `Tile.probability`. Activo en todos los niveles; peso = `probability > 0 ? probability : 1`, igual que antes.
- Layer constraints ↔ `floorCeilingConstraint`.
- Boundary ↔ `borderConstraint` / `DefineMapLimits`.
- Pre-assignment ↔ `fixedTilesConstraint` / `Tile.fixedTile`.
- Negative rules ↔ `excludedNeighborConstraint` / `excludedNeighbours*`. Se aplican en el preprocesado (adyacencias), no en el solver; por eso B4 usa el tileset compilado con negative rules.

**Identificador de configuración:** `config_id = <LEVEL_ID>/<TILESET>/<X>x<Z>x<Y>`, por ejemplo `A_UNRESTRICTED/NATURE/10x10x5` o `B3_LAYERS_BOUNDARY_FIXED/FARM/30x30x5`.

## 3. Definición del tiempo

El cronómetro está **solo** en `RuntimeBenchmarkEngine` y es idéntico para todos los solvers (`Stopwatch.GetTimestamp`).

| Fase | ¿Cronometrada? |
|---|---|
| Preprocesado del tileset (variantes, adyacencias), construcción del modelo/propagador, reserva de memoria (`Prepare`) | No |
| Generación de la instancia: posiciones de tiles fijas (`InstanceBuilder`) | No |
| Representación de la instancia: tabla de vecinos libres / máscara y `TilePropagator` de DeBroglie (`SetupAttempt`) | No |
| `GC.Collect()` antes de cada fase cronometrada | No |
| **`t_init`**: reset del estado, aplicación de capas, límite y tiles fijas, comprobación de pares fijos, propagación inicial | **Sí** |
| Diagnóstico de celdas indecisas tras init | No |
| **`t_search`**: bucle observación–colapso–propagación hasta solución o contradicción | **Sí** |
| Lectura, validación e instanciación (no hay instanciación visual) | No |

**`t_solve` de un run = Σ (t_init + t_search) de todos sus intentos**, incluidos los fallidos. Esta es la métrica principal ("total solving time"). Las dos componentes se guardan por separado.

El artículo actual dice que la aplicación de los mecanismos de control queda fuera de la ventana. Con la nueva definición queda dentro, y esto debe actualizarse en IV-B (lo pide además el revisor 2, M1b).

El reparto entre `t_init` y `t_search` **no es comparable entre solvers**: DeBroglie no propaga los baneos de `Clear()` hasta el primer `Step()`. Para comparar, usa `t_solve`.

## 4. Retries, contradicciones, success rate y stopping rule

- Cada configuración y solver ejecuta **50 runs medidos** (número fijo). Cada run reintenta con reinicio completo hasta obtener solución, con un tope común de 1000 intentos.
- Cada intento fallido es **una contradicción**, clasificada por fase: `contradictions_init` (la instancia ya es inconsistente tras la propagación inicial) y `contradictions_search`. Su tiempo queda registrado en `attempts.csv` y se suma en `t_failed_attempts_ms`.
- **Éxito principal**: `success_rate_first_attempt = runs resueltos en el primer intento / 50`. El primer intento de cada run es un ensayo independiente, así que es un muestreo de n fijo y no depende de cuántos reintentos necesite cada solver. Se incluye el intervalo de Wilson al 95 %.
- También se informa `success_rate_pooled_attempts = runs resueltos / intentos totales` (el estimador antiguo, de muestreo inverso), solo como complemento.
- Todos los solvers tienen exactamente 50 runs, y el coste de los reintentos se incluye en `t_solve` según la definición del artículo.

## 5. Celdas libres (free/collapsible)

Una celda libre es una celda que la instancia **no** preasigna: no está en las capas, ni en el anillo de límite, ni es una tile fija. Es una variable del CSP para ThisWork y DeBroglie-matched.

`effective_free_cells = N − layer_cells − boundary_cells − fixed_tile_cells`, con N = X·Y·Z:
- A y B0: N.
- B1: N − 2·X·Z.
- B2: N − 2·X·Z − (2·X + 2·Z − 4).
- B3/B4: lo anterior menos el número de tiles fijas colocadas.

Columnas relacionadas:
- `solver_csp_cells`: celdas que son variables en la representación del solver. Gumin y DeBroglie-native = N.
- `undecided_after_init`: celdas libres con más de una opción tras la propagación inicial, es decir, las que realmente requieren decisiones.
- `decisions`: observaciones realizadas.
- `time_per_free_cell_us = t_solve / effective_free_cells` (en µs).

## 6. Tiles fijas y DeBroglie

En DeBroglie 2.1.0, `FixedTileConstraint.Init()` llama a `propagator.Select(...)` dentro de `Clear()`: la celda **sigue en el CSP** y la restricción se impone igual que con Select/Ban. No es un mecanismo de preasignación distinto. El mecanismo de DeBroglie que saca celdas del CSP es la máscara de topología (`GridTopology.WithMask`).

- **`matched` (por defecto):** las celdas preasignadas (capas, límite, tiles fijas) se excluyen con la máscara. Su efecto sobre los vecinos libres se aplica con `Ban` (tiles no admitidas por la tile fija vecina). Es la misma formulación del CSP que ThisWork. LIMIT no entra en el modelo.
- **`native` (opcional, `includeDeBroglieNative`):** tiles fijas con `FixedTileConstraint` (con `Point` explícito), capas y anillo con `Select`, y LIMIT en el modelo baneado fuera del anillo. Las celdas siguen en el CSP.

Verificado fuera de Unity sobre 599 instancias: para la misma instancia, ThisWork, matched y native coinciden en la factibilidad tras `t_init` y en el número de celdas indecisas, es decir, son representaciones equivalentes del mismo CSP.

**Especificación de tiles fijas.** Es una **propuesta pendiente de confirmar**: una copia de pueblo, aserradero y campfire en nature, 2 de sand_palm en desert, y una de SiloDown y otra de HayBale en farm. Todas en la capa y=1, con rotación aleatoria. La razón de restringir a y=1: con capas y límite activos, el análisis de consistencia de arco indica que estas tiles solo son factibles en y=1 y lejos del anillo. Con la semántica "cualquier celda libre" de REFACTOR casi todas las instancias son contradictorias de entrada. Incluso en y=1 hay posiciones infactibles, que aparecen como `contradictions_init`.

## 7. Semillas y reproducibilidad

- `solver_seed = H(base_seed, tileset, tamaño, fase, run)` y `instance_seed = H(base_seed, tileset, tamaño, fase, run, intento)`.
- No dependen del solver ni del nivel: todos los solvers reciben la misma secuencia, y B3 y B4 comparten posiciones de tiles fijas en el mismo run e intento.
- El RNG del solver se crea una vez por run, así que los reintentos continúan la misma secuencia y toman decisiones nuevas.
- Las semillas se guardan en cada fila. Repetir con el mismo `base_seed` reproduce exactamente soluciones e intentos (verificado).

## 8. Warm-up

Se ejecutan **3 runs de calentamiento** por configuración y solver antes de los 50 medidos. Se registran (`phase = warmup`) y no entran en el resumen. **Se usan los 50 runs medidos completos**; ya no se descarta el primero. `summary.csv` incluye los tiempos de calentamiento (`warmup_run_times_ms`) y el del primer run medido para cuantificar el efecto.

En Mono 6.8 (fuera de Unity) el primer run de calentamiento fue en torno a un 10–20 % más lento en algunas configuraciones.

## 9. Cómo lanzar

**Antes de medir:** pon el editor en **Code Optimization = Release** (icono del bicho, esquina inferior derecha del editor). En modo Debug, Mono no optimiza el JIT y los tiempos no son representativos. El manifest registra este ajuste y el runner avisa si está en Debug. Cierra otras aplicaciones y conecta el portátil a la corriente si aplica.

1. Menú **WFC ▸ Runtime Benchmark ▸ Crear escena de benchmark**. Crea `Assets/Scenes/ARTICLE/RuntimeBenchmark.unity` con el runner y los tres tilesets del artículo ya cargados (mismas listas de prefabs que `ARTICLE/Nature`, `WFC_Desert` y `WFC_Granja`).
2. Revisa en el Inspector la especificación de tiles fijas.
3. **Benchmark A:** `runBenchmarkA = true`, `runBenchmarkB = false` → Play.
4. **Benchmark B:** `runBenchmarkA = false`, `runBenchmarkB = true` (niveles en `benchmarkBLevels`) → Play.
5. Ambos a la vez: los dos activos.
6. Prueba rápida: `runsPerConfig = 3`, `warmupRuns = 1` y un solo tamaño.
7. Duración orientativa de la matriz completa (A + B): unos 10–20 minutos.

Para el análisis: `python analyze_runtime.py "<carpeta de resultados>" --md tablas.md`. Menú **WFC ▸ Runtime Benchmark ▸ Abrir carpeta de resultados**.

## 10. Ficheros de salida

Cada ejecución crea `<persistentDataPath>/RuntimeBenchmark/<yyyyMMdd_HHmmss>_<A|B|AB>/` (nunca sobrescribe):

- `manifest.txt`: entorno (Unity, CPU, optimización del editor), ajustes, tilesets compilados (tiles, variantes, relaciones, asimetrías, tiles fijas) y lista de configuraciones.
- `attempts.csv`: una fila por intento (incluido el calentamiento).
- `runs.csv`: una fila por run.
- `summary.csv`: una fila por configuración y solver (solo runs medidos).
- `log.txt`: tiempos de `Prepare`, avisos y errores.

**attempts.csv:** `config_id, benchmark, level_id, level_order, tileset, size_label, dim_x, dim_y, dim_z, total_cells, solver, solver_variant, phase, run_index, attempt_index, solver_seed, instance_seed, effective_free_cells, solver_csp_cells, layer_cells, boundary_cells, fixed_tile_cells, fixed_tiles_requested, undecided_after_init, init_ok, success, fail_phase, decisions, t_init_ms, t_search_ms, t_attempt_ms`

**runs.csv:** `config_id, benchmark, level_id, level_order, tileset, size_label, dim_x, dim_y, dim_z, total_cells, solver, solver_variant, phase, run_index, order_position, solver_seed, attempts, contradictions, contradictions_init, contradictions_search, solved, first_attempt_success, effective_free_cells, solver_csp_cells, layer_cells, boundary_cells, fixed_tile_cells, undecided_after_init, decisions, t_solve_total_ms, t_init_total_ms, t_search_total_ms, t_success_attempt_ms, t_failed_attempts_ms, time_per_free_cell_us, success_attempt_per_free_cell_us, solution_valid, adjacency_violations, solution_hash`

**summary.csv:** `config_id, benchmark, level_id, level_order, tileset, size_label, dim_x, dim_y, dim_z, total_cells, solver, solver_variant, n_runs, runs_solved, n_attempts_total, contradictions_total, contradictions_init, contradictions_search, success_rate_first_attempt, success_first_ci95_lo, success_first_ci95_hi, success_rate_pooled_attempts, effective_free_cells, solver_csp_cells, undecided_after_init_mean, decisions_mean, t_solve_sum_ms, t_solve_mean_ms, t_solve_median_ms, t_solve_q1_ms, t_solve_q3_ms, t_solve_iqr_ms, t_solve_sd_ms, t_solve_min_ms, t_solve_max_ms, t_success_attempt_mean_ms, t_success_attempt_median_ms, t_init_total_median_ms, t_search_total_median_ms, t_failed_attempt_mean_ms, time_per_free_cell_mean_us, time_per_free_cell_median_us, success_attempt_per_free_cell_median_us, warmup_run_times_ms, measured_run0_ms, all_solutions_valid`

Los cuartiles son de tipo 7 (R por defecto, numpy `linear`). El separador es la coma y el formato numérico, invariante (punto decimal).

## 11. Diferencias entre solvers que siguen siendo relevantes

- **Selector de celda:** ThisWork usa un montículo indexado (o el barrido lineal de REFACTOR, configurable). Gumin hace un barrido O(N) con ruido por candidata (fiel al original). DeBroglie usa su `HeapEntropyTracker`. Es una diferencia de implementación legítima, pero hay que declararla. ThisWork debe coincidir con lo que publica el framework: o REFACTOR incorpora el montículo, o el benchmark usa `Linear`.
- **Ruido de desempate:** 1e-6 en ThisWork y Gumin, 1e-10 en DeBroglie (propio de la librería).
- **Memoria:** ThisWork y Gumin reservan una vez por configuración (Gumin original: `Init` una vez, `Clear` por intento). `DeBroglie.Clear()` reserva memoria nueva en cada intento por diseño de la librería. Esto encarece sus reinicios y se ve en `t_init`.
- **Propagación inicial en DeBroglie:** neutralizada por defecto (punto 9 de la sección 1). La variante `matched-lazyinit` mide el comportamiento por defecto de la librería.
- **Detección de contradicciones:** los tres se detienen al vaciarse un dominio. En Gumin es una desviación deliberada del original (que devuelve `sumsOfOnes[0] > 0`).
- **Sobrecoste de librería en DeBroglie:** trackers, despacho por interfaces y mapeo tile↔patrón forman parte de su coste real.
- **Correcciones en ThisWork respecto a `MyWFC.cs`/REFACTOR** (puntos 5 y 6 de la sección 1): deberían llevarse también a REFACTOR para que el código publicado coincida con el evaluado.

## 12. Verificaciones realizadas (fuera de Unity, CoreCLR y Mono 6.8)

- 49 comprobaciones automáticas superadas en ambos runtimes, entre ellas:
  - Adyacencias simétricas y dominio = todas las tiles menos LIMIT.
  - Conteo de celdas libres por nivel y tamaño.
  - Capas y anillo colocados donde corresponde.
  - Cronómetro con un solver falso de esperas conocidas: incluye exactamente init + search de los 3 intentos y nada más.
  - `t_solve = Σ intentos`, e intentos = contradicciones + 1.
  - Mismo número de celdas para los cuatro solvers en A.
  - Variables del CSP = celdas libres en ThisWork y matched.
  - Todas las soluciones válidas (preasignadas intactas y todas las adyacencias admitidas).
  - B0 ≡ A y determinismo entre ejecuciones.
  - Equivalencia ThisWork/matched/native por instancia.
  - CSV con columnas consistentes.
- Matriz completa (A + B, 3 tilesets × 3 tamaños, 50 runs + 3 de calentamiento) ejecutada de principio a fin en Mono 6.8 en unos 8 minutos: 6 202 runs, 0 soluciones inválidas y 0 errores. En B3/B4, ThisWork y DeBroglie-matched registran exactamente las mismas contradicciones de inicialización: reciben las mismas instancias y la propagación inicial llega al mismo resultado. Los tiempos absolutos de esa ejecución **no** son válidos para el artículo (otra máquina y otro runtime).
- Los tilesets del arnés se reconstruyeron replicando el `TilePreprocessor` sobre los prefabs. Los recuentos de relaciones difieren ligeramente de la Tabla III. En Unity se usa el `TilePreprocessor` real y el manifest registra los recuentos exactos (útil para comprobar las Tablas II y III).
- El runner de Unity y el menú se han comprobado solo a nivel de compilación (contra stubs de la API de Unity). La primera ejecución real debe ser la prueba rápida del punto 9.
