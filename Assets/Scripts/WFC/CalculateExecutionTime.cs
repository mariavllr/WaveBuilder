using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Mide el tiempo de generación de mapas WFC y lo guarda en un CSV.
/// Funciona con WaveFunctionGame_REFACTOR (modo normal) y con GuminWFC
/// (cuando executeGuminAlgorithm = true en REFACTOR).
///
/// Suscripciones:
///   WaveFunctionGame_REFACTOR.onStartGeneration  → StartStopwatch
///   WaveFunctionGame_REFACTOR.onEndGeneration    → StopStopwatch
///   WaveFunctionGame_REFACTOR.onIncompatibility  → OnIncompatibility
///   GuminWFC.onStartGeneration                   → StartStopwatch
///   GuminWFC.onEndGeneration                     → StopStopwatch
///   GuminWFC.onIncompatibility                   → OnIncompatibility
///
/// En modo GuminWFC, StopStopwatch() llama a guminAlgorithm.Generate()
/// en lugar de wfc.Regenerate() para lanzar la siguiente generación.
/// </summary>
public class CalculateExecutionTime : MonoBehaviour
{
    private bool active;

    [Header("Archivo")]
    public string nombreArchivo = "Nombre_Archivo";
    public int numberOfGenerations;

    WaveFunctionGame_REFACTOR wfc;
    Stopwatch stopwatch;

    private bool incompatibility = false;
    int inc_counter = 0;
    int totalIncompatibilities = 0;
    int regenerations_counter = 0;

    double stopwatchSum = 0f;
    double maxTime = 0f;
    double minTime = 0f;

    // Diferir el arranque de la siguiente generación al próximo Update() para
    // evitar que StopStopwatch → Regenerate → RunGenerationSync → StopStopwatch
    // se encadenen síncronamente y acumulen pila con cada mapa generado.
    private bool pendingNextGeneration = false;

    private string FilePath => Path.Combine(Application.persistentDataPath, nombreArchivo + ".csv");

    List<string[]> tabla = new List<string[]>();
    string mapSize;

    void Awake()
    {
        wfc = GetComponent<WaveFunctionGame_REFACTOR>();
        stopwatch = new Stopwatch();

        if (wfc.executeGuminAlgorithm)
        {
            // Modo Gumin: solo escuchar los eventos de GuminWFC.
            // El flag active lo decide GuminWFC.STOPWATCH, no REFACTOR.STOPWATCH.
            GuminWFC.onIncompatibility += OnIncompatibility;
            GuminWFC.onStartGeneration += StartStopwatch;
            GuminWFC.onEndGeneration += StopStopwatch;
            active = wfc.guminAlgorithm != null && wfc.guminAlgorithm.STOPWATCH;
        }
        else
        {
            // Modo REFACTOR: solo escuchar los eventos de REFACTOR.
            // El flag active lo decide REFACTOR.STOPWATCH.
            WaveFunctionGame_REFACTOR.onIncompatibility += OnIncompatibility;
            WaveFunctionGame_REFACTOR.onStartGeneration += StartStopwatch;
            WaveFunctionGame_REFACTOR.onEndGeneration += StopStopwatch;
            active = wfc.STOPWATCH;
        }
    }

    void OnDestroy()
    {
        // Desuscribirse de ambos conjuntos por seguridad (no hay coste si no estaban suscritos).
        WaveFunctionGame_REFACTOR.onIncompatibility -= OnIncompatibility;
        WaveFunctionGame_REFACTOR.onStartGeneration -= StartStopwatch;
        WaveFunctionGame_REFACTOR.onEndGeneration -= StopStopwatch;

        GuminWFC.onIncompatibility -= OnIncompatibility;
        GuminWFC.onStartGeneration -= StartStopwatch;
        GuminWFC.onEndGeneration -= StopStopwatch;
    }

    /// <summary>
    /// Lanza la siguiente generación en el frame siguiente al que StopStopwatch
    /// la programó. Esto rompe la cadena síncrona:
    ///   StopStopwatch → Regenerate → RunGenerationSync → StopStopwatch → …
    /// que de otro modo acumularía un nivel de pila por cada mapa generado.
    /// El cronómetro ya está parado en este punto, así que el frame de espera
    /// no contamina ninguna medición.
    /// </summary>
    void Update()
    {
        if (!pendingNextGeneration) return;
        pendingNextGeneration = false;

        if (wfc.executeGuminAlgorithm)
            wfc.guminAlgorithm.Generate();
        else
            wfc.Regenerate();
    }

    void Start()
    {
        if (active)
        {
            Debug.Log("PATH: " + FilePath);
            mapSize = $"{wfc.dimensionsX}x{wfc.dimensionsZ}x{wfc.dimensionsY}";

            if (!File.Exists(FilePath))
            {
                CreateNewFile();
                tabla = LeerCSV();
            }
            else
            {
                tabla = LeerCSV();
                if (ObtenerColumnaMapa(tabla, mapSize) != -1)
                {
                    Debug.LogError($"Ya existe una generación para el mapa {mapSize}. No se sobreescribirá.");
                    return;
                }
            }

            AñadirColumna(tabla, mapSize);
            GuardarCSV(tabla);
        }
    }

    // ------------------- CRONÓMETRO -------------------

    /// <summary>
    /// Arranca el cronómetro al inicio de cada generación.
    /// Se llama con onStartGeneration tanto de REFACTOR como de GuminWFC.
    ///
    /// COMPORTAMIENTO EN REINTENTOS:
    ///   REFACTOR: cada reintento dispara onStartGeneration de nuevo, pero
    ///   incompatibility=true impide el reset → el timer acumula tiempo total.
    ///   GuminWFC: onStartGeneration solo se dispara UNA VEZ (antes del bucle
    ///   interno de reintentos), por lo que el timer ya acumula naturalmente.
    /// </summary>
    public void StartStopwatch()
    {
        if (!incompatibility && active)
        {
            stopwatch.Reset();
            stopwatch.Start();
        }
    }

    /// <summary>
    /// Para el cronómetro, registra el tiempo y lanza la siguiente generación.
    /// Se llama con onEndGeneration tanto de REFACTOR como de GuminWFC.
    /// </summary>
    public void StopStopwatch()
    {
        if (!active) return;

        stopwatch.Stop();
        incompatibility = false;

        double tiempo = stopwatch.Elapsed.TotalSeconds;

        print($"Generation time: {tiempo} seconds. Number of incompatibilities: {inc_counter}");

        stopwatchSum += tiempo;
        if (tiempo > maxTime) maxTime = tiempo;
        if (tiempo < minTime || minTime == 0) minTime = tiempo;

        regenerations_counter++;
        totalIncompatibilities += inc_counter;
        inc_counter = 0;

        Debug.Log("GENERATION NUMBER " + regenerations_counter + " completed!");

        if (tabla == null || tabla.Count == 0)
            tabla = LeerCSV();

        int columna = ObtenerColumnaMapa(tabla, mapSize);
        int filaGen = regenerations_counter;
        AsegurarFilaGeneracion(tabla, filaGen);

        tabla[filaGen][columna] = tiempo.ToString("F4");
        GuardarCSV(tabla);

        if (regenerations_counter == numberOfGenerations)
        {
            float avgIncompatibilities = (float)totalIncompatibilities / regenerations_counter;
            int totalAttempts = totalIncompatibilities + regenerations_counter;
            float failRate = (float)totalIncompatibilities / totalAttempts * 100f;

            Debug.Log($"END {regenerations_counter} GENERATIONS");
            Debug.Log($"FAIL RATE: {failRate}%");
            Debug.Log($"AVG FAILS / GEN: {avgIncompatibilities}");
            Debug.Log($"AVG TIME: {stopwatchSum / regenerations_counter} s | MAX: {maxTime} | MIN: {minTime}");

            // --- Total Incompatibilities ---
            int filaInc = -1;
            for (int i = 0; i < tabla.Count; i++)
                if (tabla[i][0] == "Total Incompatibilities") filaInc = i;
            if (filaInc == -1)
            {
                string[] fila = new string[tabla[0].Length];
                fila[0] = "Total Incompatibilities";
                tabla.Add(fila);
                filaInc = tabla.Count - 1;
            }
            tabla[filaInc][columna] = totalIncompatibilities.ToString();

            // --- Total Attempts ---
            int filaAttempts = -1;
            for (int i = 0; i < tabla.Count; i++)
                if (tabla[i][0] == "Total Attempts") filaAttempts = i;
            if (filaAttempts == -1)
            {
                string[] fila = new string[tabla[0].Length];
                fila[0] = "Total Attempts";
                tabla.Add(fila);
                filaAttempts = tabla.Count - 1;
            }
            tabla[filaAttempts][columna] = totalAttempts.ToString();

            // --- Fail Rate ---
            int filaRate = -1;
            for (int i = 0; i < tabla.Count; i++)
                if (tabla[i][0] == "Fail Rate") filaRate = i;
            if (filaRate == -1)
            {
                string[] fila = new string[tabla[0].Length];
                fila[0] = "Fail Rate";
                tabla.Add(fila);
                filaRate = tabla.Count - 1;
            }
            tabla[filaRate][columna] = failRate.ToString("F2") + " %";

            GuardarCSV(tabla);

            stopwatchSum = 0;
            regenerations_counter = 0;
            totalIncompatibilities = 0;
        }
        else
        {
            // Programar la siguiente generación para el próximo Update().
            // Llamar a Regenerate()/Generate() aquí de forma síncrona causaría
            // que toda la cadena de generación ocurriera dentro de esta llamada,
            // apilando un frame de pila por cada mapa generado.
            pendingNextGeneration = true;
        }
    }

    /// <summary>
    /// Recibe notificaciones de incompatibilidad de ambos algoritmos.
    /// Incrementa el contador y bloquea el reset del timer hasta que
    /// StopStopwatch() lo limpie.
    /// </summary>
    public void OnIncompatibility()
    {
        incompatibility = true;
        inc_counter++;
    }

    // ------------------- CSV -------------------

    void CreateNewFile()
    {
        using (StreamWriter sw = new StreamWriter(FilePath))
        {
            sw.WriteLine("");
            sw.WriteLine("Gen 1");
        }
    }

    List<string[]> LeerCSV()
    {
        List<string[]> t = new List<string[]>();
        foreach (string linea in File.ReadAllLines(FilePath))
            t.Add(linea.Split(';'));
        return t;
    }

    int ObtenerColumnaMapa(List<string[]> t, string mapSize)
    {
        for (int i = 1; i < t[0].Length; i++)
            if (t[0][i] == mapSize) return i;
        return -1;
    }

    int ObtenerSiguienteFila(List<string[]> t)
    {
        for (int i = 1; i < t.Count; i++)
        {
            bool vacia = true;
            for (int j = 1; j < t[i].Length; j++)
                if (!string.IsNullOrEmpty(t[i][j])) { vacia = false; break; }
            if (vacia) return i;
        }
        return t.Count;
    }

    void AñadirColumna(List<string[]> t, string mapSize)
    {
        for (int i = 0; i < t.Count; i++)
        {
            string[] oldRow = t[i];
            string[] newRow = new string[oldRow.Length + 1];
            for (int j = 0; j < oldRow.Length; j++) newRow[j] = oldRow[j];
            newRow[newRow.Length - 1] = (i == 0) ? mapSize : "";
            t[i] = newRow;
        }
    }

    void AsegurarFilaGeneracion(List<string[]> tabla, int gen)
    {
        while (tabla.Count <= gen)
        {
            string[] fila = new string[tabla[0].Length];
            fila[0] = $"Gen {tabla.Count}";
            tabla.Add(fila);
        }
    }

    int ObtenerFilaIncompatibilidades(List<string[]> tabla)
    {
        for (int i = 0; i < tabla.Count; i++)
            if (tabla[i][0] == "Incompatibilities") return i;
        return -1;
    }

    int AsegurarFilaIncompatibilidades(List<string[]> tabla)
    {
        int fila = ObtenerFilaIncompatibilidades(tabla);
        if (fila != -1) return fila;
        string[] nuevaFila = new string[tabla[0].Length];
        nuevaFila[0] = "Incompatibilities";
        tabla.Add(nuevaFila);
        return tabla.Count - 1;
    }

    void GuardarCSV(List<string[]> t)
    {
        using (StreamWriter sw = new StreamWriter(FilePath))
            foreach (var fila in t) sw.WriteLine(string.Join(";", fila));
    }
}