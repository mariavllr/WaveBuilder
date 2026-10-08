// ============================================================================
// RuntimeBenchmarkRunner.cs
//
// Lanzador en Unity del benchmark de COSTE COMPUTACIONAL (runtime) del
// artículo. Sustituye, solo para runtime, a CalculateExecutionTime.cs
// (que se mantiene sin cambios porque WFCQualityMetrics depende de él).
//
//   Benchmark A  A_UNRESTRICTED       ThisWork · Gumin · DeBroglie
//   Benchmark B  B0_BASELINE          ThisWork · DeBroglie
//                B1_LAYERS
//                B2_LAYERS_BOUNDARY
//                B3_LAYERS_BOUNDARY_FIXED
//                B4_LAYERS_BOUNDARY_FIXED_NEGATIVE
//   × 3 tilesets (nature, desert, farm) × 3 tamaños (10x10x5, 20x20x5, 30x30x5)
//
// Flujo:
//   1. Compila cada tileset DOS veces (sin y con negative rules) con el
//      TilePreprocessor del framework sobre COPIAS de los prefabs (los
//      prefabs originales no se modifican) y lo convierte en un
//      CompiledTileset inmutable que comparten los tres solvers.
//   2. Ejecuta la matriz con RuntimeBenchmarkEngine (único cronómetro).
//   3. Escribe attempts.csv, runs.csv, summary.csv, log.txt y manifest.txt en
//      <persistentDataPath>/RuntimeBenchmark/<fecha>_<A|B|AB>/ (nunca sobrescribe).
//
// Uso rápido: menú  WFC ▸ Runtime Benchmark ▸ Crear escena  y pulsar Play.
// Documentación completa: RUNTIME_BENCHMARK.md (misma carpeta).
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using WFCRuntimeBenchmark;

public class RuntimeBenchmarkRunner : MonoBehaviour
{
    [Serializable]
    public class FixedTileEntry
    {
        [Tooltip("Tile base (prefab sin rotar). Se elige al azar una de sus rotaciones en cada colocación.")]
        public Tile baseTile;
        [Min(1)] public int count = 1;
        [Tooltip("Capa y en la que se coloca. -1 = cualquier celda libre (semántica de REFACTOR.CreateFixedTiles).")]
        public int layer = 1;
    }

    [Serializable]
    public class TilesetEntry
    {
        public string name = "nature";
        [Tooltip("Tiles base del tileset (las mismas que usan MyWFC/Gumin/DeBroglie en el artículo).")]
        public Tile[] baseTiles;
        [Tooltip("Tile de la capa inferior (Layers): SOLID.")]
        public Tile floorTile;
        [Tooltip("Tile de la capa superior (Layers): EMPTY.")]
        public Tile emptyTile;
        [Tooltip("Tile del anillo de límite (Boundary): LIMIT. Queda fuera del dominio de colapso.")]
        public Tile limitTile;
        [Tooltip("Especificación de tiles fijas para B3/B4. Si está vacía, B3/B4 no se ejecutan para este tileset.")]
        public List<FixedTileEntry> fixedTiles = new List<FixedTileEntry>();
    }

    [Header("Tilesets (usa el menú contextual ⋮ ▸ 'Autocompletar tilesets del artículo')")]
    public List<TilesetEntry> tilesets = new List<TilesetEntry>();

    [Header("Tamaños (X, Y=altura, Z)")]
    public List<Vector3Int> sizes = new List<Vector3Int>
    {
        new Vector3Int(10, 5, 10), new Vector3Int(20, 5, 20), new Vector3Int(30, 5, 30)
    };

    [Header("Qué ejecutar")]
    public bool runBenchmarkA = true;
    public bool runBenchmarkB = true;
    [Tooltip("B0 baseline, B1 layers, B2 +boundary, B3 +fixed tiles, B4 +negative rules")]
    public bool[] benchmarkBLevels = { true, true, true, true, true };

    [Header("Protocolo")]
    [Tooltip("Runs medidos por configuración y solver (todos se usan en el resumen).")]
    public int runsPerConfig = 50;
    [Tooltip("Runs de calentamiento por configuración y solver (se registran con phase=warmup y no entran en el resumen).")]
    public int warmupRuns = 3;
    [Tooltip("Tope de intentos (reinicios) por run, idéntico para todos los solvers.")]
    public int maxAttemptsPerRun = 1000;
    public long baseSeed = 20261007;
    [Tooltip("GC.Collect() antes de cada fase cronometrada (fuera del cronómetro).")]
    public bool gcBeforeTimedPhases = true;
    [Tooltip("Intercalar los solvers run a run con orden rotado (reduce sesgos por deriva térmica o de caché).")]
    public bool interleaveSolvers = true;

    [Header("Variantes de solver")]
    [Tooltip("Selector de mínima entropía de 'This work'. Heap = MyWFC actual; Linear = REFACTOR (barrido O(N)).")]
    public ThisWorkSelector thisWorkSelector = ThisWorkSelector.Heap;
    [Tooltip("Añade también la otra variante del selector de 'This work' (análisis de sensibilidad).")]
    public bool includeThisWorkAlternativeSelector = false;
    [Tooltip("Añade DeBroglie 'native' (FixedTileConstraint/Select, celdas preasignadas dentro del CSP) en B1–B4.")]
    public bool includeDeBroglieNative = false;
    [Tooltip("Añade DeBroglie sin la propagación inicial previa a la primera decisión (comportamiento por defecto de la librería sin restricciones).")]
    public bool includeDeBroglieLazyInit = false;

    public enum CsvFormat
    {
        [Tooltip("Separador ';' y decimal ','. Para abrir directamente en Excel con configuración regional española.")]
        ExcelSpanish,
        [Tooltip("Separador ',' y decimal '.'. Formato estándar (pandas, R...).")]
        Standard
    }

    [Header("Ejecución")]
    [Tooltip("ExcelSpanish: ';' y decimal ','. Standard: ',' y decimal '.'. analyze_runtime.py lee ambos.")]
    public CsvFormat csvFormat = CsvFormat.ExcelSpanish;
    public bool startOnPlay = true;
    [Tooltip("Carpeta raíz de salida. Vacío = <persistentDataPath>/RuntimeBenchmark")]
    public string outputRoot = "";
    public bool exitPlayModeWhenDone = false;

    // ── estado ─────────────────────────────────────────────────────────
    private string status = "";
    private string outputFolder = "";
    private bool running;

    void Start()
    {
        if (startOnPlay) StartCoroutine(RunAll());
    }

    [ContextMenu("Ejecutar benchmark (en Play)")]
    public void RunFromMenu()
    {
        if (!Application.isPlaying) { Debug.LogError("[RuntimeBenchmark] Entra en Play Mode para ejecutar."); return; }
        if (!running) StartCoroutine(RunAll());
    }

    void OnGUI()
    {
        GUI.Label(new Rect(10, 10, 1400, 22), "[RuntimeBenchmark] " + status);
        if (!string.IsNullOrEmpty(outputFolder)) GUI.Label(new Rect(10, 32, 1400, 22), outputFolder);
    }

    // ════════════════════════════════════════════════════════════════
    // EJECUCIÓN
    // ════════════════════════════════════════════════════════════════

    private IEnumerator RunAll()
    {
        running = true;
        status = "compilando tilesets…";
        yield return null;

        List<TilesetPair> compiled;
        try { compiled = CompileAll(); }
        catch (Exception e) { Debug.LogException(e); status = "ERROR al compilar: " + e.Message; running = false; yield break; }

        var settings = new BenchmarkSettings
        {
            Runs = runsPerConfig, WarmupRuns = warmupRuns, MaxAttemptsPerRun = maxAttemptsPerRun,
            BaseSeed = (ulong)baseSeed, GcBeforeTimedPhases = gcBeforeTimedPhases, InterleaveSolvers = interleaveSolvers,
        };
        var options = new PlanOptions
        {
            RunA = runBenchmarkA, RunB = runBenchmarkB,
            BLevels = (bool[])benchmarkBLevels.Clone(),
            ThisWorkSelector = thisWorkSelector,
            IncludeThisWorkAlternativeSelector = includeThisWorkAlternativeSelector,
            IncludeDeBroglieNative = includeDeBroglieNative,
            IncludeDeBroglieLazyInit = includeDeBroglieLazyInit,
        };
        var sizeList = sizes.Select(s => new[] { s.x, s.y, s.z }).ToList();
        List<ConfigGroup> groups = BenchmarkPlan.Build(compiled, sizeList, options);

        // B3/B4 exigen una especificación de tiles fijas: sin ella serían B2 con otra etiqueta.
        int before = groups.Count;
        groups = groups.Where(g => !g.Level.FixedTiles || g.Tileset.FixedTiles.Count > 0).ToList();
        if (groups.Count < before)
            Debug.LogError("[RuntimeBenchmark] " + (before - groups.Count) + " configuraciones B3/B4 omitidas: tileset sin tiles fijas especificadas.");

        string tag = (runBenchmarkA ? "A" : "") + (runBenchmarkB ? "B" : "");
        string root = string.IsNullOrEmpty(outputRoot) ? Path.Combine(Application.persistentDataPath, "RuntimeBenchmark") : outputRoot;
        outputFolder = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + tag);

        Csv.Separator = csvFormat == CsvFormat.ExcelSpanish ? ';' : ',';
        Csv.DecimalComma = csvFormat == CsvFormat.ExcelSpanish;
        var sink = new CsvResultSink(outputFolder);
        sink.Echo = m => { if (m.StartsWith("[ERROR]") || m.StartsWith("[WARN]")) Debug.LogWarning("[RuntimeBenchmark] " + m); };
        File.WriteAllText(Path.Combine(outputFolder, "manifest.txt"),
            EnvironmentDescription() + "\n" + BenchmarkPlan.Describe(compiled, groups, settings, options), new UTF8Encoding(false));
        Debug.Log("[RuntimeBenchmark] " + groups.Count + " configuraciones → " + outputFolder);

        var startTime = DateTime.Now;
        IEnumerator<string> it = RuntimeBenchmarkEngine.Execute(groups, settings, sink).GetEnumerator();
        string lastGroup = "";
        while (true)
        {
            bool more;
            try { more = it.MoveNext(); }
            catch (Exception e)
            {
                Debug.LogException(e);
                sink.Log("[ERROR] Excepción: " + e);
                status = "ERROR: " + e.Message;
                break;
            }
            if (!more) { status = "COMPLETADO en " + (DateTime.Now - startTime).TotalMinutes.ToString("F1") + " min"; break; }
            status = it.Current;
            int cut = it.Current.IndexOf(" run ", StringComparison.Ordinal);
            string grp = cut > 0 ? it.Current.Substring(0, cut) : it.Current;
            if (grp != lastGroup) { Debug.Log("[RuntimeBenchmark] " + grp); lastGroup = grp; }
            yield return null; // un frame entre runs: el editor sigue respondiendo; nada de esto cae en el cronómetro
        }
        sink.Log("[END] " + status);
        sink.Dispose();
        Debug.Log("[RuntimeBenchmark] " + status + " → " + outputFolder);
        running = false;

#if UNITY_EDITOR
        if (exitPlayModeWhenDone) UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    private string EnvironmentDescription()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Environment");
        sb.AppendLine("date=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("unity_version=" + Application.unityVersion);
        sb.AppendLine("is_editor=" + Application.isEditor);
#if UNITY_EDITOR
        sb.AppendLine("editor_code_optimization=" + UnityEditor.Compilation.CompilationPipeline.codeOptimization +
                      "  (debe ser Release para medir tiempos)");
        if (UnityEditor.Compilation.CompilationPipeline.codeOptimization != UnityEditor.Compilation.CodeOptimization.Release)
            Debug.LogWarning("[RuntimeBenchmark] El editor está en modo 'Debug' de optimización de código: los tiempos no son representativos. " +
                             "Cambia a 'Release' (icono del bicho, esquina inferior derecha) antes de medir.");
#endif
#if ENABLE_IL2CPP
        sb.AppendLine("scripting_backend=IL2CPP");
#else
        sb.AppendLine("scripting_backend=Mono");
#endif
        sb.AppendLine("os=" + SystemInfo.operatingSystem);
        sb.AppendLine("cpu=" + SystemInfo.processorType + " x" + SystemInfo.processorCount + " @" + SystemInfo.processorFrequency + "MHz");
        sb.AppendLine("ram_mb=" + SystemInfo.systemMemorySize);
        sb.AppendLine("gc_incremental=" + UnityEngine.Scripting.GarbageCollector.isIncremental);
        sb.AppendLine("csv_format=" + csvFormat + (csvFormat == CsvFormat.ExcelSpanish ? " (separador ';', decimal ',')" : " (separador ',', decimal '.')"));
        return sb.ToString();
    }

    // ════════════════════════════════════════════════════════════════
    // COMPILACIÓN DE TILESETS
    // ════════════════════════════════════════════════════════════════

    private sealed class RefEq : IEqualityComparer<Tile>
    {
        public bool Equals(Tile a, Tile b) { return ReferenceEquals(a, b); }
        public int GetHashCode(Tile t) { return RuntimeHelpers.GetHashCode(t); }
    }

    private TilePreprocessor preprocessor;
    private GameObject scratchRoot;

    private List<TilesetPair> CompileAll()
    {
        if (tilesets.Count == 0) throw new InvalidOperationException("No hay tilesets configurados.");

        // TilePreprocessor propio, con contenedor inactivo para las variantes rotadas.
        scratchRoot = new GameObject("RuntimeBenchmark_Scratch");
        scratchRoot.SetActive(false);
        scratchRoot.transform.SetParent(transform, false);
        var preGo = new GameObject("TilePreprocessor");
        preGo.transform.SetParent(transform, false);
        preprocessor = preGo.AddComponent<TilePreprocessor>();
        FieldInfo fi = typeof(TilePreprocessor).GetField("newTilesContainer", BindingFlags.NonPublic | BindingFlags.Instance);
        if (fi == null) throw new InvalidOperationException("TilePreprocessor.newTilesContainer no encontrado.");

        var result = new List<TilesetPair>();
        foreach (TilesetEntry e in tilesets)
        {
            var pair = new TilesetPair { Name = e.name };
            pair.Plain = Compile(e, false, fi);
            pair.Negative = Compile(e, true, fi);
            result.Add(pair);
        }
        return result;
    }

    private CompiledTileset Compile(TilesetEntry e, bool negativeRules, FieldInfo containerField)
    {
        if (e.baseTiles == null || e.baseTiles.Length == 0) throw new InvalidOperationException(e.name + ": sin tiles base.");

        var container = new GameObject(e.name + (negativeRules ? "_neg" : "_plain"));
        container.transform.SetParent(scratchRoot.transform, false); // jerarquía inactiva: nada se activa ni se renderiza
        containerField.SetValue(preprocessor, container);

        // Copias de los prefabs: el preprocesado escribe listas de vecinos y no
        // debe tocar los assets originales que usan los demás scripts.
        var arr = new Tile[e.baseTiles.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            if (e.baseTiles[i] == null) throw new InvalidOperationException(e.name + ": tile base nula en la posición " + i);
            arr[i] = Instantiate(e.baseTiles[i], container.transform);
            arr[i].name = e.baseTiles[i].name;
        }
        int baseCount = arr.Length;

        preprocessor.excludedNeighborConstraint = negativeRules;
        preprocessor.Preprocess(ref arr);

        int tc = arr.Length;
        var index = new Dictionary<Tile, int>(new RefEq());
        for (int i = 0; i < tc; i++) index[arr[i]] = i;

        var c = new CompiledTileset
        {
            Name = e.name, NegativeRules = negativeRules, TileCount = tc,
            TileNames = new string[tc], TileTypes = new string[tc], BaseTile = new int[tc], RotationSteps = new int[tc],
            Probability = new int[tc], InDomain = new bool[tc], Allowed = new int[6][][],
        };
        var byName = new Dictionary<string, int>();
        for (int i = 0; i < baseCount; i++) byName[arr[i].name] = i;
        int missingRefs = 0;
        for (int i = 0; i < tc; i++)
        {
            Tile t = arr[i];
            c.TileNames[i] = t.name;
            c.TileTypes[i] = t.tileType;
            c.Probability[i] = t.probability;
            c.InDomain[i] = t.tileType != "limit";   // mismo criterio que MyWFC/GuminWFC/REFACTOR
            c.RotationSteps[i] = Mathf.RoundToInt(t.rotation.y / 90f) & 3;
            if (i < baseCount) c.BaseTile[i] = i;
            else
            {
                int cut = t.name.LastIndexOf("_Rotate", StringComparison.Ordinal);
                int b;
                if (cut < 0 || !byName.TryGetValue(t.name.Substring(0, cut), out b))
                    throw new InvalidOperationException(e.name + ": no se encuentra la tile base de la variante " + t.name);
                c.BaseTile[i] = b;
            }
        }
        for (int d = 0; d < 6; d++)
        {
            c.Allowed[d] = new int[tc][];
            for (int a = 0; a < tc; a++)
            {
                List<Tile> list = Neighbours(arr[a], d);
                var ids = new List<int>(list.Count);
                foreach (Tile n in list)
                {
                    int id;
                    if (n != null && index.TryGetValue(n, out id)) { if (!ids.Contains(id)) ids.Add(id); }
                    else missingRefs++;
                }
                ids.Sort();
                c.Allowed[d][a] = ids.ToArray();
            }
        }
        if (missingRefs > 0) Debug.LogWarning("[RuntimeBenchmark] " + e.name + ": " + missingRefs + " referencias de vecino fuera del tileset (ignoradas).");

        c.FloorTile = IndexOfPrefab(e, e.floorTile);
        c.EmptyTile = IndexOfPrefab(e, e.emptyTile);
        c.LimitTile = IndexOfPrefab(e, e.limitTile);
        if (c.FloorTile < 0 || c.EmptyTile < 0) throw new InvalidOperationException(e.name + ": floorTile/emptyTile deben estar en baseTiles.");
        if (c.LimitTile < 0) Debug.LogWarning("[RuntimeBenchmark] " + e.name + ": sin limitTile → Boundary no disponible.");
        if (!c.InDomain[c.FloorTile] || !c.InDomain[c.EmptyTile]) throw new InvalidOperationException(e.name + ": floor/empty deben pertenecer al dominio.");

        // Detección de (tileType, rotation) duplicados: Tile.Equals los considera iguales,
        // lo que afectaría a los diccionarios de los scripts antiguos (aquí se indexa por referencia).
        var dup = arr.GroupBy(t => t.tileType + "@" + t.rotation.y).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0) Debug.LogWarning("[RuntimeBenchmark] " + e.name + ": (tileType, rotación) duplicados: " + string.Join(", ", dup.ToArray()));

        c.Finish();

        foreach (FixedTileEntry f in e.fixedTiles)
        {
            int b = IndexOfPrefab(e, f.baseTile);
            if (b < 0) throw new InvalidOperationException(e.name + ": tile fija fuera de baseTiles: " + (f.baseTile != null ? f.baseTile.name : "null"));
            int[] variants = c.VariantsOf(b);
            if (variants.Length == 0) throw new InvalidOperationException(e.name + ": la tile fija " + c.TileNames[b] + " no pertenece al dominio.");
            c.FixedTiles.Add(new FixedTileSpec { BaseTile = b, Count = Mathf.Max(1, f.count), Layer = f.layer, Variants = variants });
        }

        Debug.Log(string.Format("[RuntimeBenchmark] {0} (negative rules={1}): {2} tiles ({3} base + {4} variantes), dominio T={5}, " +
                                "relaciones dirigidas={6} (dominio {7}), asimétricas={8}",
            e.name, negativeRules, tc, baseCount, tc - baseCount, c.T, c.DirectedRelations, c.DomainDirectedRelations, c.AsymmetricRelations));
        if (c.AsymmetricRelations > 0)
            Debug.LogError("[RuntimeBenchmark] " + e.name + ": tabla de adyacencias asimétrica. AC-4 requiere simetría; revisa con AdjacencySymmetryVerifier.");

        foreach (Tile t in arr) if (t != null) Destroy(t.gameObject);
        Destroy(container);
        return c;
    }

    private static int IndexOfPrefab(TilesetEntry e, Tile prefab)
    {
        if (prefab == null) return -1;
        // Comparación por referencia (Tile.Equals compara tileType+rotation).
        for (int i = 0; i < e.baseTiles.Length; i++) if (ReferenceEquals(e.baseTiles[i], prefab)) return i;
        return -1;
    }

    private static List<Tile> Neighbours(Tile t, int d)
    {
        switch (d)
        {
            case 0: return t.rightNeighbours;   // +X
            case 1: return t.leftNeighbours;    // -X
            case 2: return t.upNeighbours;      // +Z
            case 3: return t.downNeighbours;    // -Z
            case 4: return t.aboveNeighbours;   // +Y
            default: return t.belowNeighbours;  // -Y
        }
    }

    // ════════════════════════════════════════════════════════════════
    // AUTOCOMPLETADO (solo editor)
    // ════════════════════════════════════════════════════════════════

#if UNITY_EDITOR
    private const string NatureDir = "Assets/Prefabs/NATURE TILES/";
    private const string FarmDir = "Assets/Prefabs/GRANJA TILES/";

    private static readonly string[] NatureTiles =
    {
        "base", "base_pines", "beach", "beach_cornerExt", "beach_grassEnd_L", "beach_grassEnd_R", "borderGrass", "campfire",
        "cornerExt_border", "cornerExterior", "cornerInt_border", "cornerInterior", "EMPTY", "grass_end", "grass_end_cornerExt",
        "LIMIT", "path", "pathCorner", "pathEnd", "pine", "pineAutumn", "SOLID", "wall", "pueblo", "aserradero"
    };
    private static readonly string[] DesertTiles =
    {
        "base_sand", "borderSand", "EMPTY", "cornerInt_wall_sand", "cornerInt_border_sand", "cornerExt_wall_sand",
        "cornerExt_border_sand", "LIMIT", "sand_end", "sand_palm", "sand_end_cornerExt", "sand_wall", "SOLID"
    };
    private static readonly string[] FarmTiles =
    {
        "BaseWall", "BaseWallOuterCorner", "BaseWindow", "EMPTY", "Grass", "LIMIT", "RoofSide", "RoofSide_OuterCorner",
        "RoofSide_OuterCorner_F", "RoofTopExterior", "RoofTopInterior", "SOLID", "WallRoofTop", "GrassInnerCorner",
        "GrassOuterCorner", "GrassSide", "Soil", "CliffCorner", "CliffSide", "CliffTopCorner", "CliffTopSide", "HayBale",
        "SiloDown", "SiloMiddle", "SiloTop", "DoorSingle", "Fence", "FenceCorner"
    };

    /// <summary>
    /// Rellena los tres tilesets con las mismas listas de prefabs que usan las
    /// escenas del artículo (ARTICLE/Nature, WFC_Desert, WFC_Granja).
    /// Las tiles fijas son una PROPUESTA (ver RUNTIME_BENCHMARK.md): revísalas.
    /// </summary>
    [ContextMenu("Autocompletar tilesets del artículo")]
    public void AutofillArticleTilesets()
    {
        tilesets = new List<TilesetEntry>
        {
            MakeEntry("nature", NatureDir, NatureTiles, new[] { "pueblo", "aserradero", "campfire" }, new[] { 1, 1, 1 }),
            MakeEntry("desert", NatureDir, DesertTiles, new[] { "sand_palm" }, new[] { 2 }),
            MakeEntry("farm", FarmDir, FarmTiles, new[] { "SiloDown", "HayBale" }, new[] { 1, 1 }),
        };
        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log("[RuntimeBenchmark] Tilesets del artículo cargados. Revisa la especificación de tiles fijas antes de lanzar B3/B4.");
    }

    private static TilesetEntry MakeEntry(string name, string dir, string[] names, string[] fixedNames, int[] fixedCounts)
    {
        var e = new TilesetEntry { name = name };
        e.baseTiles = names.Select(n => Load(dir + n + ".prefab")).ToArray();
        e.floorTile = e.baseTiles.First(t => t != null && t.name == "SOLID");
        e.emptyTile = e.baseTiles.First(t => t != null && t.name == "EMPTY");
        e.limitTile = e.baseTiles.First(t => t != null && t.name == "LIMIT");
        for (int k = 0; k < fixedNames.Length; k++)
            e.fixedTiles.Add(new FixedTileEntry { baseTile = e.baseTiles.First(t => t != null && t.name == fixedNames[k]), count = fixedCounts[k], layer = 1 });
        return e;
    }

    private static Tile Load(string path)
    {
        var t = UnityEditor.AssetDatabase.LoadAssetAtPath<Tile>(path);
        if (t == null) Debug.LogError("[RuntimeBenchmark] No se encuentra el prefab " + path);
        return t;
    }
#endif
}
