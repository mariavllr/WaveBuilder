using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;

public class ScoreManager : MonoBehaviour
{
    public static ScoreManager Instance;

    [Header("Referencias")]
    public WaveFunctionGame wfcGame; 
    public List<TileScoreData> allScoreDataObjects;

    [Header("UI")]
    public TextMeshProUGUI pointsText;
    public TMPro.TextMeshProUGUI scorePreviewText; // Tu texto flotante
    public RectTransform scorePreviewContainer; // El padre (se mueve con la celda)
    public float floatHeight = 15f;  
    public float floatDuration = 1f;
    public float blinkSpeed = 5f;
    private Tween floatTween;

    [Header("Tile Highlight Settings")]
    [ColorUsage(true, true)]
    public Color highlightColor = Color.yellow;
    // Lista para recordar qué fichas están brillando y poder apagarlas luego
    private List<Tile> currentlyHighlightedTiles = new List<Tile>();
    private MaterialPropertyBlock mpb;
    private int highlightColorID;

    // OPTIMIZACIÓN O(1): Diccionario anidado [FichaOrigen][FichaVecina] = Puntos
    // Evita tener que iterar listas para buscar si hay sinergias cada vez que pones una ficha.

    private Dictionary<string, Dictionary<string, int>> synergyMap = new Dictionary<string, Dictionary<string, int>>();
    private Dictionary<string, int> basePointsMap = new Dictionary<string, int>();

    public int currentScore = 0;

    private void Awake()
    {
        Instance = this;
        highlightColorID = Shader.PropertyToID("_HighlightColor");
        mpb = new MaterialPropertyBlock();

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
        GameEvents.OnTileReleased += EvaluateTilePlacement;
        GameEvents.OnTileRotated += OnTileRotatedHandler;
        GameEvents.OnDeleteTile += OnTileDeletedHandler;
    }

    private void OnDisable()
    {
        GameEvents.OnTileReleased -= EvaluateTilePlacement;
        GameEvents.OnTileRotated -= OnTileRotatedHandler;
        GameEvents.OnDeleteTile -= OnTileDeletedHandler;
    }


    private void InitializeOptimizationMaps()
    {
        basePointsMap.Clear();
        synergyMap.Clear();

        // 1. Recorremos cada Scriptable Object
        foreach (var data in allScoreDataObjects)
        {
            // 2. Recorremos cada nombre de tile ("pine", "pineAutumn"...)
            foreach (string typeName in data.tileTypes)
            {
                // Asignamos los puntos base a este nombre específico
                basePointsMap[typeName] = data.basePoints;

                // Nos aseguramos de que existe el diccionario de sinergias para esta ficha
                if (!synergyMap.ContainsKey(typeName))
                {
                    synergyMap[typeName] = new Dictionary<string, int>();
                }

                // 3. Recorremos los bonus configurados
                foreach (var bonus in data.adjacencyBonuses)
                {
                    // 4. Recorremos los targets de cada bonus
                    foreach (string targetName in bonus.targetTileTypes)
                    {
                        // Guardamos la relación. Ej: synergyMap["pine"]["aserradero"] = 2;
                        synergyMap[typeName][targetName] = bonus.bonusPoints;
                    }
                }
            }
        }
    }

    private void EvaluateTilePlacement(Tile placedTile, Cell placedCell)
    {
        if(placedTile == null || placedCell == null) return;

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
        pointsText.text = "PUNTOS: " + currentScore;

        GameEvents.ScoreUpdated(pointsEarnedThisTurn);

        // Limpieza visual del estado de drag
        HidePreview();
        ClearHighlights();
    }

    /// <summary>
    /// Utiliza las matemáticas ya existentes en WaveFunctionGame para leer los vecinos directos 
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
        //Si la celda no está colapsada, pasamos
        if (!neighborCell.collapsed) return;
        Tile realTileInstance = neighborCell.GetComponentInChildren<Tile>();

        // Si por algún motivo está vacía, pasamos
        if (realTileInstance == null) return;

        string type = realTileInstance.tileType;

        // Filtramos los bordes del mapa y el aire
        if (type != "empty" && type != "solid" && type != "limit" && type != "air")
        {
            list.Add(realTileInstance); // Añadimos la ficha REAL a la lista
        }
    }

    private void OnTileRotatedHandler(Vector3 rotation, Tile tile)
    {
        // Al rotar, las celdas válidas cambian: ocultamos la preview hasta que
        // el jugador vuelva a pasar por encima de una celda válida con la nueva rotación.
        HidePreview();
        ClearHighlights();
    }

    private void OnTileDeletedHandler()
    {
        // El drag se canceló
        HidePreview();
        ClearHighlights();
    }

    //--------------
    //------UI------
    //--------------


    public int CalculatePotentialScore(Tile tileToPlace, Cell targetCell, out List<Tile> contributingTiles)
    {
        contributingTiles = new List<Tile>();
        if (!basePointsMap.ContainsKey(tileToPlace.tileType)) return 0;

        int potentialPoints = basePointsMap[tileToPlace.tileType];
        List<Tile> neighbors = GetActualNeighbors(targetCell);
        string pType = tileToPlace.tileType;

        foreach (Tile neighbor in neighbors)
        {
            string nType = neighbor.tileType;
            bool contributed = false;

            // A. Bonus para la ficha colocada
            if (synergyMap.ContainsKey(pType) && synergyMap[pType].TryGetValue(nType, out int bonusForPlaced))
            {
                potentialPoints += bonusForPlaced;
                contributed = true;
            }

            // B. Bonus para el vecino
            if (synergyMap.ContainsKey(nType) && synergyMap[nType].TryGetValue(pType, out int bonusForNeighbor))
            {
                potentialPoints += bonusForNeighbor;
                contributed = true;
            }

            // Si este vecino nos dio puntos, lo añadimos a la lista
            if (contributed)
            {
                contributingTiles.Add(neighbor);
            }
        }

        return potentialPoints;
    }

    // --- MOSTRAR TEXTO POSIBLE PUNTUACION ---
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
        scorePreviewContainer.gameObject.SetActive(false);
        floatTween.Pause();

        // Reseteamos el hijo a su posición original
        scorePreviewText.rectTransform.localPosition = new Vector3(
            scorePreviewText.rectTransform.localPosition.x,
            0f,
            scorePreviewText.rectTransform.localPosition.z
        );
    }

    //---ILUMINAR FICHAS AFECTADAS----
    private void Update()
    {
        //Animacion iluminacion
        if (currentlyHighlightedTiles.Count > 0)
        {
            float pulse = (Mathf.Sin(Time.time * blinkSpeed) + 1f) / 2f;
            Color animatedColor = Color.Lerp(Color.white, highlightColor, pulse);
            foreach (Tile tile in currentlyHighlightedTiles)
            {
                if (tile != null)
                {
                    SetTileHighlightColor(tile, animatedColor);
                }
            }
        }
    }
    public void HighlightTiles(List<Tile> tilesToHighlight)
    {
        ClearHighlights();
        currentlyHighlightedTiles.AddRange(tilesToHighlight);
    }

    public void ClearHighlights()
    {
        foreach (Tile tile in currentlyHighlightedTiles)
        {
            if (tile != null)
            {
                SetTileHighlightColor(tile, Color.white); // Color negro = apagado
            }
        }
        currentlyHighlightedTiles.Clear();
    }

    private void SetTileHighlightColor(Tile tile, Color color)
    {
        // Buscamos los renderers del objeto (puede tener varios si es un modelo compuesto)
        Renderer[] renderers = tile.GetComponentsInChildren<Renderer>();
        foreach (Renderer rend in renderers)
        {
            rend.GetPropertyBlock(mpb);
            mpb.SetColor(highlightColorID, color);
            rend.SetPropertyBlock(mpb); // ¡Aplica el color sin romper el material compartido!
        }
    }
}