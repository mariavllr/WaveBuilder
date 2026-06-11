using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Recolector de métricas de calidad para el artículo científico.
/// Se adjunta al mismo GameObject que WaveFunctionGame_REFACTOR y
/// suscribe a sus eventos estáticos, siguiendo el mismo patrón que
/// CalculateExecutionTime.
///
/// Produce dos CSV al finalizar cada lote de N generaciones:
///   quality_perrun.csv   → una fila por generación exitosa
///   quality_summary.csv  → una fila por (tileset, mapa, config)
///
/// Métricas medidas:
///   1. Constraint adherence  — % de tiles fijas presentes en el output
///   2. JS divergence         — divergencia Jensen–Shannon entre la distribución
///                              de tiles deseada (pesos) y la observada
///   3. Connectivity          — % de tiles jugables alcanzables por BFS
///   4. Entropy mean/var      — distribución de entropía de Shannon por cuadrante
///   5. Diversity             — distancia Hamming normalizada entre pares de mapas
///
/// </summary>
public class WFCQualityMetrics : MonoBehaviour
{
    // ============================================================
    // CONFIGURACIÓN DEL INSPECTOR
    // ============================================================

    [Header("Activación")]
    [SerializeField] private bool active = false;

    [Header("Experimento")]
    [Tooltip("Nombre del tileset activo (nature / forest / urban …)")]
    [SerializeField] private string tilesetName = "nature";

    [Tooltip("Etiqueta de la configuración de restricciones activa (none / prob_only / full …)")]
    [SerializeField] private string configLabel = "full";

    [Tooltip("Número de generaciones exitosas por lote (debe coincidir con CalculateExecutionTime.numberOfGenerations)")]
    [SerializeField] private int generationsPerBatch = 50;

    [Header("Archivos de salida")]
    [SerializeField] private string perRunFileName = "quality_perrun";
    [SerializeField] private string summaryFileName = "quality_summary";

    // ============================================================
    // ESTADO INTERNO
    // ============================================================

    private WaveFunctionGame_REFACTOR _wfc;

    // Contadores de lote
    private int _successCount = 0;
    private int _incompatibilityCount = 0;

    // Acumuladores para medias/desviaciones (método de Welford)
    private WelfordAccumulator _accCA = new WelfordAccumulator(); // constraint adherence
    private WelfordAccumulator _accJS = new WelfordAccumulator(); // JS divergence
    private WelfordAccumulator _accConn = new WelfordAccumulator(); // connectivity
    private WelfordAccumulator _accEntM = new WelfordAccumulator(); // entropy mean
    private WelfordAccumulator _accEntV = new WelfordAccumulator(); // entropy variance

    // Mapas de tiles por generación (para Diversity)
    private List<int[]> _storedMaps = new List<int[]>();

    // Distribución objetivo P (calculada una vez del conjunto de tiles)
    // Agrupada por tileType (las rotaciones heredan el mismo probability)
    private Dictionary<string, float> _targetDist = new Dictionary<string, float>();

    // Rutas CSV
    private string _perRunPath;
    private string _summaryPath;
    private string _mapSize;

    // ============================================================
    // CICLO DE VIDA UNITY
    // ============================================================

    private void Awake()
    {
        _wfc = GetComponent<WaveFunctionGame_REFACTOR>();
        if (_wfc == null)
        {
            Debug.LogError("[Metrics] WaveFunctionGame_REFACTOR no encontrado en el mismo GameObject.");
            active = false;
            return;
        }

        if (!active) return;

        _mapSize = $"{_wfc.dimensionsX}x{_wfc.dimensionsZ}x{_wfc.dimensionsY}";
        _perRunPath = Path.Combine(Application.persistentDataPath, perRunFileName + ".csv");
        _summaryPath = Path.Combine(Application.persistentDataPath, summaryFileName + ".csv");

        PrecomputeTargetDistribution();
        EnsureCSVHeaders();

        WaveFunctionGame_REFACTOR.onEndGeneration += OnGenerationEnd;
        WaveFunctionGame_REFACTOR.onIncompatibility += OnIncompatibility;

        Debug.Log($"[Metrics] Activo. PerRun: {_perRunPath} | Summary: {_summaryPath}");
    }

    private void OnDestroy()
    {
        WaveFunctionGame_REFACTOR.onEndGeneration -= OnGenerationEnd;
        WaveFunctionGame_REFACTOR.onIncompatibility -= OnIncompatibility;
    }

    // ============================================================
    // MANEJADORES DE EVENTOS
    // ============================================================

    private void OnIncompatibility()
    {
        if (!active) return;
        _incompatibilityCount++;
    }

    /// <summary>
    /// Llamado en cada generación exitosa. Construye el mapa de tiles,
    /// calcula todas las métricas y escribe la fila en quality_perrun.csv.
    /// Al completar el lote, escribe la fila de resumen en quality_summary.csv.
    /// </summary>
    private void OnGenerationEnd()
    {
        if (!active) return;

        _successCount++;

        // --- Construir el mapa de tiles resueltos ---
        // Índice en tileObjects para tiles jugables, -1 para infraestructura.
        int n = _wfc.gridComponents.Count;
        int[] map = new int[n];

        for (int i = 0; i < n; i++)
        {
            Tile t = _wfc.GetResolvedTile(i);
            map[i] = (t != null && !_wfc.IsInfrastructureTile(t.tileType))
                ? Array.IndexOf(_wfc.tileObjects, t)
                : -1;
        }
        _storedMaps.Add(map);

        // --- Calcular métricas por generación ---
        float ca = MeasureConstraintAdherence(map);
        float js = MeasureJSDivergence(map);
        float conn = MeasureConnectivity(map);
        (float entM, float entV) = MeasureStructuralRegularity(map);

        // Acumular en Welford
        _accCA.Add(ca);
        _accJS.Add(js);
        _accConn.Add(conn);
        _accEntM.Add(entM);
        _accEntV.Add(entV);

        // Escribir fila individual
        AppendPerRunRow(_successCount, ca, js, conn, entM, entV);

        // --- Fin de lote ---
        if (_successCount >= generationsPerBatch)
            FlushBatch();
    }

    // ============================================================
    // MÉTRICA 1: CONSTRAINT ADHERENCE
    // ============================================================

    /// <summary>
    /// Calcula el porcentaje de tiles fijas (fixedTile > 0) que aparecen
    /// en el output. El número de tiles de cada tipo encontradas se compara
    /// con el número configurado en el inspector.
    ///
    /// Un valor de 1.0 indica que todas las tiles fijas están presentes,
    /// lo que debe verificarse incluso si la generación fue exitosa,
    /// para validar la preservación de restricciones editoriales.
    /// </summary>
    private float MeasureConstraintAdherence(int[] map)
    {
        int required = 0;
        int satisfied = 0;

        foreach (Tile proto in _wfc.tileObjects)
        {
            if (proto.fixedTile <= 0) continue;

            // Contar apariciones del tipo en el mapa
            int found = 0;
            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] < 0) continue;
                if (_wfc.tileObjects[map[i]].tileType == proto.tileType) found++;
            }

            required += proto.fixedTile;
            satisfied += Math.Min(found, proto.fixedTile);
        }

        return required > 0 ? (float)satisfied / required : 1f;
    }

    // ============================================================
    // MÉTRICA 2: JS DIVERGENCE (DISTRIBUTION MATCHING)
    // ============================================================

    /// <summary>
    /// Calcula la divergencia Jensen–Shannon entre la distribución deseada P
    /// (derivada de los pesos de probabilidad de cada tileType) y la
    /// distribución observada Q (frecuencia real de cada tileType en el mapa).
    ///
    /// JS(P||Q) = (KL(P||M) + KL(Q||M)) / 2,  M = (P+Q)/2
    ///
    /// El resultado está acotado en [0, ln(2)] ≈ [0, 0.693].
    /// Un valor cercano a 0 indica que el generador respeta los pesos.
    ///
    /// Referencia: Cover, T. M. y Thomas, J. A. (2006). Elements of Information
    /// Theory (2.ª ed.). Wiley. ISBN 978-0-471-24195-9.
    /// </summary>
    private float MeasureJSDivergence(int[] map)
    {
        // Contar frecuencias por tileType (Q observada, sin normalizar)
        var counts = new Dictionary<string, int>();
        int total = 0;

        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] < 0) continue;
            string type = _wfc.tileObjects[map[i]].tileType;
            counts.TryGetValue(type, out int c);
            counts[type] = c + 1;
            total++;
        }

        if (total == 0) return 0f;

        // Normalizar Q
        var Q = new Dictionary<string, float>(counts.Count);
        foreach (var kvp in counts)
            Q[kvp.Key] = (float)kvp.Value / total;

        // Calcular M = (P + Q) / 2 sobre la unión de tileTypes
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
    // MÉTRICA 3: CONNECTIVITY (BFS)
    // ============================================================

    /// <summary>
    /// Mide el porcentaje de tiles jugables alcanzables mediante BFS
    /// desde la primera tile no-infraestructura del mapa.
    ///
    /// La conectividad se evalúa en el plano XZ (2D) sobre la capa y=1,
    /// que es la primera capa jugable por encima del suelo sólido.
    /// Esto refleja la definición práctica de "nivel accesible" en
    /// generación de niveles tile-based (Shaker et al., 2016).
    ///
    /// Referencia: Shaker, N., Togelius, J. y Nelson, M. J. (2016).
    /// Procedural Content Generation in Games. Springer. DOI 10.1007/978-3-319-42716-4.
    /// </summary>
    private float MeasureConnectivity(int[] map)
    {
        int nx = _wfc.dimensionsX;
        int nz = _wfc.dimensionsZ;
        int ny = _wfc.dimensionsY;

        // Trabajar sobre la capa y=1 (primera capa jugable)
        int playableY = 1;

        // Recopilar celdas jugables en y=1
        var playable = new HashSet<int>();
        for (int z = 0; z < nz; z++)
        {
            for (int x = 0; x < nx; x++)
            {
                int idx = x + z * nx + playableY * nx * nz;
                if (idx < map.Length && map[idx] >= 0)
                    playable.Add(idx);
            }
        }

        if (playable.Count == 0) return 0f;

        // BFS en 4 direcciones (XZ)
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
    // MÉTRICA 4: STRUCTURAL REGULARITY (ENTROPY DISTRIBUTION)
    // ============================================================

    /// <summary>
    /// Divide el plano XZ en cuatro cuadrantes y calcula la entropía de
    /// Shannon de la distribución de tileType en cada uno. Devuelve la
    /// media y la varianza de las cuatro entropías.
    ///
    /// Una varianza baja indica distribución espacialmente uniforme;
    /// una alta indica clustering o anisotropía estructural.
    ///
    /// Metodología basada en Karth, I. y Smith, A. M. (2022).
    /// WaveFunctionCollapse: Content Generation via Constraint Solving and
    /// Machine Learning. IEEE Transactions on Games, 14(3), 364–376.
    /// DOI 10.1109/TG.2021.3076368.
    /// </summary>
    private (float mean, float variance) MeasureStructuralRegularity(int[] map)
    {
        int nx = _wfc.dimensionsX;
        int nz = _wfc.dimensionsZ;
        int midX = nx / 2;
        int midZ = nz / 2;

        // 4 cuadrantes: (x < midX, z < midZ), (x >= midX, z < midZ),
        //               (x < midX, z >= midZ), (x >= midX, z >= midZ)
        var quadCounts = new Dictionary<string, int>[4];
        for (int q = 0; q < 4; q++)
            quadCounts[q] = new Dictionary<string, int>();

        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] < 0) continue;

            int x = i % nx;
            int z = (i / nx) % nz;
            int q = (x >= midX ? 1 : 0) | (z >= midZ ? 2 : 0);

            string type = _wfc.tileObjects[map[i]].tileType;
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
    // MÉTRICA 5: DIVERSITY (HAMMING DISTANCE)
    // ============================================================

    /// <summary>
    /// Calcula la distancia de Hamming normalizada entre todos los pares de
    /// mapas almacenados en el lote. Devuelve la media y la desviación estándar
    /// de las distancias pairwise.
    ///
    /// La distancia se normaliza sobre las posiciones con al menos una tile
    /// jugable en alguno de los dos mapas comparados.
    ///
    /// Esta métrica cuantifica la diversidad del generador: un valor alto
    /// con restricciones activas indica que el espacio de soluciones no se
    /// colapsa al añadir control editorial, lo que refuta la hipótesis de
    /// que las restricciones reducen la variedad generativa.
    ///
    /// Referencia metodológica: Katz, J., Bateni, B. y Smith, A. M. (2024).
    /// You-Only-Randomize-Once: Shaping Statistical Properties in
    /// Constraint-based PCG. FDG '24. DOI 10.1145/3649921.3649995.
    /// </summary>
    private (float mean, float std) ComputeDiversity()
    {
        int numMaps = _storedMaps.Count;
        if (numMaps < 2) return (0f, 0f);

        int n = _storedMaps[0].Length;

        // Denominator: posiciones jugables en al menos un mapa del lote
        int denominator = 0;
        for (int i = 0; i < n; i++)
        {
            for (int m = 0; m < numMaps; m++)
            {
                if (_storedMaps[m][i] >= 0) { denominator++; break; }
            }
        }
        if (denominator == 0) return (0f, 0f);

        var distances = new List<float>(numMaps * (numMaps - 1) / 2);

        for (int a = 0; a < numMaps; a++)
        {
            for (int b = a + 1; b < numMaps; b++)
            {
                int diff = 0;
                for (int i = 0; i < n; i++)
                    if (_storedMaps[a][i] != _storedMaps[b][i]) diff++;

                distances.Add((float)diff / denominator);
            }
        }

        float mean = distances.Average();
        float variance = distances.Select(d => (d - mean) * (d - mean)).Average();
        return (mean, (float)Math.Sqrt(variance));
    }

    // ============================================================
    // PRECÓMPUTO DE DISTRIBUCIÓN OBJETIVO
    // ============================================================

    /// <summary>
    /// Construye P: suma de pesos por tileType (agrupando todas las rotaciones
    /// del mismo tipo) y los normaliza para obtener una distribución de probabilidad.
    /// Los tiles de infraestructura se excluyen del cálculo.
    /// </summary>
    private void PrecomputeTargetDistribution()
    {
        _targetDist.Clear();
        float totalWeight = 0f;

        foreach (Tile t in _wfc.tileObjects)
        {
            if (_wfc.IsInfrastructureTile(t.tileType)) continue;

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
            _accCA.Mean, _accCA.PopStd,
            _accJS.Mean, _accJS.PopStd,
            _accConn.Mean, _accConn.PopStd,
            _accEntM.Mean, _accEntM.PopStd,
            _accEntV.Mean, _accEntV.PopStd,
            divMean, divStd
        );

        Debug.Log($"[Metrics] Lote completado. SuccessRate={successRate:F3} | " +
                  $"JS={_accJS.Mean:F4} | Conn={_accConn.Mean:F3} | Div={divMean:F3}");

        // Resetear para el próximo lote
        _successCount = 0;
        _incompatibilityCount = 0;
        _storedMaps.Clear();
        _accCA.Reset(); _accJS.Reset(); _accConn.Reset();
        _accEntM.Reset(); _accEntV.Reset();
    }

    // ============================================================
    // CSV: INICIALIZACIÓN Y ESCRITURA
    // ============================================================

    private void EnsureCSVHeaders()
    {
        if (!File.Exists(_perRunPath))
            File.WriteAllText(_perRunPath,
                "run_id;tileset;map_size;config;" +
                "constraint_adherence;js_divergence;connectivity_pct;" +
                "entropy_mean;entropy_variance\n");

        if (!File.Exists(_summaryPath))
            File.WriteAllText(_summaryPath,
                "tileset;map_size;config;n_runs;success_rate;" +
                "mean_ca;std_ca;mean_js;std_js;" +
                "mean_conn;std_conn;" +
                "mean_ent_mean;std_ent_mean;mean_ent_var;std_ent_var;" +
                "mean_diversity;std_diversity\n");
    }

    private void AppendPerRunRow(int runId,
        float ca, float js, float conn, float entM, float entV)
    {
        string row = string.Join(";",
            runId, tilesetName, _mapSize, configLabel,
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
    // ACUMULADOR WELFORD (media y varianza online, sin almacenar datos)
    // ============================================================

    /// <summary>
    /// Implementa el algoritmo de Welford para calcular media y varianza
    /// de población de forma online, sin almacenar todos los valores.
    ///
    /// Referencia: Welford, B. P. (1962). Note on a method for calculating
    /// corrected sums of squares and products. Technometrics, 4(3), 419–420.
    /// DOI 10.1080/00401706.1962.10490022.
    /// </summary>
    private class WelfordAccumulator
    {
        private int _n = 0;
        private double _mean = 0.0;
        private double _M2 = 0.0;   // varianza acumulada (Welford)

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

        public void Reset()
        {
            _n = 0; _mean = 0.0; _M2 = 0.0;
        }
    }
}