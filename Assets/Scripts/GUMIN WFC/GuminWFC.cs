// ============================================================================
// GuminWFC.cs
//
// Implementación fiel al algoritmo Wave Function Collapse de Maxim Gumin (2016)
// [https://github.com/mxgmn/WaveFunctionCollapse], extendida de 2D (4 dirs)
// a 3D (6 dirs ortogonales) y adaptada al entorno Unity del proyecto.
//
// Correspondencia directa con el código original de Gumin:
//   Model.Init()              → InitWave()
//   Model.Clear()             → Clear()
//   Model.Run()               → RunAlgorithm()
//   Model.NextUnobservedNode  → SelectNextCell()          [heurística Entropy]
//   Model.Observe()           → CollapseCell()
//   Model.Propagate()         → Propagate()               [AC-4]
//   Model.Ban()               → Ban()
//   SimpleTiledModel (ctor)   → BuildPropagator()
//
// Diferencias con el original (necesarias para la integración 3D):
//   · 4 direcciones → 6 direcciones ortogonales (±X, ±Y, ±Z).
//   · El input NO es un XML ni una imagen: se reciben directamente los Tile[]
//     del framework propio, cuyos campos *Neighbours ya están calculados.
//   · No hay periodicidad (volumen finito con bordes duros).
//   · La instanciación visual se hace mediante Instantiate() de Unity en lugar
//     de escribir un bitmap a disco.
//
// USO:
//   1. Asignar tileObjects[] con la misma lista preprocesada que usa REFACTOR
//      (incluyendo rotaciones ya generadas).
//   2. Asignar outputParent (un Transform vacío, separado del de REFACTOR).
//   3. Configurar dimensiones, gridSize y seed.
//   4. Llamar Generate() o activar generateOnStart.
//
// Copyright (C) 2016 Maxim Gumin, The MIT License (MIT)
// Adaptación 3D: proyecto WFC Framework (véase artículo adjunto)
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GuminWFC : MonoBehaviour
{
    // =========================================================================
    // INSPECTOR
    // =========================================================================

    [Header("Input – tiles con vecinos ya calculados")]
    [Tooltip("Misma lista tileObjects que usa REFACTOR, preprocesada con vecinos y rotaciones.")]
    public Tile[] tileObjects;

    [Header("Dimensiones del grid 3D")]
    public int dimensionsX = 10;
    public int dimensionsY = 3;
    public int dimensionsZ = 10;
    public float gridSize = 1f;

    [Header("Parámetros del algoritmo (Gumin)")]
    [Tooltip("Semilla aleatoria. 0 = semilla del sistema (no determinista).")]
    public int seed = 0;
    [Tooltip("Límite de reinicios por incompatibilidad antes de abortar.")]
    public int maxRetries = 100;

    [Header("Salida visual")]
    [Tooltip("Transform padre bajo el que se instanciarán los tiles del resultado.")]
    public Transform outputParent;
    public bool generateOnStart = false;

    // =========================================================================
    // EVENTOS
    // =========================================================================

    public delegate void OnStartGeneration();
    public delegate void OnIncompatibility();
    public delegate void OnEndGeneration();

    /// <summary>Disparado justo antes de comenzar cada generación completa.</summary>
    public static event OnStartGeneration onStartGeneration;
    /// <summary>Disparado cada vez que un intento falla por contradicción.</summary>
    public static event OnIncompatibility onIncompatibility;
    /// <summary>Disparado cuando la generación termina con éxito.</summary>
    public static event OnEndGeneration onEndGeneration;

    // =========================================================================
    // PROPIEDADES PÚBLICAS (consulta post-generación)
    // =========================================================================

    /// <summary>Número de reinicios por incompatibilidad en la última llamada.</summary>
    public int FailCount { get; private set; }

    // =========================================================================
    // ESTADO INTERNO – ESTRUCTURAS DE GUMIN (Model.cs)
    // =========================================================================

    // T  = número total de tiles (índices 0..T-1)
    // MX = dimensionsX, MY = dimensionsY, MZ = dimensionsZ
    // N  = totalCells = MX * MY * MZ
    private int T, MX, MY, MZ, N;

    // wave[i*T + t] : ¿es el tile t todavía posible en la celda i?
    // Equivale a Model.wave[i][t].
    private bool[] wave;

    // propagator[d*T + t] : array de índices de tiles compatibles con t en dirección d.
    // Equivale a Model.propagator[d][t].
    // Calculado una vez en BuildPropagator() a partir de los *Neighbours de cada Tile.
    private int[][] propagator;

    // compatible[(i*T + t)*6 + d] : contador AC-4.
    // Número de tiles todavía posibles en el vecino de i en dirección d que "soportan" a t en i.
    // Cuando llega a 0, t no tiene soporte y debe eliminarse de i.
    // Equivale a Model.compatible[i][t][d].
    private int[] compatible;

    // observed[i] : índice del tile colapsado en celda i; -1 si no colapsada.
    private int[] observed;

    // Pesos derivados de Tile.probability (≥1).
    // Equivalen a Model.weights y Model.weightLogWeights.
    private double[] weights;
    private double[] weightLogWeights;
    private double[] distribution;
    private double sumOfWeights, sumOfWeightLogWeights, startingEntropy;

    // Agregados por celda para el cálculo incremental de entropía de Shannon.
    // Equivalen a Model.sumsOfOnes, sumsOfWeights, sumsOfWeightLogWeights, entropies.
    private int[] sumsOfOnes;
    private double[] sumsOfWeights_c;
    private double[] sumsOfWeightLogWeights_c;
    private double[] entropies;

    // Stack para propagación AC-4.  Equivale a Model.stack y Model.stacksize.
    private (int cellIdx, int tileIdx)[] stack;
    private int stacksize;

    // Flag de contradicción (dominio vacío en alguna celda).
    private bool contradiction;

    // RNG con semilla controlada.
    private System.Random rng;

    // =========================================================================
    // DIRECCIONES 3D
    // =========================================================================
    //
    // Índice │ Dirección │ Delta (x,y,z) │ Opuesto
    // ───────┼───────────┼───────────────┼────────
    //   0    │ Right +X  │ (+1, 0,  0)   │   1
    //   1    │ Left  -X  │ (-1, 0,  0)   │   0
    //   2    │ Fwd   +Z  │ ( 0, 0, +1)   │   3
    //   3    │ Back  -Z  │ ( 0, 0, -1)   │   2
    //   4    │ Above +Y  │ ( 0,+1,  0)   │   5
    //   5    │ Below -Y  │ ( 0,-1,  0)   │   4
    //
    // El índice lineal de una celda (x,y,z) es:
    //   i = x + z*MX + y*MX*MZ       (mismo orden que REFACTOR)

    private static readonly int[] DX = { 1, -1, 0, 0, 0, 0 };
    private static readonly int[] DY = { 0, 0, 0, 0, 1, -1 };
    private static readonly int[] DZ = { 0, 0, 1, -1, 0, 0 };
    private static readonly int[] OPPOSITE = { 1, 0, 3, 2, 5, 4 };

    // =========================================================================
    // UNITY LIFECYCLE
    // =========================================================================

    void Start()
    {
        if (generateOnStart) Generate();
    }

    // =========================================================================
    // API PÚBLICA
    // =========================================================================

    /// <summary>
    /// Punto de entrada principal. Construye el propagador (una vez), reserva
    /// las estructuras internas e inicia la generación en una corrutina para
    /// no bloquear el hilo principal entre intentos.
    /// </summary>
    public void Generate()
    {
        StartCoroutine(GenerateCoroutine());
    }

    // =========================================================================
    // CORRUTINA PRINCIPAL
    // =========================================================================

    private IEnumerator GenerateCoroutine()
    {
        FailCount = 0;

        if (!BuildPropagator())
        {
            Debug.LogError("[GuminWFC] Error construyendo el propagador. ¿tileObjects está vacío?");
            yield break;
        }

        // Init calcula pesos y reserva arrays (no cambia entre intentos).
        InitWave();

        onStartGeneration?.Invoke();

        bool success = false;
        while (!success && FailCount < maxRetries)
        {
            Clear();
            success = RunAlgorithm();

            if (!success)
            {
                FailCount++;
                Debug.Log($"[GuminWFC] Incompatibilidad #{FailCount}. Reiniciando...");
                onIncompatibility?.Invoke();
                yield return null;
            }
        }

        if (!success)
        {
            Debug.LogError($"[GuminWFC] Generación fallida tras {maxRetries} intentos.");
            yield break;
        }

        onEndGeneration?.Invoke();
        InstantiateTiles();
        Debug.Log($"[GuminWFC] Generación completada. Reinicios: {FailCount}");
    }

    // =========================================================================
    // CONSTRUCCIÓN DEL PROPAGADOR  (SimpleTiledModel ctor en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale al constructor de SimpleTiledModel en Gumin.
    /// Traduce las seis listas de vecinos de cada Tile (ya calculadas por el
    /// preprocesado de REFACTOR) al array propagator[d*T + t], que contiene
    /// los índices de tiles compatibles con t en cada una de las 6 direcciones.
    ///
    /// La dirección ↔ lista de vecinos del Tile es:
    ///   d=0 Right (+X)  ←→  tile.rightNeighbours
    ///   d=1 Left  (-X)  ←→  tile.leftNeighbours
    ///   d=2 Fwd   (+Z)  ←→  tile.upNeighbours     (eje Z en plano XZ)
    ///   d=3 Back  (-Z)  ←→  tile.downNeighbours
    ///   d=4 Above (+Y)  ←→  tile.aboveNeighbours
    ///   d=5 Below (-Y)  ←→  tile.belowNeighbours
    /// </summary>
    private bool BuildPropagator()
    {
        if (tileObjects == null || tileObjects.Length == 0) return false;

        T = tileObjects.Length;

        // Índice inverso Tile → int en O(1)
        var tileIndex = new Dictionary<Tile, int>(T);
        for (int t = 0; t < T; t++)
        {
            if (tileObjects[t] == null)
            {
                Debug.LogError($"[GuminWFC] tileObjects[{t}] es null.");
                return false;
            }
            tileIndex[tileObjects[t]] = t;
        }

        propagator = new int[6 * T][];

        for (int t = 0; t < T; t++)
        {
            Tile tile = tileObjects[t];
            propagator[0 * T + t] = ToIndices(tile.rightNeighbours, tileIndex); // Right +X
            propagator[1 * T + t] = ToIndices(tile.leftNeighbours, tileIndex); // Left  -X
            propagator[2 * T + t] = ToIndices(tile.upNeighbours, tileIndex); // Fwd   +Z
            propagator[3 * T + t] = ToIndices(tile.downNeighbours, tileIndex); // Back  -Z
            propagator[4 * T + t] = ToIndices(tile.aboveNeighbours, tileIndex); // Above +Y
            propagator[5 * T + t] = ToIndices(tile.belowNeighbours, tileIndex); // Below -Y
        }

        return true;
    }

    /// <summary>
    /// Convierte una lista de Tile a un array de índices enteros usando el
    /// mapa de búsqueda. Ignora tiles no encontradas (con advertencia).
    /// </summary>
    private static int[] ToIndices(List<Tile> neighbours, Dictionary<Tile, int> index)
    {
        var result = new List<int>(neighbours.Count);
        foreach (Tile n in neighbours)
        {
            if (n == null) continue;
            if (index.TryGetValue(n, out int idx))
                result.Add(idx);
            else
                Debug.LogWarning($"[GuminWFC] Vecino '{n.tileType}' no encontrado en tileObjects.");
        }
        return result.ToArray();
    }

    // =========================================================================
    // INIT  (Model.Init en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale a Model.Init(). Calcula las estructuras que no cambian entre
    /// reinicios: pesos, log-pesos, entropía de partida, y reserva los arrays.
    /// Se llama una sola vez por llamada a Generate().
    /// </summary>
    private void InitWave()
    {
        MX = dimensionsX;
        MY = dimensionsY;
        MZ = dimensionsZ;
        N = MX * MY * MZ;

        // Pesos desde Tile.probability (mínimo 1 para evitar log(0))
        weights = new double[T];
        weightLogWeights = new double[T];
        distribution = new double[T];

        sumOfWeights = 0;
        sumOfWeightLogWeights = 0;

        for (int t = 0; t < T; t++)
        {
            weights[t] = Math.Max(tileObjects[t].probability, 1);
            weightLogWeights[t] = weights[t] * Math.Log(weights[t]);
            sumOfWeights += weights[t];
            sumOfWeightLogWeights += weightLogWeights[t];
        }

        startingEntropy = Math.Log(sumOfWeights) - sumOfWeightLogWeights / sumOfWeights;

        // Arrays reutilizados entre reinicios (se reinician en Clear)
        wave = new bool[N * T];
        compatible = new int[N * T * 6];
        observed = new int[N];

        sumsOfOnes = new int[N];
        sumsOfWeights_c = new double[N];
        sumsOfWeightLogWeights_c = new double[N];
        entropies = new double[N];

        stack = new (int, int)[N * T];
        stacksize = 0;

        rng = (seed == 0) ? new System.Random() : new System.Random(seed);
    }

    // =========================================================================
    // CLEAR  (Model.Clear en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale a Model.Clear(). Restablece el estado de la ola al inicio de
    /// cada intento: todas las opciones habilitadas, contadores AC-4 inicializados
    /// al número total de soportes posibles, entropías al máximo.
    ///
    /// Inicialización del contador AC-4 (Gumin, Model.cs línea ~220):
    ///   compatible[i][t][d] = propagator[opposite[d]][t].Length
    ///
    /// Semántica: para el tile t en la celda i, el número de tiles que pueden
    /// estar en el vecino en dirección OPPOSITE[d] y que "soportan" a t desde
    /// ese lado. Al inicio, todos son posibles.
    /// </summary>
    private void Clear()
    {
        contradiction = false;
        stacksize = 0;

        for (int i = 0; i < N; i++)
        {
            for (int t = 0; t < T; t++)
            {
                wave[i * T + t] = true;

                // compatible[i][t][d] = propagator[OPPOSITE[d]][t].Length
                for (int d = 0; d < 6; d++)
                    compatible[(i * T + t) * 6 + d] = propagator[OPPOSITE[d] * T + t].Length;
            }

            sumsOfOnes[i] = T;
            sumsOfWeights_c[i] = sumOfWeights;
            sumsOfWeightLogWeights_c[i] = sumOfWeightLogWeights;
            entropies[i] = startingEntropy;
            observed[i] = -1;
        }
    }

    // =========================================================================
    // BUCLE PRINCIPAL  (Model.Run en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale a Model.Run(). Itera el ciclo observar → propagar hasta que
    /// todas las celdas están colapsadas o se detecta una contradicción.
    /// Devuelve true si la generación fue exitosa.
    /// </summary>
    private bool RunAlgorithm()
    {
        // Límite conservador: nunca más iteraciones que celdas en el volumen.
        int limit = N + 1;

        for (int iteration = 0; iteration < limit; iteration++)
        {
            int node = SelectNextCell();

            if (node < 0)
            {
                // Argmin < 0 significa que todas las celdas tienen sumsOfOnes <= 1,
                // es decir, están todas colapsadas. Leer el resultado.
                for (int i = 0; i < N; i++)
                    for (int t = 0; t < T; t++)
                        if (wave[i * T + t]) { observed[i] = t; break; }

                return true;
            }

            // Colapsar la celda elegida y propagar restricciones.
            CollapseCell(node);
            bool success = Propagate();
            if (!success) return false; // contradicción detectada en AC-4
        }

        // Llegamos al límite: leer estado parcial (no debería ocurrir en
        // volúmenes finitos si el tile set es coherente).
        Debug.LogWarning("[GuminWFC] Límite de iteraciones alcanzado sin completar el grid.");
        for (int i = 0; i < N; i++)
            for (int t = 0; t < T; t++)
                if (wave[i * T + t]) { observed[i] = t; break; }
        return true;
    }

    // =========================================================================
    // SELECCIÓN  (Model.NextUnobservedNode – heurística Entropy en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale a Model.NextUnobservedNode() con heurística Entropy.
    /// Recorre TODAS las celdas del dominio en O(|D|) para localizar la de
    /// menor entropía de Shannon. En caso de empate, añade ruido uniforme
    /// pequeño para resolverlo estocásticamente (idéntico a Gumin).
    ///
    /// DIFERENCIA CLAVE respecto al framework propio:
    ///   Este método es O(|D|) = O(MX·MY·MZ) por iteración.
    ///   El framework propio reduce esto a O(|∂t|) mediante la frontera activa.
    /// </summary>
    private int SelectNextCell()
    {
        double min = 1E+4;
        int argmin = -1;

        for (int i = 0; i < N; i++)
        {
            int remaining = sumsOfOnes[i];

            // Celdas ya colapsadas (1 opción) o en contradicción (0) se saltan.
            if (remaining <= 1) continue;

            double entropy = entropies[i];

            // Ruido diminuto para desempate estocástico (fiel a Gumin).
            if (entropy <= min)
            {
                double noise = 1E-6 * rng.NextDouble();
                if (entropy + noise < min)
                {
                    min = entropy + noise;
                    argmin = i;
                }
            }
        }

        return argmin;
    }

    // =========================================================================
    // COLAPSO  (Model.Observe en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale a Model.Observe(). Colapsa la celda node al tile elegido
    /// mediante selección aleatoria PONDERADA por weights[t] (Gumin, línea ~103):
    ///
    ///   distribution[t] = wave[node][t] ? weights[t] : 0
    ///   r = SampleDistribution(distribution, random.NextDouble())
    ///
    /// Todos los tiles excepto el elegido son eliminados con Ban().
    /// </summary>
    private void CollapseCell(int node)
    {
        // Construir distribución de probabilidad sobre tiles posibles.
        for (int t = 0; t < T; t++)
            distribution[t] = wave[node * T + t] ? weights[t] : 0.0;

        // Muestreo ponderado (idéntico a distribution.Random() en Gumin).
        int chosen = SampleWeighted(distribution, rng.NextDouble());

        // Prohibir todos los tiles menos el elegido.
        for (int t = 0; t < T; t++)
            if (wave[node * T + t] && t != chosen)
                Ban(node, t);
    }

    /// <summary>
    /// Selección aleatoria ponderada fiel a la función distribution.Random()
    /// de Gumin (Extensions.cs). Devuelve el índice del elemento seleccionado.
    /// </summary>
    private static int SampleWeighted(double[] dist, double r)
    {
        double total = 0;
        for (int i = 0; i < dist.Length; i++) total += dist[i];

        double threshold = r * total;
        double cumulative = 0;
        for (int i = 0; i < dist.Length; i++)
        {
            cumulative += dist[i];
            if (cumulative >= threshold) return i;
        }

        return dist.Length - 1; // fallback numérico
    }

    // =========================================================================
    // PROPAGACIÓN AC-4  (Model.Propagate en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale a Model.Propagate(). Implementa AC-4 en 6 direcciones:
    /// procesa el stack de tiles prohibidas y propaga la reducción de dominio.
    ///
    /// Cuando el tile t1 es eliminado de la celda i1:
    ///   Para cada dirección d, sea i2 = vecino(i1, d):
    ///     Para cada t2 en propagator[d][t1]  // tiles que t1 soportaba en i2
    ///       compatible[i2][t2][d]--
    ///       Si compatible[i2][t2][d] == 0 → Ban(i2, t2)
    ///
    /// El contador compatible[i2][t2][d] llega a 0 cuando ningún tile en la
    /// dirección d (desde i1) puede soportar ya a t2 en i2, lo que hace
    /// obligatoria su eliminación (AC-4).
    ///
    /// Devuelve false si se detecta una contradicción (dominio vacío).
    /// </summary>
    private bool Propagate()
    {
        while (stacksize > 0)
        {
            if (contradiction) break; // abortar propagación si ya hay fallo

            (int i1, int t1) = stack[--stacksize];

            int x1 = i1 % MX;
            int z1 = (i1 / MX) % MZ;
            int y1 = i1 / (MX * MZ);

            for (int d = 0; d < 6; d++)
            {
                int x2 = x1 + DX[d];
                int y2 = y1 + DY[d];
                int z2 = z1 + DZ[d];

                // Comprobar límites del volumen (sin periodicidad: bordes duros).
                if (x2 < 0 || x2 >= MX || y2 < 0 || y2 >= MY || z2 < 0 || z2 >= MZ) continue;

                int i2 = x2 + z2 * MX + y2 * MX * MZ;

                // Tiles que t1 soportaba en dirección d (ahora t1 está prohibido en i1).
                int[] supported = propagator[d * T + t1];

                for (int l = 0; l < supported.Length; l++)
                {
                    int t2 = supported[l];

                    ref int comp = ref compatible[(i2 * T + t2) * 6 + d];
                    comp--;

                    // t2 en i2 ha perdido todos sus soportes desde dirección d → prohibirlo.
                    if (comp == 0) Ban(i2, t2);
                }
            }
        }

        return !contradiction;
    }

    // =========================================================================
    // BAN  (Model.Ban en Gumin)
    // =========================================================================

    /// <summary>
    /// Equivale a Model.Ban(). Elimina el tile t de la celda i:
    ///   1. Desactiva wave[i][t].
    ///   2. Anula los contadores compatible[i][t][d] para que los vecinos
    ///      no los computen como soporte válido.
    ///   3. Encola (i, t) en el stack para su propagación posterior.
    ///   4. Actualiza incrementalmente los agregados de entropía de Shannon.
    ///   5. Detecta contradicción si sumsOfOnes[i] llega a 0.
    /// </summary>
    private void Ban(int i, int t)
    {
        wave[i * T + t] = false;

        // Anular todos los contadores de soporte de este tile
        // (ya no puede ser soporte para ningún vecino).
        int baseComp = (i * T + t) * 6;
        for (int d = 0; d < 6; d++) compatible[baseComp + d] = 0;

        // Encolar para propagar la eliminación.
        stack[stacksize++] = (i, t);

        // Actualización incremental de entropía (Gumin, Model.Ban línea ~190).
        sumsOfOnes[i]--;
        sumsOfWeights_c[i] -= weights[t];
        sumsOfWeightLogWeights_c[i] -= weightLogWeights[t];

        if (sumsOfOnes[i] == 0)
        {
            // Dominio vacío: contradicción.
            contradiction = true;
        }
        else
        {
            double s = sumsOfWeights_c[i];
            entropies[i] = (s > 0)
                ? Math.Log(s) - sumsOfWeightLogWeights_c[i] / s
                : 0;
        }
    }

    // =========================================================================
    // INSTANCIACIÓN VISUAL
    // =========================================================================

    /// <summary>
    /// Genera la representación 3D del resultado en la escena de Unity.
    /// Para cada celda, instancia el prefab del tile observado aplicando
    /// la rotación y el offset de posición definidos en el ScriptableObject.
    ///
    /// Usa la misma lógica de instanciación que REFACTOR.InstantiateTileInCell()
    /// para garantizar que ambos sistemas produzcan visualmente resultados
    /// comparables bajo las mismas condiciones.
    /// </summary>
    private void InstantiateTiles()
    {
        ClearOutput();

        Vector3 origin = (outputParent != null)
            ? outputParent.position
            : transform.position;

        for (int i = 0; i < N; i++)
        {
            int tIdx = observed[i];
            if (tIdx < 0) continue;

            Tile tileRef = tileObjects[tIdx];

            // Reconstruir coordenadas 3D desde el índice lineal.
            int x = i % MX;
            int z = (i / MX) % MZ;
            int y = i / (MX * MZ);

            Vector3 worldPos = origin + new Vector3(x * gridSize, y * gridSize, z * gridSize);

            Tile instance = Instantiate(tileRef, worldPos, Quaternion.identity, outputParent);

            // Aplicar rotación y offset igual que REFACTOR.
            if (tileRef.rotation != Vector3.zero)
                instance.transform.Rotate(tileRef.rotation, Space.Self);

            instance.transform.position += tileRef.positionOffset;
            instance.gameObject.SetActive(true);
        }
    }

    /// <summary>Destruye todos los hijos del outputParent antes de generar.</summary>
    private void ClearOutput()
    {
        if (outputParent == null) return;
        for (int i = outputParent.childCount - 1; i >= 0; i--)
            DestroyImmediate(outputParent.GetChild(i).gameObject);
    }

    // =========================================================================
    // HELPERS DE DIAGNÓSTICO (opcionales, útiles en Editor)
    // =========================================================================

#if UNITY_EDITOR
    [ContextMenu("Generate Now")]
    private void GenerateFromMenu() => Generate();

    private void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.4f);
        Vector3 size = new Vector3(dimensionsX * gridSize, dimensionsY * gridSize, dimensionsZ * gridSize);
        Vector3 center = size * 0.5f - Vector3.one * gridSize * 0.5f;
        Gizmos.DrawWireCube(center, size);
    }
#endif
}