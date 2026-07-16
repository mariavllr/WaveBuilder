using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Sistema de preprocesado de tiles para WFC 3D.
///
/// Responsabilidad única: dado un array de tiles definido en el inspector,
/// genera las variantes rotadas y calcula la tabla completa de adyacencias
/// (listas rightNeighbours, leftNeighbours, upNeighbours, downNeighbours,
/// aboveNeighbours, belowNeighbours de cada tile).
///
/// Uso:
///   Llamar a Preprocess(ref tileObjects) una sola vez, antes de que
///   cualquier solver (REFACTOR, GuminWFC, DeBroglieWFC) inicie la
///   generación. El array resultante contiene las tiles originales más
///   sus variantes rotadas, con las listas de vecinos completamente
///   rellenadas. Las tiles de tipo "limit" se mantienen en el array
///   resultante para que las listas de vecinos queden correctas; es
///   responsabilidad del solver filtrarlas de su dominio si no quiere
///   colapsar tiles LIMIT (REFACTOR lo hace con
///   tileObjects.Where(t => t.tileType != "limit").ToArray()).
///
/// Dependencias con otros sistemas:
///   - excludedNeighborConstraint: se recibe como parámetro en Preprocess()
///     para no acoplar este script a los flags de inspector de REFACTOR.
///   - newTilesContainer: contenedor de Unity donde se instancian las
///     variantes rotadas; se recibe como parámetro para la misma razón.
///
/// Scripts que usan la salida:
///   - WaveFunctionGame_REFACTOR: la primera llamada a PreprocessTileSet()
///     delega aquí. Tras Preprocess(), filtra LIMIT y construye el
///     propagador AC-4.
///   - GuminWFC: recibe tileObjects ya procesado (lo asigna
///     CalculateExecutionTime antes de guminWFC.Generate()).
///   - DeBroglieWFC: accede a tileObjects procesado de REFACTOR y a las
///     listas de vecinos para construir el AdjacentModel.
///   - AdjacencySymmetryVerifier: verifica la simetría de la tabla
///     resultante.
/// </summary>
public class TilePreprocessor : MonoBehaviour
{
    [Header("Contenedor de variantes rotadas")]
    [Tooltip("GameObject padre donde se instancian las variantes rotadas. " +
             "Se desactiva al finalizar el preprocesado para no renderizar " +
             "las instancias intermedias.")]
    [SerializeField] private GameObject newTilesContainer;

    [Header("Restricción de exclusiones")]
    [Tooltip("Si true, las listas 'excludedNeighbours*' de cada tile se " +
             "respetan al calcular las adyacencias. Debe coincidir con el " +
             "flag 'excludedNeighborConstraint' de WaveFunctionGame_REFACTOR.")]
    [SerializeField] public bool excludedNeighborConstraint = true;

    // ════════════════════════════════════════════════════════════════
    //  API PÚBLICA
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Punto de entrada principal.
    /// Modifica tileArray en el lugar: añade variantes rotadas y rellena
    /// las listas de vecinos de cada tile. Al finalizar, desactiva
    /// newTilesContainer.
    /// </summary>
    /// <param name="tileArray">
    /// Array base de tiles definido en el inspector. Se pasa por referencia
    /// porque se expande con las variantes rotadas.
    /// </param>
    public void Preprocess(ref Tile[] tileArray)
    {
        ClearNeighbours(ref tileArray);
        CreateRemainingCells(ref tileArray);
        DefineNeighbourTiles(ref tileArray, ref tileArray);

        if (newTilesContainer != null)
            newTilesContainer.SetActive(false);
    }

    // ════════════════════════════════════════════════════════════════
    //  PREPROCESADO
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Limpia todas las listas de vecinos del array antes de recalcularlas.
    /// Necesario al reutilizar tiles entre generaciones o al llamar a
    /// Preprocess más de una vez.
    /// </summary>
    private void ClearNeighbours(ref Tile[] tileArray)
    {
        foreach (Tile tile in tileArray)
        {
            tile.upNeighbours.Clear();
            tile.rightNeighbours.Clear();
            tile.downNeighbours.Clear();
            tile.leftNeighbours.Clear();
            tile.aboveNeighbours.Clear();
            tile.belowNeighbours.Clear();
        }
    }

    /// <summary>
    /// Genera las variantes rotadas (90°, 180°, 270°) de cada tile del
    /// conjunto base, según las flags rotateRight/rotate180/rotateLeft
    /// declaradas en el inspector de cada Tile.
    /// </summary>
    private void CreateRemainingCells(ref Tile[] tileArray)
    {
        List<Tile> generated = new List<Tile>();

        foreach (Tile tile in tileArray)
        {
            if (tile.rotateRight) generated.Add(BuildRotatedVariant(tile, "_RotateRight", 90, 1));
            if (tile.rotate180) generated.Add(BuildRotatedVariant(tile, "_Rotate180", 180, 2));
            if (tile.rotateLeft) generated.Add(BuildRotatedVariant(tile, "_RotateLeft", 270, 3));
        }

        if (generated.Count > 0)
            tileArray = tileArray.Concat(generated).ToArray();
    }

    /// <summary>
    /// Crea una variante rotada de una tile y le aplica la transformación
    /// de sockets, exclusiones y faldas correspondiente.
    /// </summary>
    private Tile BuildRotatedVariant(Tile original, string suffix, float yDegrees, int quarterSteps)
    {
        Tile rotated = CreateNewTileVariation(original, suffix);
        RotateBorders(original, rotated, quarterSteps);
        rotated.rotation = new Vector3(0f, yDegrees, 0f);
        return rotated;
    }

    /// <summary>
    /// Instancia una copia del GameObject de la tile bajo newTilesContainer
    /// y configura sus propiedades base (tipo, probabilidad, offsets, flags
    /// de rotación). Las listas de vecinos se calculan después en
    /// DefineNeighbourTiles.
    /// </summary>
    private Tile CreateNewTileVariation(Tile tile, string nameVariation)
    {
        GameObject newTile = Instantiate(tile.gameObject, newTilesContainer.transform);
        newTile.name = tile.gameObject.name + nameVariation;
        newTile.tag = tile.gameObject.tag;
        newTile.SetActive(false);

        Tile tileRotated = newTile.GetComponent<Tile>();
        tileRotated.tileType = tile.tileType;
        tileRotated.probability = tile.probability;
        tileRotated.isInfrastructureTile = tile.isInfrastructureTile;
        tileRotated.positionOffset = tile.positionOffset;
        tileRotated.rotateRight = tile.rotateRight;
        tileRotated.rotate180 = tile.rotate180;
        tileRotated.rotateLeft = tile.rotateLeft;

        return tileRotated;
    }

    // ════════════════════════════════════════════════════════════════
    //  ROTACIÓN DE SOCKETS, EXCLUSIONES Y FALDAS
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Rota los sockets, exclusiones y faldas de una tile k pasos de 90°
    /// en sentido horario.
    /// Fórmula geométrica: para una rotación de k pasos, el lado del índice
    /// i de la tile rotada toma el valor del lado (i − k) mod 4 de la
    /// original. Numeración horaria de lados: U=0, R=1, D=2, L=3 para
    /// sockets/exclusiones; N=0, E=1, S=2, W=3 para faldas cardinales;
    /// NE=0, SE=1, SW=2, NW=3 para faldas diagonales.
    ///
    /// Los sockets verticales (above/below) no rotan en plano: solo se
    /// actualiza el rotationIndex.
    /// </summary>
    private void RotateBorders(Tile original, Tile rotated, int k)
    {
        int steps = ((k % 4) + 4) % 4;
        if (steps == 0) return;

        // Sockets horizontales (U, R, D, L)
        var srcSockets = new[] { original.upSocket, original.rightSocket,
                                 original.downSocket, original.leftSocket };
        rotated.upSocket = srcSockets[Wrap(0 - steps)];
        rotated.rightSocket = srcSockets[Wrap(1 - steps)];
        rotated.downSocket = srcSockets[Wrap(2 - steps)];
        rotated.leftSocket = srcSockets[Wrap(3 - steps)];

        // Sockets verticales: solo se reetiqueta la rotación
        rotated.aboveSocket = original.aboveSocket;
        rotated.aboveSocket.rotationIndex =
            (original.aboveSocket.rotationIndex + steps * 90) % 360;
        rotated.belowSocket = original.belowSocket;
        rotated.belowSocket.rotationIndex =
            (original.belowSocket.rotationIndex + steps * 90) % 360;

        // Exclusiones horizontales
        var srcExcl = new[] { original.excludedNeighboursUp,   original.excludedNeighboursRight,
                              original.excludedNeighboursDown, original.excludedNeighboursLeft };
        rotated.excludedNeighboursUp = srcExcl[Wrap(0 - steps)];
        rotated.excludedNeighboursRight = srcExcl[Wrap(1 - steps)];
        rotated.excludedNeighboursDown = srcExcl[Wrap(2 - steps)];
        rotated.excludedNeighboursLeft = srcExcl[Wrap(3 - steps)];

        // Exclusiones verticales (no rotan en plano)
        rotated.excludedNeighboursAbove = original.excludedNeighboursAbove;
        rotated.excludedNeighboursBelow = original.excludedNeighboursBelow;

        if (rotated.useSkirts) RotateSkirts(original, rotated, steps);
    }

    /// <summary>
    /// Rotación específica de las faldas (cardinales y diagonales).
    /// </summary>
    private void RotateSkirts(Tile original, Tile rotated, int steps)
    {
        var srcCard = new[] { original.skirtNorth, original.skirtEast,
                              original.skirtSouth, original.skirtWest };
        rotated.skirtNorth = srcCard[Wrap(0 - steps)];
        rotated.skirtEast = srcCard[Wrap(1 - steps)];
        rotated.skirtSouth = srcCard[Wrap(2 - steps)];
        rotated.skirtWest = srcCard[Wrap(3 - steps)];

        var srcDiag = new[] { original.skirtCornerNE, original.skirtCornerSE,
                              original.skirtCornerSW, original.skirtCornerNW };
        rotated.skirtCornerNE = srcDiag[Wrap(0 - steps)];
        rotated.skirtCornerSE = srcDiag[Wrap(1 - steps)];
        rotated.skirtCornerSW = srcDiag[Wrap(2 - steps)];
        rotated.skirtCornerNW = srcDiag[Wrap(3 - steps)];
    }

    /// <summary>
    /// Aritmética modular para rotaciones de cuatro lados. Devuelve el
    /// índice (i mod 4) garantizando un resultado en [0, 3], incluso si
    /// i es negativo.
    /// </summary>
    private static int Wrap(int i) => ((i % 4) + 4) % 4;

    // ════════════════════════════════════════════════════════════════
    //  TABLA DE ADYACENCIAS
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Calcula la tabla completa de adyacencias permitidas entre tiles
    /// del conjunto, comparando sockets, exclusiones bidireccionales y
    /// reglas de simetría/flip (caras horizontales) o de rotación
    /// invariante (caras verticales).
    ///
    /// Para cada par (a, b) y cada una de las seis direcciones del cubo,
    /// si la conexión es válida se añade b a la lista de vecinos
    /// correspondiente de a.
    ///
    /// Nota sobre la simetría resultante: la condición bidireccional de
    /// excludedNeighborConstraint garantiza que si b ∈ a.rightNeighbours
    /// entonces a ∈ b.leftNeighbours (y análogamente para el resto de
    /// pares de caras opuestas), siempre que no haya exclusiones
    /// asimétricas en los ScriptableObjects de las tiles.
    /// Verificable con AdjacencySymmetryVerifier.
    /// </summary>
    public void DefineNeighbourTiles(ref Tile[] tileArray, ref Tile[] otherTileArray)
    {
        foreach (Tile a in tileArray)
            foreach (Tile b in otherTileArray)
            {
                // Caras horizontales: regla de simetría/flip
                if (CanConnectHorizontal(a.upSocket, b.downSocket,
                        a.excludedNeighboursUp, b.excludedNeighboursDown,
                        a.tileType, b.tileType))
                    a.upNeighbours.Add(b);

                if (CanConnectHorizontal(a.downSocket, b.upSocket,
                        a.excludedNeighboursDown, b.excludedNeighboursUp,
                        a.tileType, b.tileType))
                    a.downNeighbours.Add(b);

                if (CanConnectHorizontal(a.rightSocket, b.leftSocket,
                        a.excludedNeighboursRight, b.excludedNeighboursLeft,
                        a.tileType, b.tileType))
                    a.rightNeighbours.Add(b);

                if (CanConnectHorizontal(a.leftSocket, b.rightSocket,
                        a.excludedNeighboursLeft, b.excludedNeighboursRight,
                        a.tileType, b.tileType))
                    a.leftNeighbours.Add(b);

                // Caras verticales: regla de rotación invariante
                if (CanConnectVertical(a.aboveSocket, b.belowSocket,
                        a.excludedNeighboursAbove, b.excludedNeighboursBelow,
                        a.tileType, b.tileType))
                    a.aboveNeighbours.Add(b);

                if (CanConnectVertical(a.belowSocket, b.aboveSocket,
                        a.excludedNeighboursBelow, b.excludedNeighboursAbove,
                        a.tileType, b.tileType))
                    a.belowNeighbours.Add(b);
            }
    }

    // ════════════════════════════════════════════════════════════════
    //  REGLAS DE CONEXIÓN
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Determina si dos tiles pueden ser vecinas en una dirección horizontal.
    /// Tres condiciones deben cumplirse simultáneamente:
    ///   1. Los sockets coinciden (mismo tipo, mismo sistema legacy/ScriptableObject).
    ///   2. Ninguna tile excluye al tipo de la otra (si excludedNeighborConstraint).
    ///   3. La combinación de isSymmetric / isFlipped permite la conexión geométrica:
    ///      una cara puede conectar con otra si al menos una es simétrica, o si
    ///      tienen flags de flip opuestos (una es la "imagen espejo" de la otra).
    /// </summary>
    private bool CanConnectHorizontal(
        Tile.Socket socketA, Tile.Socket socketB,
        List<string> excludedA, List<string> excludedB,
        string typeA, string typeB)
    {
        if (!SocketsMatch(socketA, socketB)) return false;

        if (excludedNeighborConstraint
            && (excludedA.Contains(typeB) || excludedB.Contains(typeA)))
            return false;

        return socketA.isSymmetric || socketB.isSymmetric
            || (socketA.isFlipped != socketB.isFlipped);
    }

    /// <summary>
    /// Determina si dos tiles pueden ser vecinas en una dirección vertical.
    /// Las caras superior e inferior no aplican simetría ni flip: o ambas
    /// son rotacionalmente invariantes, o sus rotationIndex coinciden.
    /// </summary>
    private bool CanConnectVertical(
        Tile.Socket socketA, Tile.Socket socketB,
        List<string> excludedA, List<string> excludedB,
        string typeA, string typeB)
    {
        if (!SocketsMatch(socketA, socketB)) return false;

        if (excludedNeighborConstraint
            && (excludedA.Contains(typeB) || excludedB.Contains(typeA)))
            return false;

        return (socketA.rotationallyInvariant && socketB.rotationallyInvariant)
            || (socketA.rotationIndex == socketB.rotationIndex);
    }

    /// <summary>
    /// Compara dos sockets soportando el sistema antiguo (Enum) y el nuevo
    /// (ScriptableObject). Una mezcla de ambos sistemas nunca conecta.
    /// </summary>
    private bool SocketsMatch(Tile.Socket socketA, Tile.Socket socketB)
    {
        if (socketA.HasCustomDefinition && socketB.HasCustomDefinition)
            return socketA.socketDefinition == socketB.socketDefinition;

        if (!socketA.HasCustomDefinition && !socketB.HasCustomDefinition)
            return socketA.socket_name == socketB.socket_name;

        return false; // mezcla de sistemas
    }
}