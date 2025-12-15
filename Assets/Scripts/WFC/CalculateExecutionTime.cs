using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class CalculateExecutionTime : MonoBehaviour
{
    [Header("Archivo")]
    public string nombreArchivo = "Nombre_Archivo";

    WaveFunctionGame wfc;
    Stopwatch stopwatch;

    private bool incompatibility = false;
    int inc_counter = 0;
    int totalIncompatibilities = 0;
    int regenerations_counter = 0;

    double stopwatchSum = 0f;
    double maxTime = 0f;
    double minTime = 0f;

    private string FilePath => Path.Combine(Application.persistentDataPath, nombreArchivo + ".csv");

    List<string[]> tabla = new List<string[]>();
    string mapSize;

    void Awake()
    {
        wfc = GetComponent<WaveFunctionGame>();
        stopwatch = new Stopwatch();

        WaveFunctionGame.onIncompatibility += OnIncompatibility;
        WaveFunctionGame.onStartGeneration += StartStopwatch;
        WaveFunctionGame.onEndGeneration += StopStopwatch;
    }

    void Start()
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

    // ------------------- CRONÓMETRO -------------------

    public void StartStopwatch()
    {
        if (!incompatibility)
        {
            stopwatch.Reset();
            stopwatch.Start();
        }
    }

    public void StopStopwatch()
    {
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

        if (regenerations_counter == 50)
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

            if (filaInc == -1) // si no existe, crearla
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


            // Guardar CSV
            GuardarCSV(tabla);


            stopwatchSum = 0;
            regenerations_counter = 0;
            totalIncompatibilities = 0;
        }
        else
        {
            wfc.Regenerate();
        }
    }

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
            sw.WriteLine("");       // esquina superior izquierda vacía
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
        {
            if (t[0][i] == mapSize)
                return i;
        }
        return -1;
    }

    int ObtenerSiguienteFila(List<string[]> t)
    {
        for (int i = 1; i < t.Count; i++)
        {
            bool vacia = true;
            for (int j = 1; j < t[i].Length; j++)
            {
                if (!string.IsNullOrEmpty(t[i][j]))
                {
                    vacia = false;
                    break;
                }
            }
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

            for (int j = 0; j < oldRow.Length; j++)
                newRow[j] = oldRow[j];

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
        {
            if (tabla[i][0] == "Incompatibilities")
                return i;
        }
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
        {
            foreach (var fila in t)
                sw.WriteLine(string.Join(";", fila));
        }
    }
}
