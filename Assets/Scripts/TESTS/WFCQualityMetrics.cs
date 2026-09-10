using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Recolector de métricas de calidad para el artículo científico.
/// Soporta los tres algoritmos: REFACTOR, GuminWFC y DeBroglieWFC.
/// Se suscribe a los eventos del algoritmo seleccionado en el Inspector.
/// Gumin y DeBroglie exponen sus propios eventos de instancia (contrato
/// IWFCGenerator: OnStart/OnEnd/OnIncompatibility); REFACTOR (motor del juego)
/// conserva sus eventos estáticos.
///
/// Produce dos CSV al finalizar cada lote de N generaciones:
///   quality_perrun.csv   → una fila por generación exitosa
///   quality_summary.csv  → una fila por (tileset, mapa, config)
///
/// Métricas:
///   1. Constraint adherence  — % de tiles fijas presentes en el output
///   2. JS divergence         — divergencia Jensen-Shannon entre la distribución
///                              objetivo (pesos) y la observada
///   (Conectividad por BFS retirada: presupone una semántica de transitabilidad
///    —celdas caminables frente a obstáculos— que los tilesets de naturaleza no
///    poseen, por lo que no es significativa y no se reporta para ningún algoritmo.)
///   4. Entropy mean/var      — distribución de entropía de Shannon por cuadrante
///   5. Diversity             — distancia Hamming normalizada entre pares de mapas
/// </summary>
public class WFCQualityMetrics : MonoBehaviour
{
    // ============================================================
    // CONFIGURACIÓN DEL INSPECTOR
    // ============================================================

    [Header("Activación")]
    [SerializeField] private bool active = false;

    [Header("Algoritmo a medir")]
    [Tooltip("Selecciona qué solver se mide. DEBE coincidir con el 'Algorithm' de " +
             "CalculateExecutionTime, que es quien dispara las generaciones que este " +
             "arnés mide de forma pasiva.")]
    [SerializeField] private WFCAlgorithmType algorithmType = WFCAlgorithmType.MyWFC;

    [Header("Referencias a solvers")]
    [SerializeField] private MyWFC myWFC;
    [SerializeField] private GuminWFC guminWFC;
    [SerializeField] private DeBroglieWFC deBroglieWFC;

    [Header("Experimento")]
    [Tooltip("Nombre del tileset activo (nature / desert / farm …)")]
    [SerializeField] private string tilesetName = "nature";

    // La etiqueta viaja con el solver (AlgorithmLabel), igual que en
    // CalculateExecutionTime. Se fija en Awake desde selected.AlgorithmLabel.
    private string configLabel;

    [Tooltip("Número de generaciones exitosas por lote (debe coincidir con CalculateExecutionTime.numberOfGenerations)")]
    [SerializeField] private int generationsPerBatch = 50;

    [Header("Archivos de salida")]
    [SerializeField] private string perRunFileName = "quality_perrun";
    [SerializeField] private string summaryFileName = "quality_summary";

    [Tooltip("WriteIfNew  → escribe solo si (tileset, mapa, config) no está ya en el CSV (modo seguro).\n" +
             "Overwrite   → reemplaza los datos previos de esa combinación, conservando el resto.\n" +
             "ConsoleOnly → no escribe ningún fichero; solo muestra por consola (pruebas).")]
    [SerializeField] private WriteMode writeMode = WriteMode.WriteIfNew;

    // ============================================================
    // ENUM DE ALGORITMO
    // ============================================================

    public enum WFCAlgorithmType { MyWFC, Gumin, DeBroglie }

    // ============================================================
    // ESTADO INTERNO
    // ============================================================

    private IWFCGenerator selected;   // solver activo (resuelto en Awake desde algorithmType)

    private int _successCount = 0;
    private int _incompatibilityCount = 0;
    private int _warmupSeen = 0;   // generaciones vistas en el batch actual (para descartar la 1ª)

    private WelfordAccumulator _accCA = new WelfordAccumulator();
    private WelfordAccumulator _accJS = new WelfordAccumulator();
    private WelfordAccumulator _accEntM = new WelfordAccumulator();
    private WelfordAccumulator _accEntV = new WelfordAccumulator();

    private Stopwatch _stopwatch = new Stopwatch();
    private double _lastTime = 0.0;
    private WelfordAccumulator _accTime = new WelfordAccumulator();

    private List<int[]> _storedMaps = new List<int[]>();
    private Dictionary<string, float> _targetDist = new Dictionary<string, float>();

    private string _perRunPath;
    private string _summaryPath;
    private string _mapSize;

    // ============================================================
    // ABSTRACCIÓN DE DATOS — delegaciones al solver activo
    // ============================================================

    // Delegación uniforme al solver seleccionado (contrato IWFCGenerator).
    private int GetDimX() => selected.DimensionsX;
    private int GetDimY() => selected.DimensionsY;
    private int GetDimZ() => selected.DimensionsZ;
    private Tile GetResolvedTile(int i) => selected.GetResolvedTile(i);
    private bool IsInfra(Tile tile) => selected.IsInfrastructureTile(tile);
    private Tile[] GetTileObjects() => selected.TileObjects;

    // ============================================================
    // CICLO DE VIDA UNITY
    // ============================================================

    private void Awake()
    {
        selected = ResolveSelected();
        if (selected == null)
        {
            Debug.LogError($"[Metrics] El solver '{algorithmType}' no está asignado en el Inspector.");
            active = false;
            return;
        }
        if (!active) return;

        // La etiqueta viaja con el solver (AlgorithmLabel), igual que en
        // CalculateExecutionTime: no puede desincronizarse. Recuerda que
        // algorithmType debe coincidir con el Algorithm de CalculateExecutionTime,
        // que es quien dispara las generaciones que este arnés mide.
        configLabel = selected.AlgorithmLabel;

        _mapSize = $"{GetDimX()}x{GetDimZ()}x{GetDimY()}";
        _perRunPath = Path.Combine(Application.persistentDataPath, perRunFileName + ".csv");
        _summaryPath = Path.Combine(Application.persistentDataPath, summaryFileName + ".csv");

        PrecomputeTargetDistribution();
        EnsureCSVHeaders();

        // En modo Overwrite hay que eliminar las filas per-run previas de este
        // mismo experimento: AppendPerRunRow siempre añade al final, así que
        // sin esta purga quedarían duplicadas junto a las nuevas.
        if (writeMode == WriteMode.Overwrite) PurgePerRunForThisExperiment();

        // Suscripción uniforme a los eventos de instancia del solver seleccionado.
        selected.OnStart += OnGenerationStart;
        selected.OnEnd += OnGenerationEnd;
        selected.OnIncompatibility += OnIncompatibility;

        Debug.Log($"[Metrics] Activo | Algoritmo: {algorithmType} | Config: {configLabel} | " +
                  $"Mapa: {_mapSize} | PerRun: {_perRunPath}");
    }

    private void OnDestroy()
    {
        if (selected == null) return;
        selected.OnStart -= OnGenerationStart;
        selected.OnEnd -= OnGenerationEnd;
        selected.OnIncompatibility -= OnIncompatibility;
    }

    private IWFCGenerator ResolveSelected()
    {
        switch (algorithmType)
        {
            case WFCAlgorithmType.MyWFC:     return myWFC;
            case WFCAlgorithmType.Gumin:     return guminWFC;
            case WFCAlgorithmType.DeBroglie: return deBroglieWFC;
            default:                         return null;
        }
    }

    // ============================================================
    // MANEJADORES DE EVENTOS
    // ============================================================

    private void OnGenerationStart()
    {
        if (!active) return;
        _stopwatch.Restart();
    }

    private void OnIncompatibility()
    {
        if (!active) return;
        _incompatibilityCount++;
    }

    private void OnGenerationEnd()
    {
        if (!active) return;

        Tile[] tiles = GetTileObjects();
        int n = GetDimX() * GetDimY() * GetDimZ();
        int[] map = new int[n];

        int nonEmpty = 0;
        for (int i = 0; i < n; i++)
        {
            Tile t = GetResolvedTile(i);
            map[i] = (t != null && !IsInfra(t))
                ? Array.IndexOf(tiles, t)
                : -1;
            if (map[i] >= 0) nonEmpty++;
        }

        // Guarda defensiva: si el mapa no tiene NINGUNA tile jugable, no es una
        // generación real (evento recibido antes de que el solver resuelva nada);
        // se descarta sin contabilizar. Con los eventos de instancia esto ya no
        // debería ocurrir, pero se mantiene como red de seguridad.
        if (nonEmpty == 0)
        {
            Debug.LogWarning("[Metrics] OnEnd recibido con mapa vacío (0 tiles jugables). " +
                "Descartado.");
            return;
        }

        // Descarte de warm-up. La primera generación de cada batch incluye coste
        // de calentamiento (JIT, asignaciones iniciales, cachés frías) que no
        // refleja el rendimiento estable del algoritmo. Se descarta por completo
        // (ni tiempo ni calidad) para mantener un n idéntico entre todas las
        // métricas. El batch efectivo pasa a ser de (generationsPerBatch - 1)
        // generaciones medidas.
        _warmupSeen++;
        _stopwatch.Stop();
        if (_warmupSeen <= 1)
        {
            Debug.Log("[Metrics] Generación de warm-up descartada (no se contabiliza " +
                      "en tiempo ni en calidad).");
            return;
        }

        _lastTime = _stopwatch.Elapsed.TotalSeconds;
        _accTime.Add((float)_lastTime);

        _successCount++;

        _storedMaps.Add(map);

        float ca = MeasureConstraintAdherence(map, tiles);
        float js = MeasureJSDivergence(map, tiles);
        (float entM, float entV) = MeasureStructuralRegularity(map, tiles);

        _accCA.Add(ca);
        _accJS.Add(js);
        _accEntM.Add(entM);
        _accEntV.Add(entV);

        AppendPerRunRow(_successCount, (float)_lastTime, ca, js, entM, entV);

        if (_successCount >= generationsPerBatch - 1)
            FlushBatch();
    }

    // ============================================================
    // MÉTRICA 1: CONSTRAINT ADHERENCE
    // ============================================================

    private float MeasureConstraintAdherence(int[] map, Tile[] tiles)
    {
        int required = 0;
        int satisfied = 0;

        foreach (Tile proto in tiles)
        {
            if (proto.fixedTile <= 0) continue;

            int found = 0;
            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] < 0) continue;
                if (tiles[map[i]].tileType == proto.tileType) found++;
            }

            required += proto.fixedTile;
            satisfied += Math.Min(found, proto.fixedTile);
        }

        return required > 0 ? (float)satisfied / required : 1f;
    }

    // ============================================================
    // MÉTRICA 2: JS DIVERGENCE
    // ============================================================

    private float MeasureJSDivergence(int[] map, Tile[] tiles)
    {
        var counts = new Dictionary<string, int>();
        int total = 0;

        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] < 0) continue;
            string type = tiles[map[i]].tileType;
            counts.TryGetValue(type, out int c);
            counts[type] = c + 1;
            total++;
        }

        if (total == 0) return 0f;

        var Q = new Dictionary<string, float>(counts.Count);
        foreach (var kvp in counts)
            Q[kvp.Key] = (float)kvp.Value / total;

        var allTypes = new HashSet<string>(_targetDist.Keys);
        foreach (string k in Q.Keys) allTypes.Add(k);

        double js = 0.0;
        foreach (string k in allTypes)
        {
            float p = _targetDist.TryGetValue(k, out float pv) ? pv : 0f;
            float q = Q.TryGetValue(k, out float qv) ? qv : 0f;
            float m = (p + q) * 0.5f;

            if (m <= 0f) continue;
            if (p > 0f) js += p * Math.Log(p / m);
            if (q > 0f) js += q * Math.Log(q / m);
        }

        return (float)(js * 0.5);
    }

    // ============================================================
    // MÉTRICA 4: STRUCTURAL REGULARITY (ENTROPY POR CUADRANTE)
    // ============================================================

    private (float mean, float variance) MeasureStructuralRegularity(int[] map, Tile[] tiles)
    {
        int nx = GetDimX();
        int nz = GetDimZ();
        int midX = nx / 2;
        int midZ = nz / 2;

        var quadCounts = new Dictionary<string, int>[4];
        for (int q = 0; q < 4; q++)
            quadCounts[q] = new Dictionary<string, int>();

        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] < 0) continue;

            int x = i % nx;
            int z = (i / nx) % nz;
            int q = (x >= midX ? 1 : 0) | (z >= midZ ? 2 : 0);

            string type = tiles[map[i]].tileType;
            quadCounts[q].TryGetValue(type, out int c);
            quadCounts[q][type] = c + 1;
        }

        float[] entropies = new float[4];
        for (int q = 0; q < 4; q++)
        {
            int total = quadCounts[q].Values.Sum();
            if (total == 0) { entropies[q] = 0f; continue; }

            double H = 0.0;
            foreach (int count in quadCounts[q].Values)
            {
                double p = (double)count / total;
                if (p > 0.0) H -= p * Math.Log(p);
            }
            entropies[q] = (float)H;
        }

        float mean = entropies.Average();
        float variance = entropies.Select(e => (e - mean) * (e - mean)).Average();
        return (mean, variance);
    }

    // ============================================================
    // MÉTRICA 5: DIVERSITY (HAMMING DISTANCE ENTRE PARES)
    // ============================================================

    private (float mean, float std) ComputeDiversity()
    {
        int numMaps = _storedMaps.Count;
        if (numMaps < 2) return (0f, 0f);

        int n = _storedMaps[0].Length;

        int denominator = 0;
        for (int i = 0; i < n; i++)
            for (int m = 0; m < numMaps; m++)
                if (_storedMaps[m][i] >= 0) { denominator++; break; }

        if (denominator == 0) return (0f, 0f);

        var distances = new List<float>(numMaps * (numMaps - 1) / 2);

        for (int a = 0; a < numMaps; a++)
            for (int b = a + 1; b < numMaps; b++)
            {
                int diff = 0;
                for (int i = 0; i < n; i++)
                    if (_storedMaps[a][i] != _storedMaps[b][i]) diff++;
                distances.Add((float)diff / denominator);
            }

        float mean = distances.Average();
        float variance = distances.Select(d => (d - mean) * (d - mean)).Average();
        return (mean, (float)Math.Sqrt(variance));
    }

    // ============================================================
    // PRECÓMPUTO DE DISTRIBUCIÓN OBJETIVO P
    // ============================================================

    private void PrecomputeTargetDistribution()
    {
        _targetDist.Clear();
        float totalWeight = 0f;

        foreach (Tile t in GetTileObjects())
        {
            if (IsInfra(t)) continue;

            float w = Mathf.Max((float)t.probability, 1f);
            _targetDist.TryGetValue(t.tileType, out float prev);
            _targetDist[t.tileType] = prev + w;
            totalWeight += w;
        }

        if (totalWeight <= 0f) return;

        var keys = new List<string>(_targetDist.Keys);
        foreach (string k in keys)
            _targetDist[k] /= totalWeight;
    }

    // ============================================================
    // FLUSH DE LOTE
    // ============================================================

    private void FlushBatch()
    {
        int totalAttempts = _successCount + _incompatibilityCount;
        float successRate = totalAttempts > 0
            ? (float)_successCount / totalAttempts
            : 1f;

        (float divMean, float divStd) = ComputeDiversity();

        AppendSummaryRow(
            successRate,
            _accTime.Mean, _accTime.PopStd,
            _accCA.Mean, _accCA.PopStd,
            _accJS.Mean, _accJS.PopStd,
            _accEntM.Mean, _accEntM.PopStd,
            _accEntV.Mean, _accEntV.PopStd,
            divMean, divStd
        );

        Debug.Log($"[Metrics] Lote completado. SuccessRate={successRate:F3} | " +
                  $"JS={_accJS.Mean:F4} | Div={divMean:F3}");

        _successCount = 0;
        _incompatibilityCount = 0;
        _warmupSeen = 0;
        _storedMaps.Clear();
        _accTime.Reset();
        _accCA.Reset(); _accJS.Reset();
        _accEntM.Reset(); _accEntV.Reset();
    }

    // ============================================================
    // CSV
    // ============================================================

    private void EnsureCSVHeaders()
    {
        if (writeMode == WriteMode.ConsoleOnly) return;   // no crear ficheros

        if (!File.Exists(_perRunPath))
            File.WriteAllText(_perRunPath,
                "run_id;tileset;map_size;config;time;" +
                "constraint_adherence;js_divergence;" +
                "entropy_mean;entropy_variance\n");

        if (!File.Exists(_summaryPath))
            File.WriteAllText(_summaryPath,
                "tileset;map_size;config;n_runs;success_rate;" +
                "mean_time;std_time;" +
                "mean_ca;std_ca;mean_js;std_js;" +
                "mean_ent_mean;std_ent_mean;mean_ent_var;std_ent_var;" +
                "mean_diversity;std_diversity\n");
    }

    /// <summary>
    /// Elimina del per-run las filas de este experimento (tileset, mapa, config).
    /// Necesario en modo Overwrite, ya que AppendPerRunRow siempre añade al final.
    /// El per-run identifica cada fila por sus columnas tileset;map_size;config,
    /// que ocupan las posiciones 1, 2 y 3 (tras run_id).
    /// </summary>
    private void PurgePerRunForThisExperiment()
    {
        if (!File.Exists(_perRunPath)) return;

        string[] lines = File.ReadAllLines(_perRunPath);
        var kept = new List<string>();
        int removed = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            if (i == 0) { kept.Add(lines[i]); continue; }              // cabecera
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            // Formato: run_id;tileset;map_size;config;...
            string[] f = lines[i].Split(';');
            bool esteExperimento = f.Length > 3
                                   && f[1] == tilesetName
                                   && f[2] == _mapSize
                                   && f[3] == configLabel;

            if (esteExperimento) { removed++; continue; }
            kept.Add(lines[i]);
        }

        File.WriteAllLines(_perRunPath, kept);
        if (removed > 0)
            Debug.LogWarning($"[Metrics] Purgadas {removed} filas per-run previas de " +
                             $"{tilesetName}/{_mapSize}/{configLabel}.");
    }

    private void AppendPerRunRow(int runId, float time,
        float ca, float js, float entM, float entV)
    {
        string row = string.Join(";",
            runId, tilesetName, _mapSize, configLabel,
            time.ToString("F4"),
            ca.ToString("F4"),
            js.ToString("F6"),
            entM.ToString("F4"),
            entV.ToString("F6")
        );

        if (writeMode == WriteMode.ConsoleOnly)
        {
            Debug.Log($"[Metrics][consola] {row}");
            return;
        }

        File.AppendAllText(_perRunPath, row + "\n");
    }

    private void AppendSummaryRow(
        float successRate,
        float meanTime, float stdTime,
        float meanCA, float stdCA,
        float meanJS, float stdJS,
        float meanEntM, float stdEntM,
        float meanEntV, float stdEntV,
        float meanDiv, float stdDiv)
    {
        string row = string.Join(";",
            tilesetName, _mapSize, configLabel,
            _successCount,
            successRate.ToString("F4"),
            meanTime.ToString("F4"), stdTime.ToString("F4"),
            meanCA.ToString("F4"), stdCA.ToString("F4"),
            meanJS.ToString("F6"), stdJS.ToString("F6"),
            meanEntM.ToString("F4"), stdEntM.ToString("F4"),
            meanEntV.ToString("F6"), stdEntV.ToString("F6"),
            meanDiv.ToString("F4"), stdDiv.ToString("F4")
        );

        // ── ConsoleOnly: no se escribe nada ────────────────────────────────
        if (writeMode == WriteMode.ConsoleOnly)
        {
            Debug.Log($"[Metrics][consola] RESUMEN {tilesetName}/{_mapSize}/{configLabel}\n{row}");
            return;
        }

        if (!File.Exists(_summaryPath))
        {
            EnsureCSVHeaders();
            File.AppendAllText(_summaryPath, row + "\n");
            return;
        }

        // ¿Existe ya una fila para esta combinación (tileset, mapa, config)?
        string[] lines = File.ReadAllLines(_summaryPath);
        bool existe = false;
        for (int i = 1; i < lines.Length; i++)
        {
            string[] f = lines[i].Split(';');
            if (f.Length > 2 && f[0] == tilesetName && f[1] == _mapSize && f[2] == configLabel)
            { existe = true; break; }
        }

        // ── WriteIfNew: no pisar datos existentes ──────────────────────────
        if (existe && writeMode == WriteMode.WriteIfNew)
        {
            Debug.LogWarning($"[Metrics] {tilesetName}/{_mapSize}/{configLabel} ya existe en el " +
                             "summary. No se escribe (modo WriteIfNew). Usa Overwrite para repetirlo.");
            Debug.Log($"[Metrics][no escrito] {row}");
            return;
        }

        // ── Overwrite (o la fila no existía): reconstruir el fichero sin la
        //    fila previa de esta combinación y añadir la nueva al final ──────
        var kept = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i == 0) { kept.Add(lines[i]); continue; }              // cabecera
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            string[] f = lines[i].Split(';');
            bool esteExperimento = f.Length > 2
                                   && f[0] == tilesetName
                                   && f[1] == _mapSize
                                   && f[2] == configLabel;

            if (esteExperimento)
            {
                Debug.LogWarning($"[Metrics] Reemplazando resultado previo de " +
                                 $"{tilesetName}/{_mapSize}/{configLabel} en el summary.");
                continue;
            }
            kept.Add(lines[i]);
        }

        kept.Add(row);
        File.WriteAllLines(_summaryPath, kept);
    }

    // ============================================================
    // ACUMULADOR WELFORD
    // ============================================================

    private class WelfordAccumulator
    {
        private int _n = 0;
        private double _mean = 0.0;
        private double _M2 = 0.0;

        public float Mean => _n > 0 ? (float)_mean : 0f;
        public float PopVar => _n > 1 ? (float)(_M2 / _n) : 0f;
        public float PopStd => (float)Math.Sqrt(PopVar);

        public void Add(float value)
        {
            _n++;
            double delta = value - _mean;
            _mean += delta / _n;
            double delta2 = value - _mean;
            _M2 += delta * delta2;
        }

        public void Reset() { _n = 0; _mean = 0.0; _M2 = 0.0; }
    }
}