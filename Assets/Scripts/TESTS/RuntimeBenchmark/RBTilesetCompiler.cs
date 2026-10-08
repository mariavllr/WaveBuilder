// ============================================================================
// RBTilesetCompiler.cs
//
// Compila un tileset del framework (prefabs base + TilePreprocessor) en un
// CompiledTileset inmutable. Lo usan RuntimeBenchmarkRunner (benchmark de
// runtime) y MyWFC (solver publicado), de modo que ambos resuelven
// exactamente el mismo problema.
//
// El preprocesado se hace sobre COPIAS de los prefabs bajo un contenedor
// inactivo: los assets originales no se modifican.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using WFCRuntimeBenchmark;
using Object = UnityEngine.Object;

public static class RBTilesetCompiler
{
    /// <summary>Especificación de tiles fijas: tile base, número de copias y capa (-1 = cualquier celda libre).</summary>
    public struct FixedInput
    {
        public Tile tile;
        public int count;
        public int layer;
    }

    private sealed class RefEq : IEqualityComparer<Tile>
    {
        public bool Equals(Tile a, Tile b) { return ReferenceEquals(a, b); }
        public int GetHashCode(Tile t) { return RuntimeHelpers.GetHashCode(t); }
    }

    private static readonly FieldInfo ContainerField =
        typeof(TilePreprocessor).GetField("newTilesContainer", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <param name="scratchParent">Transform de una jerarquía INACTIVA donde se crean las copias.</param>
    /// <param name="keepTiles">Si true, las copias preprocesadas se conservan y se devuelven en processedTiles
    /// (índice = id de tile del CompiledTileset). Si false, se destruyen.</param>
    /// <param name="uniformWeights">Si true, todas las tiles pesan 1 (probabilityConstraint desactivado).</param>
    public static CompiledTileset Compile(string name, Tile[] baseTiles, Tile floorTile, Tile emptyTile, Tile limitTile,
        IList<FixedInput> fixedTiles, bool negativeRules, bool uniformWeights,
        TilePreprocessor preprocessor, Transform scratchParent, bool keepTiles, out Tile[] processedTiles, string logTag)
    {
        if (baseTiles == null || baseTiles.Length == 0) throw new InvalidOperationException(name + ": sin tiles base.");
        if (preprocessor == null) throw new InvalidOperationException(name + ": TilePreprocessor no asignado.");
        if (ContainerField == null) throw new InvalidOperationException("TilePreprocessor.newTilesContainer no encontrado.");

        var container = new GameObject(name + (negativeRules ? "_neg" : "_plain"));
        container.transform.SetParent(scratchParent, false); // jerarquía inactiva: nada se activa ni se renderiza
        ContainerField.SetValue(preprocessor, container);

        // Copias de los prefabs: el preprocesado escribe listas de vecinos y no
        // debe tocar los assets originales que usan los demás scripts.
        var arr = new Tile[baseTiles.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            if (baseTiles[i] == null) throw new InvalidOperationException(name + ": tile base nula en la posición " + i);
            arr[i] = Object.Instantiate(baseTiles[i], container.transform);
            arr[i].name = baseTiles[i].name;
        }
        int baseCount = arr.Length;

        preprocessor.excludedNeighborConstraint = negativeRules;
        preprocessor.Preprocess(ref arr);

        int tc = arr.Length;
        var index = new Dictionary<Tile, int>(new RefEq());
        for (int i = 0; i < tc; i++) index[arr[i]] = i;

        var c = new CompiledTileset
        {
            Name = name, NegativeRules = negativeRules, TileCount = tc,
            TileNames = new string[tc], TileTypes = new string[tc], BaseTile = new int[tc], RotationSteps = new int[tc],
            Probability = new int[tc], InDomain = new bool[tc], Infrastructure = new bool[tc], Allowed = new int[6][][],
        };
        var byName = new Dictionary<string, int>();
        for (int i = 0; i < baseCount; i++) byName[arr[i].name] = i;
        int missingRefs = 0;
        for (int i = 0; i < tc; i++)
        {
            Tile t = arr[i];
            c.TileNames[i] = t.name;
            c.TileTypes[i] = t.tileType;
            c.Probability[i] = uniformWeights ? 1 : t.probability;
            c.InDomain[i] = t.tileType != "limit";   // mismo criterio que MyWFC/GuminWFC/REFACTOR
            c.Infrastructure[i] = t.isInfrastructureTile;   // SOLID, EMPTY, LIMIT (las variantes heredan el flag)
            c.RotationSteps[i] = Mathf.RoundToInt(t.rotation.y / 90f) & 3;
            if (i < baseCount) c.BaseTile[i] = i;
            else
            {
                int cut = t.name.LastIndexOf("_Rotate", StringComparison.Ordinal);
                int b;
                if (cut < 0 || !byName.TryGetValue(t.name.Substring(0, cut), out b))
                    throw new InvalidOperationException(name + ": no se encuentra la tile base de la variante " + t.name);
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
        if (missingRefs > 0) Debug.LogWarning(logTag + " " + name + ": " + missingRefs + " referencias de vecino fuera del tileset (ignoradas).");

        c.FloorTile = IndexOfPrefab(baseTiles, floorTile);
        c.EmptyTile = IndexOfPrefab(baseTiles, emptyTile);
        c.LimitTile = IndexOfPrefab(baseTiles, limitTile);
        if (c.FloorTile < 0 || c.EmptyTile < 0) throw new InvalidOperationException(name + ": floorTile/emptyTile deben estar en la lista de tiles base.");
        if (c.LimitTile < 0) Debug.LogWarning(logTag + " " + name + ": sin limitTile → Boundary no disponible.");
        if (!c.InDomain[c.FloorTile] || !c.InDomain[c.EmptyTile]) throw new InvalidOperationException(name + ": floor/empty deben pertenecer al dominio.");

        // Detección de (tileType, rotation) duplicados: Tile.Equals los considera iguales,
        // lo que afectaría a los diccionarios de los scripts antiguos (aquí se indexa por referencia).
        var dup = arr.GroupBy(t => t.tileType + "@" + t.rotation.y).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0) Debug.LogWarning(logTag + " " + name + ": (tileType, rotación) duplicados: " + string.Join(", ", dup.ToArray()));

        c.Finish();

        if (fixedTiles != null)
            foreach (FixedInput f in fixedTiles)
            {
                int b = IndexOfPrefab(baseTiles, f.tile);
                if (b < 0) throw new InvalidOperationException(name + ": tile fija fuera de la lista de tiles base: " + (f.tile != null ? f.tile.name : "null"));
                int[] variants = c.VariantsOf(b);
                if (variants.Length == 0) throw new InvalidOperationException(name + ": la tile fija " + c.TileNames[b] + " no pertenece al dominio.");
                c.FixedTiles.Add(new FixedTileSpec { BaseTile = b, Count = Mathf.Max(1, f.count), Layer = f.layer, Variants = variants });
            }

        Debug.Log(string.Format("{0} {1} (negative rules={2}): {3} tiles ({4} base + {5} variantes), dominio T={6}, " +
                                "relaciones dirigidas={7} (dominio {8}), asimétricas={9}",
            logTag, name, negativeRules, tc, baseCount, tc - baseCount, c.T, c.DirectedRelations, c.DomainDirectedRelations, c.AsymmetricRelations));
        if (c.AsymmetricRelations > 0)
            Debug.LogError(logTag + " " + name + ": tabla de adyacencias asimétrica. AC-4 requiere simetría; revisa con AdjacencySymmetryVerifier.");

        if (keepTiles)
        {
            processedTiles = arr;
        }
        else
        {
            processedTiles = null;
            foreach (Tile t in arr) if (t != null) Object.Destroy(t.gameObject);
            Object.Destroy(container);
        }
        return c;
    }

    /// <summary>Índice de un prefab en la lista de tiles base, por referencia (Tile.Equals compara tileType+rotation).</summary>
    public static int IndexOfPrefab(Tile[] baseTiles, Tile prefab)
    {
        if (prefab == null) return -1;
        for (int i = 0; i < baseTiles.Length; i++) if (ReferenceEquals(baseTiles[i], prefab)) return i;
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
}
