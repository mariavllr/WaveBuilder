using System.Collections.Generic;
using UnityEngine;

namespace AureDevGames
{
    [System.Serializable]
    public class WaterSpline
    {
        public string name = "RiverSegment";
        public Color debugColor = Color.cyan;

        public List<Vector3> points = new List<Vector3>() { Vector3.zero, Vector3.forward * 10f };

        [Header("Curve")]
        public List<Vector3> inHandles = new List<Vector3>();
        public List<Vector3> outHandles = new List<Vector3>();
        public List<float> pointWidths = new List<float>();

        [Header("Per-spline options")]
        [Tooltip("Default/fallback width used when pointWidths is not yet populated.")]
        public float width = 5f;

        public float flowSpeed = 1f;
        public bool useBezier = true;

        [Header("Editor")]
        public bool invertNormals = false;

        [Header("Multi-river")]
        [Tooltip("Group/riverId used to separate preview meshes.")]
        public int riverId = 0;

        [Tooltip("When true, first point is linked to the last point of the previous spline.")]
        public bool linkToPrevious = false;

        [Header("Generated FlowMap (editor)")]
        public Texture2D flowMap;

        [Header("Constraints (Locked Axes)")]
        [Tooltip("Lock X/Y/Z for Point 0")]
        public Vector3Bool point0Locks = new Vector3Bool(false, false, false);
        [Tooltip("Lock X/Y/Z for Last Point")]
        public Vector3Bool lastPointLocks = new Vector3Bool(false, false, false);

        [Tooltip("Lock X/Y/Z for Out Handle of Point 0 (Bezier only)")]
        public Vector3Bool outHandle0Locks = new Vector3Bool(false, false, false);
        [Tooltip("Lock X/Y/Z for In Handle of Last Point (Bezier only)")]
        public Vector3Bool inHandleLastLocks = new Vector3Bool(false, false, false);

        [System.NonSerialized] public Mesh previewMesh;
        [System.NonSerialized] public Mesh[] previewMeshes = new Mesh[3];

        [System.Serializable]
        public struct Vector3Bool
        {
            public bool x, y, z;
            public Vector3Bool(bool x, bool y, bool z) { this.x = x; this.y = y; this.z = z; }
        }

        public void EnsureHandleCounts()
        {
            int n = points.Count;
            while (inHandles.Count < n) inHandles.Add(points[inHandles.Count]);
            while (outHandles.Count < n) outHandles.Add(points[outHandles.Count]);
            while (pointWidths.Count < n) pointWidths.Add(width);
            
            if (inHandles.Count > n) inHandles.RemoveRange(n, inHandles.Count - n);
            if (outHandles.Count > n) outHandles.RemoveRange(n, outHandles.Count - n);
            if (pointWidths.Count > n) pointWidths.RemoveRange(n, pointWidths.Count - n);

            for (int i = 0; i < n; i++)
            {
                bool inDefault = (inHandles[i] == points[i]);
                bool outDefault = (outHandles[i] == points[i]);
                if (inDefault || outDefault)
                {
                    Vector3 prev = points[Mathf.Max(0, i - 1)];
                    Vector3 next = points[Mathf.Min(n - 1, i + 1)];
                    Vector3 dir = (next - prev);
                    float dlen = dir.magnitude;
                    Vector3 tangent = (dlen > 1e-4f) ? dir.normalized : Vector3.forward;
                    float handleLen = Mathf.Clamp(dlen * 0.25f, 0.5f, 10f);
                    if (inDefault) inHandles[i] = points[i] - tangent * handleLen * 0.5f;
                    if (outDefault) outHandles[i] = points[i] + tangent * handleLen * 0.5f;
                }
            }
        }

        public Vector3 ApplyConstraints(Vector3 newPos, Vector3 oldPos, Vector3Bool locks)
        {
            if (locks.x) newPos.x = oldPos.x;
            if (locks.y) newPos.y = oldPos.y;
            if (locks.z) newPos.z = oldPos.z;
            return newPos;
        }
    }
}

