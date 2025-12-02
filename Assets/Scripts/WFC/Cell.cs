using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Cell : MonoBehaviour
{
    public bool collapsed;
    public Tile[] tileOptions;
    public bool haSidoVisitado; //debug
    public bool visitable = false; //optimization
    public int index; //debug
    public Vector3Int coords; 
    public bool showDebugVisitableCells;
    public bool centerCubeCell;

    MeshRenderer meshRenderer;



    public void CreateCell(bool collapseState, Tile[] tiles, int cellIndex, Vector3Int cellCoords)
    {
        collapsed = collapseState;
        tileOptions = tiles;
        haSidoVisitado = false;
        index = cellIndex;
        coords = cellCoords;
        centerCubeCell = false;

        meshRenderer = GetComponentInChildren<MeshRenderer>();

        if (!showDebugVisitableCells) Destroy(transform.GetChild(0).gameObject);
    }

    public void RecreateCell(Tile[] tiles)
    {
        tileOptions = tiles;
    }

    public void MakeVisitable()
    {
        visitable = true;
        //    if (!collapsed && showDebugVisitableCells) MakeVisible(true);
    }

    public void MakeVisible(bool visibility)
    {
        meshRenderer = GetComponentInChildren<MeshRenderer>();
        if (meshRenderer != null) meshRenderer.enabled = visibility;
    }

    public void ChangeAlpha(float alpha)
    {
        meshRenderer = GetComponentInChildren<MeshRenderer>();
        if (meshRenderer != null)
        {
            Color color = meshRenderer.material.color;
            color.a = alpha;
            meshRenderer.material.color = color;
        }

    }
}
