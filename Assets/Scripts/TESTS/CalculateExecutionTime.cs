using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Script central de los tests de rendimiento WFC.
/// Centraliza toda la lógica de test: qué medir, cuándo, cuántas veces y qué guardar.
///
/// REFACTOR y GuminWFC solo disparan eventos; este script decide qué escuchar.
///
/// Tests disponibles para MyWFC (REFACTOR):
///   ALL_GENERATION    – tiempo de generar el mapa completo (GENERATE_ALL)
///   CUBE_GENERATION   – tiempo de generar el cubo inicial (modo juego)
///   TILE_PROPAGATION  – tiempo de respuesta al colocar una ficha (modo juego)
///
/// Tests disponibles para Gumin:
///   ALL_GENERATION    – único test aplicable
///
/// Estructura del CSV resultante (una tabla por archivo, una columna por algoritmo):
///   n de generacion ; gumin_prob ; mi_wfc_prob ; mi_wfc_full
///   1               ; 2.3412     ; 2.1034      ; 2.4512
///   ...
///   Media           ; 2.20       ; 1.95        ; 2.30
///   Max             ; 2.34       ; 2.21        ; 2.51
///   Min             ; 1.98       ; 1.78        ; 2.10
///   Incompatibilidades ; 3       ; 2           ; 4
///   Fail rate       ; 5.00 %     ; 4.00 %      ; 6.00 %
///
/// Flujo de uso:
///   1. Configura testGumin=true y columnLabel="gumin_prob" → Play → se genera la primera columna
///   2. Configura testMyWFC=true y columnLabel="mi_wfc_prob" → Play → se añade la segunda columna
///   3. Configura testMyWFC=true y columnLabel="mi_wfc_full" → Play → se añade la tercera columna
/// </summary>
public class CalculateExecutionTime : MonoBehaviour
{
    public enum StopwatchTest { ALL_GENERATION, CUBE_GENERATION, TILE_PROPAGATION }

    [Header("¿Qué algoritmo testear? (máximo uno activo)")]
    public bool testMyWFC = false;
    public StopwatchTest testTypeMyWFC = StopwatchTest.ALL_GENERATION;
    public bool testGumin = false;

    [Header("Configuración del test")]
    public int numberOfGenerations = 50;

    [Tooltip("Nombre del archivo CSV sin extensión. Debe incluir tileset y tamaño: p.ej. times_nature_10x10x5")]
    public string nombreArchivo = "WFC_Benchmark";

    [Tooltip("Etiqueta de la columna que se va a medir en esta sesión: gumin_prob | mi_wfc_prob | mi_wfc_full")]
    public string columnLabel = "gumin_prob";

    [Header("Referencias")]
    [SerializeField] private GuminWFC guminWFC;

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

    private List<string[]> tabla = new List<string[]>();
    private string FilePath => Path.Combine(Application.persistentDataPath, nombreArchivo + ".csv");

    // ════════════════════════════════════════════════════════════════
    // INICIALIZACIÓN
    // ════════════════════════════════════════════════════════════════

    void Awake()
    {
        wfc = GetComponent<WaveFunctionGame_REFACTOR>();
        stopwatch = new Stopwatch();

        if (testMyWFC && testGumin)
        {
            Debug.LogError("[Benchmark] Solo puede ejecutarse un test a la vez. Desactiva uno de los dos.");
            return;
        }

        active = testMyWFC || testGumin;
        if (!active) return;

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
                    break;
            }
        }
        else // testGumin
        {
            GuminWFC.onStartGeneration += OnStart;
            GuminWFC.onEndGeneration += OnEnd;
            GuminWFC.onIncompatibility += OnIncompat;
        }

        PrepararCSV();
    }

    void Start()
    {
        if (!active || !testGumin) return;

        if (guminWFC == null) { Debug.LogError("[Benchmark] guminWFC no asignado en el Inspector."); return; }
        //guminWFC.tileObjects = wfc.tileObjects;
        guminWFC.Generate();
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
    // LOOP DE GENERACIONES
    // ════════════════════════════════════════════════════════════════

    void Update()
    {
        if (!pendingNext) return;
        pendingNext = false;

        if (testGumin) guminWFC.Generate();
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
            int col = ObtenerColumna(tabla, columnLabel);
            AsegurarFila(tabla, generationsDone);
            if (col >= 0 && generationsDone < tabla.Count && col < tabla[generationsDone].Length)
                tabla[generationsDone][col] = t.ToString("F4");
            GuardarCSV(tabla);
        }

        if (generationsDone >= numberOfGenerations)
            FinalizarBenchmark();
        else if (testTypeMyWFC != StopwatchTest.TILE_PROPAGATION || testGumin)
            pendingNext = true;
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

        int col = ObtenerColumna(tabla, columnLabel);
        if (col < 0) { GuardarCSV(tabla); return; }

        EscribirStat("Media", col, avg.ToString("F4"));
        EscribirStat("Max", col, maxTime.ToString("F4"));
        EscribirStat("Min", col, minTime.ToString("F4"));
        EscribirStat("Incompatibilidades", col, totalIncompat.ToString());
        EscribirStat("Fail rate", col, failRate.ToString("F2") + " %");
        GuardarCSV(tabla);
    }

    // ════════════════════════════════════════════════════════════════
    // CSV
    // ════════════════════════════════════════════════════════════════

    private void PrepararCSV()
    {
        if (!File.Exists(FilePath))
        {
            // Archivo nuevo: crear cabecera con columna de etiquetas
            File.WriteAllText(FilePath, "n de generacion\n");
            tabla = LeerCSV();
            AñadirColumna(tabla, columnLabel);
            GuardarCSV(tabla);
            writeToCSV = true;
            Debug.Log($"[Benchmark] CSV creado: {FilePath}");
        }
        else
        {
            tabla = LeerCSV();
            int col = ObtenerColumna(tabla, columnLabel);

            if (col == -1)
            {
                // Columna nueva en archivo existente: añadirla al final
                AñadirColumna(tabla, columnLabel);
                GuardarCSV(tabla);
                writeToCSV = true;
                Debug.Log($"[Benchmark] Columna '{columnLabel}' añadida a {FilePath}");
            }
            else
            {
                // Columna ya existe: comprobar si está completa (tiene "Media" rellena)
                bool completa = false;
                foreach (var row in tabla)
                    if (row[0] == "Media" && col < row.Length && !string.IsNullOrEmpty(row[col]))
                    { completa = true; break; }

                if (completa)
                {
                    writeToCSV = false;
                    Debug.LogWarning($"[Benchmark] Columna '{columnLabel}' ya está completa. Solo consola.");
                }
                else
                {
                    // Columna incompleta: reanudar desde la última generación registrada
                    writeToCSV = true;
                    for (int i = 1; i < tabla.Count; i++)
                    {
                        if (!int.TryParse(tabla[i][0], out _)) break;
                        if (col < tabla[i].Length && !string.IsNullOrEmpty(tabla[i][col]))
                            generationsDone++;
                    }
                    if (generationsDone > 0)
                        Debug.LogWarning($"[Benchmark] Reanudando '{columnLabel}' desde gen {generationsDone + 1}.");
                }
            }
        }
    }

    private List<string[]> LeerCSV()
    {
        var t = new List<string[]>();
        foreach (var l in File.ReadAllLines(FilePath))
            if (!string.IsNullOrWhiteSpace(l))
                t.Add(l.Split(';'));
        return t;
    }

    private int ObtenerColumna(List<string[]> t, string label)
    {
        if (t.Count == 0) return -1;
        for (int i = 0; i < t[0].Length; i++)
            if (t[0][i] == label) return i;
        return -1;
    }

    private void AñadirColumna(List<string[]> t, string label)
    {
        if (t.Count == 0) t.Add(new string[0]);
        for (int i = 0; i < t.Count; i++)
        {
            var old = t[i];
            var nueva = new string[old.Length + 1];
            for (int j = 0; j < old.Length; j++) nueva[j] = old[j];
            nueva[old.Length] = (i == 0) ? label : "";
            t[i] = nueva;
        }
    }

    /// <summary>
    /// Garantiza que exista una fila para la generación idx (etiquetada con
    /// su número), insertándola antes de las filas de resumen si es necesario.
    /// </summary>
    private void AsegurarFila(List<string[]> t, int idx)
    {
        // Si la fila ya existe en la posición esperada, no hacer nada
        if (t.Count > idx && int.TryParse(t[idx][0], out int n) && n == idx) return;

        // Buscar si existe en otra posición
        for (int i = 1; i < t.Count; i++)
            if (int.TryParse(t[i][0], out int m) && m == idx) return;

        // Crear e insertar antes de la primera fila de resumen (etiqueta no numérica)
        int insertAt = t.Count;
        for (int i = 1; i < t.Count; i++)
            if (!int.TryParse(t[i][0], out _)) { insertAt = i; break; }

        int cols = t.Count > 0 ? t[0].Length : 2;
        var f = new string[cols];
        f[0] = idx.ToString();
        for (int k = 1; k < f.Length; k++) f[k] = "";
        t.Insert(insertAt, f);
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
            for (int k = 1; k < f.Length; k++) f[k] = "";
            tabla.Add(f);
            fila = tabla.Count - 1;
        }

        // Asegurar anchura en caso de que la tabla se haya ensanchado
        if (col >= tabla[fila].Length)
        {
            var ext = new string[tabla[0].Length];
            System.Array.Copy(tabla[fila], ext, tabla[fila].Length);
            tabla[fila] = ext;
        }

        tabla[fila][col] = valor;
    }

    private void GuardarCSV(List<string[]> t)
    {
        using var sw = new StreamWriter(FilePath);
        foreach (var fila in t)
            sw.WriteLine(string.Join(";", fila));
    }
}