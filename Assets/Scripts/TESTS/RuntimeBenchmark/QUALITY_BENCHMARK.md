# Benchmark de calidad (ToG-2026-0037.R1)

Este documento describe el benchmark de **calidad** de las soluciones (JS global, JS condicionada, entropía de Shannon, diversidad entre ejecuciones y tasa de éxito por intento). El benchmark de coste computacional se describe en `RUNTIME_BENCHMARK.md`; ambos comparten la misma infraestructura (solvers, instancias, semillas), pero sus resultados se escriben en carpetas distintas y nunca se mezclan.

Código: `Core/RBQuality.cs` (motor y métricas, sin dependencias de Unity) y el modo `Experiment = Quality` de `RuntimeBenchmarkRunner.cs`. Análisis: `analyze_quality.py`.

## 1. Problemas del procedimiento anterior (`WFCQualityMetrics.cs`)

El script anterior se conserva sin cambios como referencia histórica, pero no debe usarse para el artículo. Los problemas detectados fueron los siguientes. La distribución objetivo dependía del orden de `Awake` (no definido en el proyecto), de modo que podía o no incluir las variantes rotadas. Se descartaba la primera generación como calentamiento, lo que es innecesario cuando no se mide tiempo y reduce la muestra. La tasa de éxito se calculaba de forma inversa. La adherencia a restricciones era vacía porque ninguna configuración tenía tiles fijas. La entropía calculada (media y varianza por cuadrantes) no coincidía con la definición del pie de la tabla del artículo. La diversidad asignaba −1 a las tiles de infraestructura y comparaba conjuntos de celdas distintos según el solver. La distribución empírica incluía celdas preasignadas. Por último, los tres solvers eran MonoBehaviours con implementaciones no equivalentes del problema.

## 2. Diseño

**Q-NEUTRAL** (`runBenchmarkA`): ThisWork, Gumin y DeBroglie sobre `nature`, `desert` y `farm` en 10x10x5, 20x20x5 y 30x30x5, sin restricciones (`Q_UNRESTRICTED`). Es el mismo problema que el Benchmark A de runtime.

**Q-ABLATION** (`runBenchmarkB`): ThisWork y DeBroglie (modo `matched`) con niveles acumulativos `Q0_BASELINE`, `Q1_LAYERS`, `Q2_LAYERS_BOUNDARY`, `Q3_LAYERS_BOUNDARY_FIXED` y `Q4_LAYERS_BOUNDARY_FIXED_NEGATIVE`. Son los niveles B0 a B4 del benchmark de runtime con otra etiqueta; Gumin queda excluido porque no admite restricciones. Q0 es el mismo problema que Q-NEUTRAL (verificado: mismos mapas).

Tiles fijas (capa y = 1): `nature` pueblo x1 y aserradero x1; `desert` sand_palm x2; `farm` HayBale x2.

## 3. Protocolo

Para cada configuración y solver se generan **exactamente R = 50 mapas** (`runsPerConfig`). Cada run reinicia ante contradicción hasta obtener una solución (tope de seguridad `maxAttemptsPerRun` = 1000; si se alcanzara, se registra `[ERROR]` en `log.txt`). No hay warm-up, no se descarta ninguna generación (el run 0 se evalúa) y no se mide tiempo ni se fuerza GC.

Las semillas son las de la fase `measured` del benchmark de runtime (`Seeds.Derive` con `baseSeed`): semilla del solver `solver|tileset|MXxMYxMZ|measured|r` y semilla de la instancia `instance|tileset|dims|measured|r|a` para el intento a. En consecuencia, el mapa r de calidad es exactamente la solución del run r medido en runtime (mismo `solution_hash`), lo que puede comprobarse con `runtimeRunsCsvForHashCheck`.

Cada intento usa su propia instancia (en Q3/Q4 las posiciones de las tiles fijas dependen de r y de a). Para ThisWork y DeBroglie la instancia del run r coincide cuando ambos resuelven en el mismo intento, que es lo habitual (en las pruebas, 748/750 runs de ablation).

## 4. Métricas

**Celdas evaluadas.** Solo las celdas libres de la instancia del intento con éxito (no preasignadas por capas, límite o tiles fijas).

**Identidad de tile para JS y entropía.** Tipo de tile jugable: las rotaciones se agregan en su tipo y se excluyen SOLID, EMPTY y LIMIT (`Tile.isInfrastructureTile`). Sea K el número de tipos jugables y q el vector de frecuencias relativas de los tipos jugables observados en las celdas libres del mapa.

**Distribución original.** p_orig(k) proporcional a la suma de los pesos de muestreo de las variantes del tipo k (cada variante rotada recibe el peso completo de su tile base, `probability > 0 ? probability : 1`), restringida a los tipos jugables y normalizada. Es la distribución que el sampler usa realmente.

**JS global.** JS(p_orig, q) con logaritmo natural, en [0, ln 2]. Mide cuánto se aleja la composición del mapa de los pesos declarados. Incluye el efecto de las reglas de adyacencia y de las restricciones, por lo que un valor mayor no indica por sí solo un solver peor.

**JS condicionada.** JS(p_cond, q). La referencia condicionada se calcula por mapa con una sonda (ThisWork, independiente del solver evaluado) que aplica la instancia y la propagación inicial (arco-consistencia). Para cada celda libre c con dominio D_c se toma P_c(t) = w_t / suma de w_s sobre s en D_c; se suman las P_c de todas las celdas libres, se agregan por tipo jugable y se normaliza. Es la composición esperada si cada celda muestreara sus pesos dentro de su dominio factible tras las restricciones. Es una aproximación de primer orden (ignora las dependencias entre celdas posteriores a la propagación inicial), por lo que su valor no es cero para un sampler perfecto; sirve para separar el efecto de las restricciones del efecto del solver. `cond_impossible_types` cuenta los tipos con p_orig > 0 y p_cond = 0.

**Entropía.** Shannon H(q) en nats, con 0 ≤ H ≤ ln K. Se informan además la media y la varianza de la entropía por cuadrantes XZ (definición de la versión anterior, solo por continuidad).

**Diversidad.** Distancia de Hamming normalizada entre todos los pares de mapas de la misma configuración y solver (1225 pares para 50 mapas), con identidad de variante (rotación incluida) e incluyendo infraestructura. `diversity` compara las celdas libres en ambos mapas. `diversity_undecided` compara solo las celdas que, en ambas instancias, conservan más de una tile tras la propagación inicial: excluye las celdas libres que las restricciones ya determinan (por ejemplo, las capas superiores forzadas a EMPTY en Q2), que diluyen la medida. `diversity_undecided_type` usa esas mismas celdas pero con identidad de tipo (rotaciones agregadas), de modo que un cambio solo de rotación no cuenta como diversidad. Los conjuntos de celdas son independientes del solver. No se informa IC de la diversidad porque los pares no son independientes.

**Tasa de éxito por intento.** `attempt_success_rate` = mapas / intentos totales. La tasa de éxito por run es 1 por construcción.

**Estadística.** Media, desviación típica e IC95 con t de Student (n = 50) para JS global, JS condicionada y entropía. `analyze_quality.py` añade contrastes Mann-Whitney bilaterales (sin corrección por comparaciones múltiples) como contraste de diferencia, no de calidad.

## 5. Cómo lanzar

1. Abrir `Assets/Scenes/ARTICLE/RuntimeBenchmark.unity` (o crearla con *WFC ▸ Runtime Benchmark ▸ Crear escena de benchmark*). Comprobar que las tiles fijas son las del apartado 2.
2. En el componente `RuntimeBenchmarkRunner`: `Experiment = Quality`, `runsPerConfig = 50`, `maxAttemptsPerRun = 1000`, `baseSeed = 20261007`, `csvFormat = ExcelSpanish`. Los campos `warmupRuns`, `gcBeforeTimedPhases` e `interleaveSolvers` se ignoran.
3. Q-NEUTRAL: `runBenchmarkA = true`, `runBenchmarkB = false`. Q-ABLATION: `runBenchmarkA = false`, `runBenchmarkB = true` (los cinco niveles activos). Pueden lanzarse juntos; se escriben en subcarpetas separadas.
4. Opcional: `runtimeRunsCsvForHashCheck` = ruta del `runs.csv` del benchmark de runtime (`.../20261008_094155_AB/runs.csv`). Requiere los mismos tilesets, tiles fijas, `baseSeed` y `maxAttemptsPerRun`.
5. Pulsar Play. La optimización de código (Debug/Release) no afecta a los resultados de calidad.
6. `python analyze_quality.py <carpeta de la ejecución>`.

## 6. Ficheros de salida

Carpeta `<persistentDataPath>/QualityBenchmark/<fecha>_QUALITY_<NEUTRAL|ABLATION|NEUTRAL_ABLATION>/`:

`manifest.txt` (entorno, protocolo, tilesets, tipos jugables con p_orig y matriz de configuraciones) y, si se activó, `runtime_hash_check.txt`. En cada subcarpeta `Q-NEUTRAL/` y `Q-ABLATION/`:

`quality_maps.csv`, una fila por mapa: `config_id, benchmark, level_id, level_order, runtime_config_id, tileset, size_label, dim_x, dim_y, dim_z, total_cells, solver, solver_variant, run_index, solver_seed, attempts, solution_hash, solution_valid, adjacency_violations, layer_cells, boundary_cells, fixed_tile_cells, free_cells, playable_free_cells, infra_free_cells, js_global, js_conditional, entropy, entropy_quadrant_mean, entropy_quadrant_var, diversity_to_others_mean, types_present, cond_support_types, cond_impossible_types, undecided_cells, diversity_undecided_to_others_mean, diversity_undecided_type_to_others_mean`.

`quality_summary.csv`, una fila por configuración y solver: identificación, `n_maps, attempts_total, attempts_mean, attempt_success_rate, all_solutions_valid, free_cells_mean, playable_free_cells_mean`, `js_global_{mean,sd,ci95_lo,ci95_hi}`, `js_conditional_{...}`, `entropy_{...}`, `entropy_quadrant_mean_mean, entropy_quadrant_var_mean, diversity_mean, diversity_sd, diversity_pairs, compared_cells_mean, types_present_mean, cond_support_types_mean, cond_impossible_types_mean, undecided_cells_mean, diversity_undecided_mean, diversity_undecided_sd, compared_undecided_cells_mean, diversity_undecided_type_mean, diversity_undecided_type_sd`.

`quality_reference.csv`, una fila por configuración, solver y tipo jugable: `p_original`, `p_conditional_mean` (media de p_cond sobre los 50 mapas) y `q_empirical_mean`.

`quality_tiles.csv`: tabla de tiles de cada tileset compilado (id, nombre, tipo, tile base, rotación, peso, dominio, infraestructura, tipo jugable), necesaria para decodificar los mapas.

`quality_maps_data.csv.gz`: todos los mapas, `config_id, solver, run_index, solution_hash, tiles`, con `tiles` = ids separados por espacios en el orden de celda x + z·MX + y·MX·MZ; las celdas preasignadas llevan prefijo `*`.

`log.txt`: errores y cierre.

## 7. Verificaciones (arnés fuera de Unity, CoreCLR y Mono 6.8, 10x10x5, 1950 mapas)

Todas superadas: JS(p, p) = 0, JS de soportes disjuntos = ln 2, H(uniforme) = ln K; SOLID/EMPTY/LIMIT fuera del soporte; 39 grupos con exactamente 50 mapas y `run_index` 0..49; 0 violaciones de adyacencia; Gumin ausente de la ablation; `solution_hash` e intentos idénticos al benchmark de runtime en 1950/1950 mapas (también leyendo un `runs.csv` en formato español); Q0 idéntico a Q-NEUTRAL (50/50 por tileset y solver); celdas libres 500/300/264/262/262 con las restricciones activas que corresponden a cada nivel; p_orig, p_cond y q normalizadas y con el mismo soporte; JS en [0, ln 2], H en [0, ln K], diversidad en [0, 1], diversidad por tipo menor o igual que por variante; ningún tipo observado tiene p_cond = 0; el fichero comprimido reproduce los hashes; resultados idénticos byte a byte entre CoreCLR y Mono. El solver de ThisWork no cambia (49/49 pruebas del benchmark de runtime y 360/360 runs de `SingleRun` iguales al motor).

## 8. Limitaciones conocidas

La referencia condicionada es de primer orden (apartado 4). Los tilesets del arnés son una reconstrucción; la equivalencia exacta con Unity se comprueba con `runtimeRunsCsvForHashCheck`. En Q3/Q4 una pequeña fracción de runs usa instancias distintas en ThisWork y DeBroglie cuando resuelven en intentos distintos; la distribución de instancias es la misma para ambos, pero el emparejamiento run a run no está garantizado.
