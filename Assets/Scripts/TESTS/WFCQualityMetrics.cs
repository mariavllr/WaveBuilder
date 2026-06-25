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
///
/// DeBroglieWFC dispara los eventos estáticos de REFACTOR
/// (WaveFunctionGame_REFACTOR.InvokeStartGeneration/InvokeEndGeneration),
/// por lo que en modo DeBroglie se escuchan los mismos eventos que en modo REFACTOR.
///
/// Produce dos CSV al finalizar cada lote de N generaciones:
///   quality_perrun.csv   → una fila por generación exitosa
///   quality_summary.csv  → una fila por (tileset, mapa, config)
///
/// Métricas:
///   1. Constraint adherence  — % de tiles fijas presentes en el output
///   2. JS divergence         — divergencia Jensen-Shannon entre la distribución
///                              objetivo (pesos) y la observada
///   3. Connectivity          — % de tiles jugables alcanzables por BFS
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
    [Tooltip("Selecciona qué solver se testea en este run. " +
             "Los otros dos deben tener generateOnStart = false para no interferir.")]
    [SerializeField] private WFCAlgorithmType algorithmType = WFCAlgorithmType.REFACTOR;

    [Header("Referencias a solvers")]
    [Tooltip("Se busca automáticamente en el mismo GameObject. No hace falta asignarlo.")]
    [SerializeField] private WaveFunctionGame_REFACTOR refactorWFC;
    [SerializeField] private GuminWFC guminWFC;
    [SerializeField] private DeBroglieWFC deBroglieWFC;

    [Header("Experimento")]
    [Tooltip("Nombre del tileset activo (nature / desert / farm …)")]
    [SerializeField] private string tilesetName = "nature";

    [Tooltip("Etiqueta de la configuración activa (gumin_prob / mi_wfc_prob / mi_wfc_full / debroglie_prob / debroglie_full …)")]
    [SerializeField] private string configLabel = "mi_wfc_full";

    [Tooltip("Número de generaciones exitosas por lote (debe coincidir con CalculateExecutionTime.numberOfGenerations)")]
    [SerializeField] private int generationsPerBatch = 50;

    [Header("Archivos de salida")]
    [SerializeField] private string perRunFileName = "quality_perrun";
    [SerializeField] private string summaryFileName = "quality_summary";

    // ============================================================
    // ENUM DE ALGORITMO
    // ============================================================

    public enum WFCAlgorithmType { REFACTOR, Gumin, DeBroglie }

    // ============================================================
    // ESTADO INTERNO
    // ============================================================

    private int _successCount = 0;
    private int _incompatibilityCount = 0;

    private WelfordAccumulator _accCA = new WelfordAccumulator();
    private WelfordAccumulator _accJS = new WelfordAccumulator();
    private WelfordAccumulator _accConn = new WelfordAccumulator();
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

    private int GetDimX() => algorithmType switch
    {
        WFCAlgorithmType.REFACTOR => refactorWFC.dimensionsX,
        WFCAlgorithmType.Gumin => guminWFC.dimensionsX,
        WFCAlgorithmType.DeBroglie => deBroglieWFC.dimensionsX,
        _ => 0
    };

    private int GetDimY() => algorithmType switch
    {
        WFCAlgorithmType.REFACTOR => refactorWFC.dimensionsY,
        WFCAlgorithmType.Gumin => guminWFC.dimensionsY,
        WFCAlgorithmType.DeBroglie => deBroglieWFC.dimensionsY,
        _ => 0
    };

    private int GetDimZ() => algorithmType switch
    {
        WFCAlgorithmType.REFACTOR => refactorWFC.dimensionsZ,
        WFCAlgorithmType.Gumin => guminWFC.dimensionsZ,
        WFCAlgorithmType.DeBroglie => deBroglieWFC.dimensionsZ,
        _ => 0
    };

    private Tile GetResolvedTile(int i) => algorithmType switch
    {
        WFCAlgorithmType.REFACTOR => refactorWFC.GetResolvedTile(i),
        WFCAlgorithmType.Gumin => guminWFC.GetResolvedTile(i),
        WFCAlgorithmType.DeBroglie => deBroglieWFC.GetResolvedTile(i),
        _ => null
    };

    private bool IsInfra(string tileType) => algorithmType switch
    {
        WFCAlgorithmType.REFACTOR => refactorWFC.IsInfrastructureTile(tileType),
        WFCAlgorithmType.Gumin => guminWFC.IsInfrastructureTile(tileType),
        WFCAlgorithmType.DeBroglie => deBroglieWFC.IsInfrastructureTile(tileType),
        _ => false
    };

    private Tile[] GetTileObjects() => algorithmType switch
    {
        WFCAlgorithmType.REFACTOR => refactorWFC.tileObjects,
        WFCAlgorithmType.Gumin => guminWFC.tileObjects,
        WFCAlgorithmType.DeBroglie => deBroglieWFC.tileObjects,
        _ => null
    };

    // ============================================================
    // CICLO DE VIDA UNITY
    // ============================================================

    private void Awake()
    {
        // Resolver referencias
        bool valid = false;
        switch (algorithmType)
        {
            case WFCAlgorithmType.REFACTOR:
                if (refactorWFC == null) refactorWFC = GetComponent<WaveFunctionGame_REFACTOR>();
                valid = refactorWFC != null;
                if (!valid) Debug.LogError("[Metrics] WaveFunctionGame_REFACTOR no encontrado.");
                break;
            case WFCAlgorithmType.Gumin:
                valid = guminWFC != null;
                if (!valid) Debug.LogError("[Metrics] GuminWFC no asignado en el Inspector.");
                break;
            case WFCAlgorithmType.DeBroglie:
                valid = deBroglieWFC != null;
                if (!valid) Debug.LogError("[Metrics] DeBroglieWFC no asignado en el Inspector.");
                break;
        }

        if (!valid) { active = false; return; }
        if (!active) return;

        // Comprobación defensiva: configLabel y algorithmType son dos campos
        // independientes en el Inspector y nada impide que se desincronicen
        // (p.ej. dejar algorithmType en REFACTOR mientras se escribe
        // configLabel = "debroglie" solo para anotar el CSV). Como
        // DeBroglieWFC dispara los mismos eventos estáticos que REFACTOR,
        // ese desajuste no lanza ninguna excepción: el cronómetro mide
        // bien, pero GetResolvedTile() lee del solver equivocado y todas
        // las métricas de contenido (JS, conectividad, entropía, diversidad)
        // salen a cero sin ningún aviso. Esto corta el batch antes de que
        // eso vuelva a pasar.
        if (!ValidateAlgorithmConfigConsistency())
        {
            active = false;
            return;
        }

        _mapSize = $"{GetDimX()}x{GetDimZ()}x{GetDimY()}";
        _perRunPath = Path.Combine(Application.persistentDataPath, perRunFileName + ".csv");
        _summaryPath = Path.Combine(Application.persistentDataPath, summaryFileName + ".csv");

        PrecomputeTargetDistribution();
        EnsureCSVHeaders();

        // Suscribir eventos según el algoritmo.
        // DeBroglieWFC dispara los eventos estáticos de REFACTOR.
        switch (algorithmType)
        {
            case WFCAlgorithmType.REFACTOR:
            case WFCAlgorithmType.DeBroglie:
                WaveFunctionGame_REFACTOR.onStartGeneration += OnGenerationStart;
                WaveFunctionGame_REFACTOR.onEndGeneration += OnGenerationEnd;
                WaveFunctionGame_REFACTOR.onIncompatibility += OnIncompatibility;
                break;
            case WFCAlgorithmType.Gumin:
                GuminWFC.onStartGeneration += OnGenerationStart;
                GuminWFC.onEndGeneration += OnGenerationEnd;
                GuminWFC.onIncompatibility += OnIncompatibility;
                break;
        }

        Debug.Log($"[Metrics] Activo | Algoritmo: {algorithmType} | Config: {configLabel} | " +
                  $"Mapa: {_mapSize} | PerRun: {_perRunPath}");
    }

    private void OnDestroy()
    {
        switch (algorithmType)
        {
            case WFCAlgorithmType.REFACTOR:
            case WFCAlgorithmType.DeBroglie:
                WaveFunctionGame_REFACTOR.onStartGeneration -= OnGenerationStart;
                WaveFunctionGame_REFACTOR.onEndGeneration -= OnGenerationEnd;
                WaveFunctionGame_REFACTOR.onIncompatibility -= OnIncompatibility;
                break;
            case WFCAlgorithmType.Gumin:
                GuminWFC.onStartGeneration -= OnGenerationStart;
                GuminWFC.onEndGeneration -= OnGenerationEnd;
                GuminWFC.onIncompatibility -= OnIncompatibility;
                break;
        }
    }

    // ============================================================
    // VALIDACIÓN DEFENSIVA: configLabel vs algorithmType
    // ============================================================

    /// <summary>
    /// Comprueba que configLabel menciona el algoritmo que algorithmType
    /// dice estar midiendo. No es una validación semántica completa, solo
    /// una red de seguridad contra el error más probable: cambiar uno de
    /// los dos campos en el Inspector y olvidar el otro. Si configLabel
    /// usa una convención de nombres distinta a "gumin" / "debroglie" /
    /// "mi_wfc" / "refactor", ajusta las cadenas de abajo en consecuencia.
    /// </summary>
    private bool ValidateAlgorithmConfigConsistency()
    {
        string label = (configLabel ?? "").ToLowerInvariant();
        bool mentionsGumin = label.Contains("gumin");
        bool mentionsDebroglie = label.Contains("debroglie");
        bool mentionsMiWfc = label.Contains("mi_wfc") || label.Contains("refactor");

        bool mismatch =
            (algorithmType == WFCAlgorithmType.REFACTOR && (mentionsGumin || mentionsDebroglie)) ||
            (algorithmType == WFCAlgorithmType.Gumin && !mentionsGumin) ||
            (algorithmType == WFCAlgorithmType.DeBroglie && !mentionsDebroglie);

        if (mismatch)
        {
            Debug.LogError(
                $"[Metrics] configLabel ('{configLabel}') no coincide con algorithmType " +
                $"('{algorithmType}'). Revisa el Inspector antes de lanzar el batch: si no " +
                "coinciden, vas a registrar el mapa del solver equivocado sin que salte " +
                "ninguna excepción (DeBroglieWFC y REFACTOR comparten los mismos eventos " +
                "estáticos, así que el cronómetro seguiría funcionando con normalidad).");
        }

        return !mismatch;
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

        _stopwatch.Stop();
        _lastTime = _stopwatch.Elapsed.TotalSeconds;
        _accTime.Add((float)_lastTime);

        _successCount++;

        Tile[] tiles = GetTileObjects();
        int n = GetDimX() * GetDimY() * GetDimZ();
        int[] map = new int[n];

        for (int i = 0; i < n; i++)
        {
            Tile t = GetResolvedTile(i);
            map[i] = (t != null && !IsInfra(t.tileType))
                ? Array.IndexOf(tiles, t)
                : -1;
        }
        _storedMaps.Add(map);

        float ca = MeasureConstraintAdherence(map, tiles);
        float js = MeasureJSDivergence(map, tiles);
        float conn = MeasureConnectivity(map);
        (float entM, float entV) = MeasureStructuralRegularity(map, tiles);

        _accCA.Add(ca);
        _accJS.Add(js);
        _accConn.Add(conn);
        _accEntM.Add(entM);
        _accEntV.Add(entV);

        AppendPerRunRow(_successCount, (float)_lastTime, ca, js, conn, entM, entV);

        if (_successCount >= generationsPerBatch)
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
    // MÉTRICA 3: CONNECTIVITY (BFS en capa y=1)
    // ============================================================

    private float MeasureConnectivity(int[] map)
    {
        int nx = GetDimX();
        int nz = GetDimZ();
        int playableY = 1;

        var playable = new HashSet<int>();
        for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                int idx = x + z * nx + playableY * nx * nz;
                if (idx < map.Length && map[idx] >= 0)
                    playable.Add(idx);
            }

        if (playable.Count == 0) return 0f;

        int startIdx = playable.First();
        var visited = new HashSet<int> { startIdx };
        var queue = new Queue<int>();
        queue.Enqueue(startIdx);

        int[] dxArr = { 1, -1, 0, 0 };
        int[] dzArr = { 0, 0, 1, -1 };

        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int x = cur % nx;
            int z = (cur / nx) % nz;

            for (int d = 0; d < 4; d++)
            {
                int nx2 = x + dxArr[d];
                int nz2 = z + dzArr[d];

                if (nx2 < 0 || nx2 >= nx || nz2 < 0 || nz2 >= nz) continue;
                int ni = nx2 + nz2 * nx + playableY * nx * nz;

                if (playable.Contains(ni) && !visited.Contains(ni))
                {
                    visited.Add(ni);
                    queue.Enqueue(ni);
                }
            }
        }

        return (float)visited.Count / playable.Count;
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
            if (IsInfra(t.tileType)) continue;

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
            _accConn.Mean, _accConn.PopStd,
            _accEntM.Mean, _accEntM.PopStd,
            _accEntV.Mean, _accEntV.PopStd,
            divMean, divStd
        );

        Debug.Log($"[Metrics] Lote completado. SuccessRate={successRate:F3} | " +
                  $"JS={_accJS.Mean:F4} | Conn={_accConn.Mean:F3} | Div={divMean:F3}");

        _successCount = 0;
        _incompatibilityCount = 0;
        _storedMaps.Clear();
        _accTime.Reset();
        _accCA.Reset(); _accJS.Reset(); _accConn.Reset();
        _accEntM.Reset(); _accEntV.Reset();
    }

    // ============================================================
    // CSV
    // ============================================================

    private void EnsureCSVHeaders()
    {
        if (!File.Exists(_perRunPath))
            File.WriteAllText(_perRunPath,
                "run_id;tileset;map_size;config;time;" +
                "constraint_adherence;js_divergence;connectivity_pct;" +
                "entropy_mean;entropy_variance\n");

        if (!File.Exists(_summaryPath))
            File.WriteAllText(_summaryPath,
                "tileset;map_size;config;n_runs;success_rate;" +
                "mean_time;std_time;" +
                "mean_ca;std_ca;mean_js;std_js;" +
                "mean_conn;std_conn;" +
                "mean_ent_mean;std_ent_mean;mean_ent_var;std_ent_var;" +
                "mean_diversity;std_diversity\n");
    }

    private void AppendPerRunRow(int runId, float time,
        float ca, float js, float conn, float entM, float entV)
    {
        string row = string.Join(";",
            runId, tilesetName, _mapSize, configLabel,
            time.ToString("F4"),
            ca.ToString("F4"),
            js.ToString("F6"),
            conn.ToString("F4"),
            entM.ToString("F4"),
            entV.ToString("F6")
        );
        File.AppendAllText(_perRunPath, row + "\n");
    }

    private void AppendSummaryRow(
        float successRate,
        float meanTime, float stdTime,
        float meanCA, float stdCA,
        float meanJS, float stdJS,
        float meanConn, float stdConn,
        float meanEntM, float stdEntM,
        float meanEntV, float stdEntV,
        float meanDiv, float stdDiv)
    {
        string row = string.Join(";",
            tilesetName, _mapSize, configLabel,
            generationsPerBatch,
            successRate.ToString("F4"),
            meanTime.ToString("F4"), stdTime.ToString("F4"),
            meanCA.ToString("F4"), stdCA.ToString("F4"),
            meanJS.ToString("F6"), stdJS.ToString("F6"),
            meanConn.ToString("F4"), stdConn.ToString("F4"),
            meanEntM.ToString("F4"), stdEntM.ToString("F4"),
            meanEntV.ToString("F6"), stdEntV.ToString("F6"),
            meanDiv.ToString("F4"), stdDiv.ToString("F4")
        );
        File.AppendAllText(_summaryPath, row + "\n");
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