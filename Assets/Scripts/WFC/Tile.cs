using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Tile : MonoBehaviour
{
    public enum Border
    {
        GRASS,
        PATH,
        WATER,
        EMPTY,
        WALL_LATERAL,
        WALL_TOP,
        WALL_CORNER_EXT,
        WALL_CORNER_INT,
        BORDER,
        GRASS_BORDER,
        SOLID,
        SAND,
        GRASS_SAND,
        SAND_WALL_LATERAL,
        SAND_WALL_CORNER_EXT,
        SAND_WALL_CORNER_INT,
        SAND_BORDER,
        SAND_WALL_TOP,
        LIMIT,
        GRASS_END,
        BEACH

    }
    [Serializable]
    public struct Socket
    {
        public Border socket_name;
        //for horizontal faces
        [Header("For HORIZONTAL faces")]
        public bool horizontalFace;
        public bool isSymmetric;
        public bool isFlipped;
        //for vertical faces
        [Header("For VERTICAL faces")]
        public bool verticalFace;
        public int rotationIndex;
        public bool rotationallyInvariant;
    }

    public string tileType;
    public int probability;

    [Header("Create rotated tiles")]
    public bool rotateRight;
    public bool rotate180;
    public bool rotateLeft;

    public Vector3 rotation;
    public Vector3 scale;
    public Vector3 positionOffset;

    public List<Tile> upNeighbours = new List<Tile>();
    public List<Tile> rightNeighbours = new List<Tile>();
    public List<Tile> downNeighbours = new List<Tile>();
    public List<Tile> leftNeighbours = new List<Tile>();
    public List<Tile> aboveNeighbours = new List<Tile>();    // Y+
    public List<Tile> belowNeighbours = new List<Tile>();    // Y-

    [Header("Excluded neighbours")]
    public List<string> excludedNeighboursUp = new();
    public List<string> excludedNeighboursRight = new();
    public List<string> excludedNeighboursDown = new();
    public List<string> excludedNeighboursLeft = new();

    public bool excludeInTopLayer;

    [Tooltip("Para definir la direccion la derecha siempre ser� el eje X (rojo) y arriba ser� el eje Z (azul)")]
    [Header("Sockets")]
    public Socket upSocket;
    public Socket rightSocket;
    public Socket leftSocket;
    public Socket downSocket;
    public Socket aboveSocket;
    public Socket belowSocket;

    public override bool Equals(object obj)
    {
        if (obj is Tile other)
        {
            return this.tileType == other.tileType &&
                   this.rotation == other.rotation;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(tileType, rotation);
    }

}
