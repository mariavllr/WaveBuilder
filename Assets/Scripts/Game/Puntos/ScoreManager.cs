using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;

public class ScoreManager : MonoBehaviour
{
    public static ScoreManager Instance;

    [Header("Referencias")]
    public WaveFunctionGame wfcGame; // Arrastra tu WaveFunctionGame aquí desde el Inspector
    public List<TileScoreData> allScoreDataObjects; // Tus Scriptable Objects de puntuación
    [Header("UI")]
    public TMPro.TextMeshProUGUI scorePreviewText; // Tu texto flotante
    public RectTransform scorePreviewContainer; // El padre (se mueve con la celda)
    public float floatHeight = 15f;  // Cuántos píxeles sube
    public float floatDuration = 1f; // Lo lento que sube y baja

    private Tween floatTween;


    // OPTIMIZACIÓN O(1): Diccionario anidado [FichaOrigen][FichaVecina] = Puntos
    // Evita tener que iterar listas para buscar si hay sinergias cada vez que pones una ficha.

    private Dictionary<string, Dictionary<string, int>> synergyMap = new Dictionary<string, Dictionary<string, int>>();
    private Dictionary<string, int> basePointsMap = new Dictionary<string, int>();

    public int currentScore = 0;

    private void Awake()
    {
        Instance = this;
        InitializeOptimizationMaps();
    }

    private void Start()
    {
        floatTween = scorePreviewText.rectTransform.DOLocalMoveY(floatHeight, floatDuration)
            .SetRelative(true)              // Se mueve X píxeles desde su posición actual
            .SetLoops(-1, LoopType.Yoyo)    // Loop infinito que va y vuelve (arriba/abajo)
            .SetEase(Ease.InOutSine);       // Suavizado perfecto para que parezca que respira o flota

        floatTween.Pause();
    }

    private void OnEnable()
    {
        // Usamos el mismo evento que usas en tu MissionManager y OnTileRemoved
        GameEvents.OnTileReleased += EvaluateTilePlacement;
    }

    private void OnDisable()
    {
        GameEvents.OnTileReleased -= EvaluateTilePlacement;
    }


    private void InitializeOptimizationMaps()
    {
        // Mapeamos los datos de los Scriptable Objects al arrancar
        foreach (var data in allScoreDataObjects)
        {
            basePointsMap[data.tileType] = data.basePoints;
            synergyMap[data.tileType] = new Dictionary<string, int>();

            foreach (var bonus in data.adjacencyBonuses)
            {
                synergyMap[data.tileType][bonus.targetTileType] = bonus.bonusPoints;
            }
        }
    }

    private void EvaluateTilePlacement(Tile placedTile, Cell placedCell)
    {
        // 1. Si la ficha no tiene datos de puntuación, ignoramos
        if (!basePointsMap.ContainsKey(placedTile.tileType)) return;

        int pointsEarnedThisTurn = basePointsMap[placedTile.tileType];

        // 2. Obtener vecinos reales usando la lógica matemática optimizada de tu WFC
        List<Tile> neighbors = GetActualNeighbors(placedCell);

        // 3. Evaluar Sinergias Bidireccionales (Lectura directa O(1))
        string pType = placedTile.tileType;

        foreach (Tile neighbor in neighbors)
        {
            string nType = neighbor.tileType;

            // A. ¿La ficha colocada gana puntos por el vecino?
            if (synergyMap[pType].TryGetValue(nType, out int bonusForPlaced))
            {
                pointsEarnedThisTurn += bonusForPlaced;
            }

            // B. ¿El vecino gana puntos por la ficha recién colocada?
            if (synergyMap.ContainsKey(nType) && synergyMap[nType].TryGetValue(pType, out int bonusForNeighbor))
            {
                pointsEarnedThisTurn += bonusForNeighbor;
            }
        }

        // 4. Sumar al total
        currentScore += pointsEarnedThisTurn;
        Debug.Log($"Ficha: {pType} | Puntos turno: {pointsEarnedThisTurn} | Puntuación Total: {currentScore}");

        GameEvents.ScoreUpdated(pointsEarnedThisTurn);
    }

    /// <summary>
    /// Utiliza las matemáticas ya existentes en WaveFunctionGame para leer los vecinos directos 
    /// sin instanciar colliders, raycasts ni iterar toda la grid.
    /// </summary>
    private List<Tile> GetActualNeighbors(Cell centerCell)
    {
        List<Tile> validNeighbors = new List<Tile>(6); // Capacidad max de 6 direcciones (3D)

        int index = centerCell.index;
        int dimX = wfcGame.dimensionsX;
        int dimZ = wfcGame.dimensionsZ;
        int dimY = wfcGame.dimensionsY;
        int area = dimX * dimZ;

        var grid = wfcGame.gridComponents;

        // Eje X (Izquierda / Derecha)
        if (index % dimX != 0)
            TryAddNeighbor(grid[index - 1], validNeighbors);
        if ((index + 1) % dimX != 0)
            TryAddNeighbor(grid[index + 1], validNeighbors);

        // Eje Z (Abajo / Arriba en isométrico)
        if ((index / dimX) % dimZ != 0)
            TryAddNeighbor(grid[index - dimX], validNeighbors);
        if ((index / dimX) % dimZ != dimZ - 1)
            TryAddNeighbor(grid[index + dimX], validNeighbors);

        // Eje Y (Altura: Debajo / Encima) - Actívalo si quieres sinergias por apilar (ej: cascadas)
        if ((index / area) != 0)
            TryAddNeighbor(grid[index - area], validNeighbors);
        if ((index / area) != dimY - 1)
            TryAddNeighbor(grid[index + area], validNeighbors);

        return validNeighbors;
    }

    private void TryAddNeighbor(Cell neighborCell, List<Tile> list)
    {
        // Reutilizamos tu lógica: solo nos interesan celdas colapsadas
        if (!neighborCell.collapsed || neighborCell.tileOptions.Length == 0) return;

        Tile tileInfo = neighborCell.tileOptions[0];
        string type = tileInfo.tileType;

        // Filtramos límites y estructuras de relleno que tienes en tu WFC
        if (type != "empty" && type != "solid" && type != "limit" && type != "air")
        {
            list.Add(tileInfo);
        }
    }

    //------UI------

    public int CalculatePotentialScore(Tile tileToPlace, Cell targetCell)
    {
        if (!basePointsMap.ContainsKey(tileToPlace.tileType)) return 0;

        int potentialPoints = basePointsMap[tileToPlace.tileType];
        List<Tile> neighbors = GetActualNeighbors(targetCell);
        string pType = tileToPlace.tileType;

        foreach (Tile neighbor in neighbors)
        {
            string nType = neighbor.tileType;

            // A. ¿La ficha colocada ganaría puntos por el vecino?
            if (synergyMap[pType].TryGetValue(nType, out int bonusForPlaced))
            {
                potentialPoints += bonusForPlaced;
            }

            // B. ¿El vecino ganaría puntos por la ficha recién colocada?
            if (synergyMap.ContainsKey(nType) && synergyMap[nType].TryGetValue(pType, out int bonusForNeighbor))
            {
                potentialPoints += bonusForNeighbor;
            }
        }

        return potentialPoints;
    }

    // Muestra el texto con los puntos
    public void ShowPreview(int points)
    {
        // Simplemente encendemos el objeto padre y actualizamos el texto
        scorePreviewContainer.gameObject.SetActive(true);
        scorePreviewText.text = "+" + points.ToString();

        // Nos aseguramos de que la animación esté reproduciéndose
        floatTween.Play();
    }

    public void UpdatePreviewPosition(Vector3 cellWorldPosition)
    {
        if (scorePreviewContainer.gameObject.activeSelf)
        {
            // El PADRE es el que persigue a la celda
            Vector3 screenPos = Camera.main.WorldToScreenPoint(cellWorldPosition + Vector3.up * 1.5f);
            scorePreviewContainer.position = screenPos;
        }
    }

    public void HidePreview()
    {
        // Apagamos el padre directamente (cero animaciones de salida)
        scorePreviewContainer.gameObject.SetActive(false);

        // Pausamos la animación para no gastar recursos a lo tonto
        floatTween.Pause();

        // Reseteamos el hijo a su posición original (para que la próxima vez empiece desde el centro)
        scorePreviewText.rectTransform.localPosition = new Vector3(
            scorePreviewText.rectTransform.localPosition.x,
            0f,
            scorePreviewText.rectTransform.localPosition.z
        );
    }
}