using System;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
public class DragObject : MonoBehaviour
{
    private Camera mainCamera;
    private bool isDragging = false;
    private Vector3 offset;
    private float objectZ;
    private Tile tile;


    //Para mostrar las celdas validas y mostrar una preview del objeto colocado
    private List<Cell> validCells = new List<Cell>(); // para acceder a las celdas válidas, se actualiza desde WaveFunctionGame

    private Cell currentPreviewCell = null;
    private GameObject currentPreviewInstance = null;
    private WaveFunctionGame wfc;
    private CardGenerator cardGenerator;
    private Cell closest;

    Material previewMaterial;
    private void Awake()
    {
        GameEvents.OnDeleteTile += OnTileDeleted;
        GameEvents.OnTileRotated += OnTileRotated;
        wfc = FindAnyObjectByType<WaveFunctionGame>();
        cardGenerator = FindAnyObjectByType<CardGenerator>();
    }
    private void OnDestroy()
    {
        GameEvents.OnDeleteTile -= OnTileDeleted;
        GameEvents.OnTileRotated -= OnTileRotated;
    }
    public void SetValidCells(List<Cell> cells)
    {
        validCells = cells;
    }

    void Start()
    {
        mainCamera = Camera.main;
        tile = GetComponent<Tile>();
        previewMaterial = wfc.previewMaterial;
    }



    void Update()
    {
        if (!enabled) return;

        if (!wfc.isRunning && !wfc.tutorial) return; // Si el juego está pausado, no hacer nada

        if (Input.GetMouseButtonDown(0) && cardGenerator.timerCooldown <= 0) // Click izquierdo
        {
            Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit) && hit.transform == transform)
            {
                isDragging = true;
                GameEvents.TileDragged(tile);
                objectZ = mainCamera.WorldToScreenPoint(transform.position).z;
                offset = transform.position - GetWorldMousePosition();
            }
        }



        if (isDragging)
        { 
            transform.position = GetWorldMousePosition() + offset;

            //Muestra las celdas donde se puede colocar
            if (validCells != null && validCells.Count > 0)
            {
                //Cell closest = FindClosestCell(transform.position, validCells);
                closest = FindClosestCellToMouse();

                if (closest != currentPreviewCell)
                {
                    if (currentPreviewInstance != null)
                    {
                        Destroy(currentPreviewInstance);
                    }

                    currentPreviewCell = closest;
                    // Instanciar nuevo preview
                    currentPreviewInstance = CreatePreviewAtCell(currentPreviewCell);
                    //Checkear puntos si se coloca en esa celda


                    //Sonido de cambiar de cell
                    wfc.audioSource.PlayOneShot(wfc.changeCellSound, 0.5f);
                }
            }


            //---COLOCAR TILE EN CELDA---
            if (Input.GetMouseButtonUp(0)) // Suelta el click
            {
                isDragging = false;
                //OnTileReleased?.Invoke(this.gameObject, closest); // Disparamos el evento
                GameEvents.TileReleased(tile, closest);

                if (currentPreviewInstance != null)
                {
                    Destroy(currentPreviewInstance);
                    currentPreviewInstance = null;
                    currentPreviewCell = null;
                }

                wfc.audioSource.PlayOneShot(wfc.collapseCellSound, 0.5f);
            }
        }


    }

    private Vector3 GetWorldMousePosition()
    {
        Vector3 mouseScreenPos = Input.mousePosition;
        mouseScreenPos.z = objectZ;
        return mainCamera.ScreenToWorldPoint(mouseScreenPos);
    }




    void OnTileRotated(Vector3 rotation, Tile tile)
    {
        OnTileDeleted();
    }

    void OnTileDeleted()
    {
        if (currentPreviewInstance != null)
        {
            Destroy(currentPreviewInstance);
            currentPreviewInstance = null;
            currentPreviewCell = null;
        }
    }

    //Opcion 1: Calcula celda más cercana a una tile

    private Cell FindClosestCell(Vector3 origin, List<Cell> cells)
    {
        Cell closest = null;
        float minDistSq = Mathf.Infinity;

        foreach (Cell cell in cells)
        {
            float distSq = (cell.transform.position - origin).sqrMagnitude;
            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                closest = cell;
            }
        }
        return closest;
    }

    //Opcion 2: Calcula celda más cercana al mouse

    private Cell FindClosestCellToMouse()
    {
        Cell closest = null;
        float minDistSq = Mathf.Infinity;

        Vector3 mousePos = Input.mousePosition;

        foreach (Cell cell in validCells)
        {
            Vector3 cellScreenPos = Camera.main.WorldToScreenPoint(cell.transform.position);

            // Opcional: ignorar si está detrás de la cámara
            if (cellScreenPos.z < 0)
                continue;

            float distSq = (cellScreenPos - mousePos).sqrMagnitude;

            if (distSq < minDistSq)
            {
                minDistSq = distSq;
                closest = cell;
            }
        }

        return closest;
    }

    //Opcion 3: Calcula la más cercana al mouse con un RAYCAST

  /*  private Cell FindClosestCellToMouseRaycast()
    {
        Cell closest = null;
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            Cell cell = hit.collider.GetComponent<Cell>();

            if (cell != null && validCells.Contains(cell))
            {
                return cell;
            }
        }

        return closest;
    }
  */


    private GameObject CreatePreviewAtCell(Cell cell)
    {
        // Instanciar el objeto de vista previa y desactivar la celda transparente
        GameObject preview = Instantiate(gameObject);


        // Limpiar componentes innecesarios
        DestroyImmediate(preview.GetComponent<DragObject>());
        DestroyImmediate(preview.GetComponent<Tile>());

        foreach (var col in preview.GetComponentsInChildren<Collider>())
            Destroy(col);

        // Crear y aplicar material URP personalizado
        Renderer[] renderers = preview.GetComponentsInChildren<Renderer>();

        if(previewMaterial != null)
        {
            foreach (Renderer rend in renderers)
            {
                Material[] newMats = new Material[rend.materials.Length];
                for (int i = 0; i < newMats.Length; i++)
                {
                    newMats[i] = previewMaterial;
                }
                rend.materials = newMats;
            }

            preview.transform.position = cell.transform.position;
        }

        else
        {
            Debug.LogError("Preview material not found. Please assign a material in the inspector.");
        }
        

        // Rotación y offset opcional
        Tile originalTile = GetComponent<Tile>();
        if (originalTile != null)
        {
            preview.transform.rotation = Quaternion.Euler(originalTile.rotation);
            preview.transform.position += originalTile.positionOffset;
        }

        preview.name = "PreviewTile";

        // Efecto de rebote con DOTween
        Vector3 originalPosition = preview.transform.position;
        preview.transform.position = new Vector3(originalPosition.x, originalPosition.y - 0.5f, originalPosition.z); // Empujar hacia abajo un poco
        preview.transform.DOJump(originalPosition, jumpPower: 0.25f, numJumps: 1, duration: 0.3f).SetEase(Ease.OutBounce); // Rebotar hacia arriba

        return preview;
    }
}

