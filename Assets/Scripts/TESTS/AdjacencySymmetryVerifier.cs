using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Verificador de simetría de la tabla de adyacencia generada por
/// PreprocessTileSet() / DefineNeighbourTiles().
///
/// Soporta filtrado de tiles "virtuales" (p. ej., LIMIT) que se emplean
/// durante el preprocesado para definir restricciones de borde pero que se
/// descartan al ejecutar el algoritmo de colapso. La simetría relevante para
/// la comparación con DeBroglie es la de la tabla EFECTIVA (sin tiles
/// virtuales), porque es la que el solver utiliza durante la generación.
///
/// Condición verificada, sobre el subconjunto de tiles no filtradas:
///     b ∈ a.rightNeighbours  ⟺  a ∈ b.leftNeighbours
///     b ∈ a.upNeighbours     ⟺  a ∈ b.downNeighbours
///     b ∈ a.aboveNeighbours  ⟺  a ∈ b.belowNeighbours
///
/// Recomendación de uso: ejecutar dos veces tras PreprocessTileSet():
///   1) sin filtrar (auditoría completa del preprocesado)
///   2) filtrando las tiles virtuales (la simetría que importa para DeBroglie)
/// </summary>
public static class AdjacencySymmetryVerifier
{
    private struct OppositePair
    {
        public System.Func<Tile, List<Tile>> Forward;
        public System.Func<Tile, List<Tile>> Backward;
        public string ForwardName;
        public string BackwardName;
    }

    private static readonly OppositePair[] Pairs = new[]
    {
        new OppositePair {
            Forward  = t => t.rightNeighbours, Backward = t => t.leftNeighbours,
            ForwardName = "right", BackwardName = "left"
        },
        new OppositePair {
            Forward  = t => t.upNeighbours,    Backward = t => t.downNeighbours,
            ForwardName = "up",    BackwardName = "down"
        },
        new OppositePair {
            Forward  = t => t.aboveNeighbours, Backward = t => t.belowNeighbours,
            ForwardName = "above", BackwardName = "below"
        },
    };

    /// <summary>
    /// Verifica la simetría de la tabla, opcionalmente filtrando tiles cuyo
    /// nombre contenga alguno de los prefijos/subcadenas indicados.
    /// </summary>
    /// <param name="tiles">tileObjects ya preprocesado.</param>
    /// <param name="excludedNameContains">
    /// Subcadenas a buscar en Tile.name; cualquier tile que coincida queda
    /// fuera del análisis. Pasar null o vacío para auditoría completa.
    /// Ejemplo: new[] { "limit" } para descartar las tiles virtuales LIMIT.
    /// </param>
    /// <param name="logToConsole">Si true, imprime informe en consola.</param>
    /// <returns>true si la tabla restringida al subconjunto es simétrica.</returns>
    public static bool VerifySymmetry(
        Tile[] tiles,
        string[] excludedNameContains = null,
        bool logToConsole = true)
    {
        // Construir el subconjunto activo. El filtro es por contenido del nombre
        // para tolerar sufijos de rotación como "_RotateRight".
        bool IsExcluded(Tile t)
        {
            if (excludedNameContains == null || excludedNameContains.Length == 0) return false;
            string lowered = t.name.ToLowerInvariant();
            foreach (string token in excludedNameContains)
                if (!string.IsNullOrEmpty(token) && lowered.Contains(token.ToLowerInvariant()))
                    return true;
            return false;
        }

        var activeSet = new HashSet<Tile>(tiles.Where(t => !IsExcluded(t)));
        var violations = new List<string>();

        foreach (var pair in Pairs)
        {
            // Conjuntos "backward" precalculados, restringidos al subconjunto activo.
            var backwardSets = new Dictionary<Tile, HashSet<Tile>>();
            foreach (Tile t in activeSet)
                backwardSets[t] = new HashSet<Tile>(pair.Backward(t).Where(activeSet.Contains));

            foreach (Tile a in activeSet)
            {
                foreach (Tile b in pair.Forward(a))
                {
                    if (!activeSet.Contains(b)) continue;   // b está filtrado; ignorar el par

                    bool reciprocal = backwardSets[b].Contains(a);
                    if (!reciprocal)
                    {
                        violations.Add(
                            $"  [{pair.ForwardName}->{pair.BackwardName}] " +
                            $"'{b.name}' ∈ '{a.name}'.{pair.ForwardName}Neighbours, " +
                            $"pero '{a.name}' ∉ '{b.name}'.{pair.BackwardName}Neighbours");
                    }
                }
            }
        }

        bool symmetric = violations.Count == 0;

        if (logToConsole)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Verificación de simetría de la tabla de adyacencia ===");
            string filterDesc = (excludedNameContains == null || excludedNameContains.Length == 0)
                ? "(sin filtro)"
                : $"(filtrando: {string.Join(", ", excludedNameContains)})";
            sb.AppendLine($"Modo: {filterDesc}");
            sb.AppendLine($"Tiles totales tras preprocesado: {tiles.Length}");
            sb.AppendLine($"Tiles activas en el análisis: {activeSet.Count}");

            if (symmetric)
            {
                sb.AppendLine("RESULTADO: la tabla restringida es SIMÉTRICA.");
                Debug.Log(sb.ToString());
            }
            else
            {
                sb.AppendLine($"RESULTADO: la tabla restringida NO es simétrica. {violations.Count} violación(es):");
                foreach (string v in violations) sb.AppendLine(v);
                Debug.LogWarning(sb.ToString());
            }
        }

        return symmetric;
    }
}