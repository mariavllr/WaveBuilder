using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using static UnityEngine.InputSystem.Controls.DiscreteButtonControl;
using Debug = UnityEngine.Debug;

/// <summary>
/// Modo de escritura de resultados de los benchmarks.
/// Compartido por CalculateExecutionTime (tiempos) y WFCQualityMetrics (calidad),
/// de modo que ambos scripts se configuren con el mismo criterio.
/// </summary>
public enum WriteMode
{
    /// <summary>
    /// Escribe solo si el experimento NO existe todavía en el CSV.
    /// Si ya existe, no escribe nada y avisa por consola.
    /// Modo seguro: protege los datos ya recogidos. Úsalo por defecto.
    /// </summary>
    WriteIfNew,

    /// <summary>
    /// Sobrescribe el experimento aunque ya exista, conservando el resto del
    /// fichero. Úsalo para repetir una medición concreta (p. ej. gumin en
    /// desert 10x10x5) sin perder los datos de los demás algoritmos.
    /// Acuérdate de volver a WriteIfNew al terminar.
    /// </summary>
    Overwrite,

    /// <summary>
    /// No toca ningún fichero. Ejecuta el experimento y vuelca los resultados
    /// solo por consola. Para pruebas y depuración.
    /// </summary>
    ConsoleOnly
}

/// <summary>
/// Script central de los tests de rendimiento WFC.
/// Centraliza toda la lógica de test: qué medir, cuándo, cuántas veces y qué guardar.
///
/// REFACTOR, GuminWFC y DeBroglieWFC solo disparan eventos; este script decide qué escuchar.
/// DeBroglieWFC reutiliza los eventos estáticos de REFACTOR (onStartGeneration,
/// onEndGeneration, onIncompatibility), por lo que el cronómetro y los handlers son idénticos.
///
/// Tests disponibles para MyWFC (REFACTOR):
///   ALL_GENERATION    – tiempo de generar el mapa completo (GENERATE_ALL)
///   CUBE_GENERATION   – tiempo de generar el cubo inicial (modo juego)
///   TILE_PROPAGATION  – tiempo de respuesta al colocar una ficha (modo juego)
///
/// Tests disponibles para Gumin:
///   ALL_GENERATION    – único test aplicable
///
/// Tests disponibles para DeBroglie:
///   ALL_GENERATION    – único test aplicable; no soporta modo juego ni propagación incremental
/// </summary>
public class CalculateExecutionTime : MonoBehaviour
{
    public enum StopwatchTest { ALL_GENERATION, CUBE_GENERATION, TILE_PROPAGATION }

    [Header("¿Qué algoritmo testear? (máximo uno activo)")]
    public bool testMyWFC = false;
    public StopwatchTest testTypeMyWFC = StopwatchTest.ALL_GENERATION;
    public bool testGumin = false;
    public bool testDeBroglie = false;

    [Header("Configuración del test")]
    public int numberOfGenerations = 50;

    [Tooltip("WriteIfNew  → escribe solo si este algoritmo aún no está en el CSV (modo seguro).\n" +
             "Overwrite   → repite y sobrescribe SOLO la columna de este algoritmo, " +
             "conservando las de los demás. Acuérdate de volver a WriteIfNew al terminar.\n" +
             "ConsoleOnly → no escribe ningún fichero; solo muestra por consola (pruebas).")]
    public WriteMode writeMode = WriteMode.WriteIfNew;

    [Header("Etiqueta del experimento")]
    [Tooltip("Tileset activo. Se usa en el nombre del CSV: times_{tileset}_{mapSize}.csv")]
    public string tilesetName = "nature";
    [Tooltip("Algoritmo + config activos. Se convierte en la cabecera de columna del CSV.\n" +
             "Valores recomendados: gumin_prob | mi_wfc_prob | mi_wfc_full | debroglie_prob | debroglie_full")]
    public string algorithmLabel = "mi_wfc_full";

    [Header("Referencias")]
    [SerializeField] private GuminWFC guminWFC;
    [SerializeField] private DeBroglieWFC debroglie;

    // ── estado interno ──────────────────────────────────────────────
    private WaveFunctionGame_REFACTOR wfc;
    private Stopwatch stopwatch;

    private bool active = false;
    private bool writeToCSV = true;
    private bool incompatibility = false;

    private int incCounter = 0;
    private int totalIncompat = 0;
    private int generationsDone = 0;

    private double timeSum = 0, maxTime = 0, minTime = 0;

    // Dispara la siguiente generación en Update() para evitar recursión síncrona.
    // No se usa para TILE_PROPAGATION (la siguiente medición la activa el jugador).
    private bool pendingNext = false;

    private string mapSize;
    private List<string[]> tabla = new List<string[]>();
    // Nombre del CSV: times_{tileset}_{mapSize}.csv  (ej. times_nature_10x10x5.csv)
    // La columna dentro del CSV identifica el algoritmo/config (algorithmLabel).
    private string FilePath => Path.Combine(Application.persistentDataPath,
        $"times_{tilesetName}_{mapSize}.csv");

    // ════════════════════════════════════════════════════════════════
    // INICIALIZACIÓN
    // ════════════════════════════════════════════════════════════════

    void Awake()
    {
        wfc = GetComponent<WaveFunctionGame_REFACTOR>();
        stopwatch = new Stopwatch();

        int activos = (testMyWFC ? 1 : 0) + (testGumin ? 1 : 0) + (testDeBroglie ? 1 : 0);
        if (activos > 1)
        {
            Debug.LogError("[Benchmark] Solo puede ejecutarse un test a la vez. Desactiva los demás.");
            return;
        }

        active = activos == 1;
        if (!active) return;

        // Suscribirse solo al par de eventos que corresponde al test activo
        if (testMyWFC)
        {
            switch (testTypeMyWFC)
            {
                case StopwatchTest.ALL_GENERATION:
                    WaveFunctionGame_REFACTOR.onStartGeneration += OnStart;
                    WaveFunctionGame_REFACTOR.onEndGeneration += OnEnd;
                    WaveFunctionGame_REFACTOR.onIncompatibility += OnIncompat;
                    break;
                case StopwatchTest.CUBE_GENERATION:
                    WaveFunctionGame_REFACTOR.onStartCubeGeneration += OnStart;
                    WaveFunctionGame_REFACTOR.onEndCubeGeneration += OnEnd;
                    WaveFunctionGame_REFACTOR.onIncompatibility += OnIncompat;
                    break;
                case StopwatchTest.TILE_PROPAGATION:
                    WaveFunctionGame_REFACTOR.onStartTilePropagation += OnStart;
                    WaveFunctionGame_REFACTOR.onEndTilePropagation += OnEnd;
                    // Sin incompatibilidad: la colocación de fichas no genera contradicciones
                    break;
            }
        }
        else if (testGumin) // solo ALL_GENERATION
        {
            GuminWFC.onStartGeneration += OnStart;
            GuminWFC.onEndGeneration += OnEnd;
            GuminWFC.onIncompatibility += OnIncompat;
        }
        else if (testDeBroglie) // solo ALL_GENERATION
        {
            // El adaptador DeBroglieWFC dispara los eventos estáticos de REFACTOR,
            // así que reutilizamos exactamente las mismas suscripciones que para
            // testMyWFC en modo ALL_GENERATION. Esto garantiza que el contrato del
            // cronómetro es idéntico entre ambos sistemas.
            WaveFunctionGame_REFACTOR.onStartGeneration += OnStart;
            WaveFunctionGame_REFACTOR.onEndGeneration += OnEnd;
            WaveFunctionGame_REFACTOR.onIncompatibility += OnIncompat;
        }

        if (testMyWFC)
            mapSize = $"{wfc.dimensionsX}x{wfc.dimensionsZ}x{wfc.dimensionsY}";
        else if (testGumin && guminWFC != null)
            mapSize = $"{guminWFC.dimensionsX}x{guminWFC.dimensionsZ}x{guminWFC.dimensionsY}";
        else if (testDeBroglie && debroglie != null)
            mapSize = $"{debroglie.dimensionsX}x{debroglie.dimensionsZ}x{debroglie.dimensionsY}";
        PrepararCSV();
    }

    void Start()
    {
        if (!active) return;

        if (testGumin)
        {
            if (guminWFC == null) { Debug.LogError("[Benchmark] guminWFC no asignado en el Inspector."); return; }
            guminWFC.Generate();
        }
        else if (testDeBroglie)
        {
            if (debroglie == null) { Debug.LogError("[Benchmark] debroglie no asignado en el Inspector."); return; }
            debroglie.Generate();
        }
        // testMyWFC arranca por su propio Awake/Start de REFACTOR; no se dispara aquí.
    }

    void OnDestroy()
    {
        WaveFunctionGame_REFACTOR.onStartGeneration -= OnStart;
        WaveFunctionGame_REFACTOR.onEndGeneration -= OnEnd;
        WaveFunctionGame_REFACTOR.onStartCubeGeneration -= OnStart;
        WaveFunctionGame_REFACTOR.onEndCubeGeneration -= OnEnd;
        WaveFunctionGame_REFACTOR.onStartTilePropagation -= OnStart;
        WaveFunctionGame_REFACTOR.onEndTilePropagation -= OnEnd;
        WaveFunctionGame_REFACTOR.onIncompatibility -= OnIncompat;
        GuminWFC.onStartGeneration -= OnStart;
        GuminWFC.onEndGeneration -= OnEnd;
        GuminWFC.onIncompatibility -= OnIncompat;
    }

    // ════════════════════════════════════════════════════════════════
    // LOOP DE GENERACIONES (solo ALL_GENERATION y CUBE_GENERATION)
    // ════════════════════════════════════════════════════════════════

    void Update()
    {
        if (!pendingNext) return;
        pendingNext = false;

        if (testGumin) guminWFC.Generate();
        else if (testDeBroglie) debroglie.Regenerate();
        else if (testMyWFC) wfc.Regenerate();
    }

    // ════════════════════════════════════════════════════════════════
    // HANDLERS DE EVENTOS
    // ════════════════════════════════════════════════════════════════

    private void OnStart()
    {
        if (!incompatibility) stopwatch.Restart();
    }

    private void OnEnd()
    {
        stopwatch.Stop();
        incompatibility = false;

        double t = stopwatch.Elapsed.TotalSeconds;
        timeSum += t;
        if (t > maxTime) maxTime = t;
        if (minTime == 0 || t < minTime) minTime = t;

        generationsDone++;
        totalIncompat += incCounter;
        incCounter = 0;

        Debug.Log($"[Benchmark] Medición {generationsDone}/{numberOfGenerations}: {t:F4}s");

        if (writeToCSV)
        {
            int col = ObtenerColumna(tabla, algorithmLabel);
            AsegurarFila(tabla, generationsDone);
            if (col >= 0 && col < tabla[generationsDone].Length)
                tabla[generationsDone][col] = t.ToString("F4");
            GuardarCSV(tabla);
        }

        if (generationsDone >= numberOfGenerations)
            FinalizarBenchmark();
        else if (testGumin || testDeBroglie || testTypeMyWFC != StopwatchTest.TILE_PROPAGATION)
            pendingNext = true;
        // TILE_PROPAGATION (solo testMyWFC): no se dispara pendingNext; la siguiente
        // medición ocurre cuando el jugador coloca la siguiente ficha.
    }

    private void OnIncompat()
    {
        incompatibility = true;
        incCounter++;
    }

    // ════════════════════════════════════════════════════════════════
    // FINAL DEL BENCHMARK
    // ════════════════════════════════════════════════════════════════

    private void FinalizarBenchmark()
    {
        active = false;

        double avg = timeSum / generationsDone;
        int attempts = totalIncompat + generationsDone;
        float failRate = attempts > 0 ? (float)totalIncompat / attempts * 100f : 0f;

        Debug.Log($"[Benchmark] ── COMPLETADO ({numberOfGenerations} mediciones) ──");
        Debug.Log($"[Benchmark] Avg: {avg:F4}s | Max: {maxTime:F4}s | Min: {minTime:F4}s");
        Debug.Log($"[Benchmark] Fail rate: {failRate:F1}% | Incompatibilidades: {totalIncompat}");

        if (!writeToCSV) return;

        int col = ObtenerColumna(tabla, algorithmLabel);
        if (col < 0) { GuardarCSV(tabla); return; }

        EscribirStat("Avg Time", col, avg.ToString("F4"));
        EscribirStat("Min Time", col, minTime.ToString("F4"));
        EscribirStat("Max Time", col, maxTime.ToString("F4"));
        EscribirStat("Incompat.", col, totalIncompat.ToString());
        EscribirStat("Fail Rate", col, failRate.ToString("F2") + " %");
        GuardarCSV(tabla);
    }

    // ════════════════════════════════════════════════════════════════
    // CSV
    // ════════════════════════════════════════════════════════════════

    private void PrepararCSV()
    {
        // ── ConsoleOnly: no se toca ningún fichero ──────────────────────────
        if (writeMode == WriteMode.ConsoleOnly)
        {
            writeToCSV = false;
            Debug.Log($"[Benchmark] MODO CONSOLA. No se escribirá ningún fichero. " +
                      $"Resultados de '{algorithmLabel}' solo por consola.");
            return;
        }

        // ── El CSV no existe: se crea desde cero ───────────────────────────
        if (!File.Exists(FilePath))
        {
            // Inicializar con la columna n_gen; AñadirColumna añade el algoritmo a continuación.
            tabla = new List<string[]> { new[] { "n_gen" } };
            AñadirColumna(tabla, algorithmLabel);
            GuardarCSV(tabla);
            writeToCSV = true;
            Debug.Log($"[Benchmark] CSV creado: {FilePath}");
            return;
        }

        tabla = LeerCSV();
        int col = ObtenerColumna(tabla, algorithmLabel);

        // ── La columna no existe: se añade con normalidad ───────────────────
        if (col == -1)
        {
            AñadirColumna(tabla, algorithmLabel);
            GuardarCSV(tabla);
            writeToCSV = true;
            Debug.Log($"[Benchmark] Añadida columna '{algorithmLabel}' a {FilePath}");
            return;
        }

        // ── La columna YA existe ────────────────────────────────────────────
        if (writeMode == WriteMode.WriteIfNew)
        {
            writeToCSV = false;
            Debug.LogWarning($"[Benchmark] '{algorithmLabel}' ya existe en el CSV. " +
                             "No se escribe (modo WriteIfNew); solo se mostrará por consola. " +
                             "Usa Overwrite para repetir este experimento.");
            return;
        }

        // Overwrite: limpiar SOLO esta columna, incluidas las filas de
        // estadísticas finales (Avg Time, Fail Rate…), dejando intactas las
        // columnas de los demás algoritmos.
        for (int fila = 1; fila < tabla.Count; fila++)
            if (col < tabla[fila].Length)
                tabla[fila][col] = "";

        GuardarCSV(tabla);
        writeToCSV = true;
        Debug.LogWarning($"[Benchmark] SOBRESCRIBIENDO la columna '{algorithmLabel}' en {FilePath}. " +
                         "Los datos previos de este algoritmo se han descartado; " +
                         "el resto del fichero se conserva.");
    }

    private List<string[]> LeerCSV()
    {
        var t = new List<string[]>();
        foreach (var l in File.ReadAllLines(FilePath))
            t.Add(l.Split(';'));
        return t;
    }

    private int ObtenerColumna(List<string[]> t, string size)
    {
        if (t.Count == 0) return -1;
        for (int i = 0; i < t[0].Length; i++)
            if (t[0][i] == size) return i;
        return -1;
    }

    private void AñadirColumna(List<string[]> t, string size)
    {
        if (t.Count == 0) t.Add(new string[0]);
        for (int i = 0; i < t.Count; i++)
        {
            var old = t[i];
            var nueva = new string[old.Length + 1];
            for (int j = 0; j < old.Length; j++) nueva[j] = old[j];
            nueva[old.Length] = (i == 0) ? size : "";
            t[i] = nueva;
        }
    }

    private void AsegurarFila(List<string[]> t, int idx)
    {
        int cols = t.Count > 0 ? t[0].Length : 2;
        while (t.Count <= idx)
        {
            var f = new string[cols];
            f[0] = t.Count.ToString(); // número de generación
            t.Add(f);
        }
    }

    private void EscribirStat(string etiqueta, int col, string valor)
    {
        int fila = -1;
        for (int i = 0; i < tabla.Count; i++)
            if (tabla[i][0] == etiqueta) { fila = i; break; }
        if (fila == -1)
        {
            var f = new string[tabla[0].Length];
            f[0] = etiqueta;
            tabla.Add(f);
            fila = tabla.Count - 1;
        }
        if (col < tabla[fila].Length)
            tabla[fila][col] = valor;
    }

    private void GuardarCSV(List<string[]> t)
    {
        using var sw = new StreamWriter(FilePath);
        foreach (var fila in t)
            sw.WriteLine(string.Join(";", fila));
    }
}