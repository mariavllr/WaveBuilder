using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Verificador de simetría de la tabla de adyacencia generada por
/// PreprocessTileSet() / DefineNeighbourTiles().
///
/// Propósito: garantizar que la relación de vecindad es bidireccional antes de
/// volcarla en el AdjacentModel de DeBroglie. DeBroglie infiere la adyacencia
/// opuesta automáticamente al llamar AddAdjacency(a, b, dir); si la tabla propia
/// no es simétrica, la comparación dejaría de ser apples-to-apples.
///
/// Condición verificada, para cada par ordenado de pares de caras opuestas:
///     b ∈ a.rightNeighbours  ⟺  a ∈ b.leftNeighbours
///     b ∈ a.upNeighbours     ⟺  a ∈ b.downNeighbours
///     b ∈ a.aboveNeighbours  ⟺  a ∈ b.belowNeighbours
///
/// Uso: llamar a VerifySymmetry(tileObjects) UNA vez, justo después de
/// PreprocessTileSet(). Devuelve true si la tabla es simétrica; en caso
/// contrario imprime cada violación con el par de tiles y la dirección.
/// </summary>
public static class AdjacencySymmetryVerifier
{
    // Cada entrada empareja una cara con su opuesta y nombra la dirección,
    // de modo que el informe sea legible: (selector cara A, selector cara opuesta B, etiqueta).
    private struct OppositePair
    {
        public System.Func<Tile, List<Tile>> Forward;   // lista de vecinos en dirección d para A
        public System.Func<Tile, List<Tile>> Backward;  // lista de vecinos en dirección opuesta para B
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
    /// Verifica la simetría de la tabla completa.
    /// </summary>
    /// <param name="tiles">El array tileObjects YA preprocesado (con rotaciones y vecinos calculados).</param>
    /// <param name="logToConsole">Si true, imprime el informe en la consola de Unity.</param>
    /// <returns>true si la tabla es perfectamente simétrica.</returns>
    public static bool VerifySymmetry(Tile[] tiles, bool logToConsole = true)
    {
        var violations = new List<string>();

        // Para comprobar "a ∈ b.<backward>" de forma O(1) en vez de O(n) por consulta,
        // se construye un HashSet por (tile, dirección) de los vecinos en la dirección backward.
        // Con tilesets de decenas/cientos de variantes esto evita un coste cuadrático innecesario.
        foreach (var pair in Pairs)
        {
            // Precalcular conjuntos de vecinos "backward" de cada tile.
            var backwardSets = new Dictionary<Tile, HashSet<Tile>>();
            foreach (Tile t in tiles)
                backwardSets[t] = new HashSet<Tile>(pair.Backward(t));

            foreach (Tile a in tiles)
            {
                foreach (Tile b in pair.Forward(a))
                {
                    // Dirección forward: b es vecino de a. ¿Es a vecino de b en la opuesta?
                    bool reciprocal = backwardSets.TryGetValue(b, out var set) && set.Contains(a);
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
            sb.AppendLine($"Tiles evaluadas (post-preprocesado): {tiles.Length}");

            if (symmetric)
            {
                sb.AppendLine("RESULTADO: la tabla es SIMÉTRICA. " +
                              "Puede volcarse en DeBroglie via AddAdjacency sin introducir asimetrías.");
                Debug.Log(sb.ToString());
            }
            else
            {
                sb.AppendLine($"RESULTADO: la tabla NO es simétrica. {violations.Count} violación(es):");
                foreach (string v in violations) sb.AppendLine(v);
                sb.AppendLine();
                sb.AppendLine("Cada línea indica una adyacencia declarada en un sentido pero no en el opuesto. " +
                              "Causa habitual: una exclusión (excludedNeighbours*) declarada en una sola tile " +
                              "del par, o un socket cuya comparación no es perfectamente bidireccional.");
                Debug.LogWarning(sb.ToString());
            }
        }

        return symmetric;
    }

    /// <summary>
    /// Variante que además devuelve la lista de violaciones, por si quieres
    /// procesarlas o exportarlas a un CSV en lugar de solo loguearlas.
    /// </summary>
    public static bool VerifySymmetry(Tile[] tiles, out List<string> violations)
    {
        violations = new List<string>();

        foreach (var pair in Pairs)
        {
            var backwardSets = new Dictionary<Tile, HashSet<Tile>>();
            foreach (Tile t in tiles)
                backwardSets[t] = new HashSet<Tile>(pair.Backward(t));

            foreach (Tile a in tiles)
                foreach (Tile b in pair.Forward(a))
                {
                    bool reciprocal = backwardSets.TryGetValue(b, out var set) && set.Contains(a);
                    if (!reciprocal)
                        violations.Add(
                            $"[{pair.ForwardName}->{pair.BackwardName}] " +
                            $"'{b.name}' in '{a.name}'.{pair.ForwardName}Neighbours " +
                            $"but '{a.name}' not in '{b.name}'.{pair.BackwardName}Neighbours");
                }
        }

        return violations.Count == 0;
    }
}