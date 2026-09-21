#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static AureDevGames.WaterCreatorData;
using static AureDevGames.WaterSpline;

namespace AureDevGames
{
    public static class WaterMeshHelper
    {
        public struct Row
        {
            public Vector3 pos;
            public Vector3 tangent;
            public float width;
            public float flow;
            public bool invertNormals;
            public Row(Vector3 p, Vector3 t, float w, float f, bool inv = false)
            {
                pos = p; tangent = t; width = w; flow = f; invertNormals = inv;
            }
        }

        public static void GenerateAllPreviews(WaterCreatorData data, bool forceFullCleanup = false)
        {
            if (data == null) return;

            string activeScenePath = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path;
            string activeSceneGUID = AssetDatabase.AssetPathToGUID(activeScenePath);
            bool isBound = data.sceneGUIDs != null && data.sceneGUIDs.Contains(activeSceneGUID);

            string rootName = $"WaterCreator_Previews_{data.name}";

            if (!isBound)
            {

                var unwelcomeRoot = FindInactiveByName(rootName);
                if (unwelcomeRoot != null) Object.DestroyImmediate(unwelcomeRoot);
                return;
            }

            var root = FindInactiveByName(rootName);

            if (root != null)
            {
                if (forceFullCleanup)
                {

                    for (int i = root.transform.childCount - 1; i >= 0; i--)
                    {
                        Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
                    }
                }
                else
                {

                    for (int i = root.transform.childCount - 1; i >= 0; i--)
                    {
                        var child = root.transform.GetChild(i);

                        if (child.name.StartsWith("WaterSplinePreview_"))
                        {
                            string[] parts = child.name.Split('_');
                            if (parts.Length > 1 && int.TryParse(parts[1], out int idx))
                            {
                                if (idx >= data.splines.Count) Object.DestroyImmediate(child.gameObject);
                            }
                        }

                        else if (child.name.StartsWith("WaterSector_"))
                        {
                            string[] parts = child.name.Split('_');
                            if (parts.Length > 1 && int.TryParse(parts[1], out int idx))
                            {
                                if (idx >= data.sectors.Count) Object.DestroyImmediate(child.gameObject);
                            }
                        }
                    }
                }
            }

            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < data.splines.Count; i++)
            {
                int id = data.splines[i].riverId;
                if (!groups.ContainsKey(id)) groups[id] = new List<int>();
                groups[id].Add(i);
            }
            foreach (var kv in groups)
            {
                var idxs = kv.Value;
                int g = 0;
                while (g < idxs.Count)
                {
                    int start = idxs[g];
                    int h = g;
                    while (h + 1 < idxs.Count && data.splines[idxs[h + 1]].linkToPrevious) h++;
                    int end = idxs[h];
                    for (int si = start; si <= end; si++)
                    {
                        BuildSingleSplineMesh(data, si);
                    }
                    g = h + 1;
                }
            }

            for (int i = 0; i < data.sectors.Count; i++)
            {
                BuildGridSectorMesh(data, i);
            }
            EditorUtility.SetDirty(data);
            if (root != null)
            {
                var ww = root.GetComponent<WaterWaves>();
                if (ww != null)
                {
                    ww.RefreshRenderers();

                }
            }
            SceneView.RepaintAll();
        }

        public static void RefreshExistingPreviews(WaterCreatorData data)
        {
            if (data == null) return;
            string rootName = $"WaterCreator_Previews_{data.name}";
            var root = GameObject.Find(rootName);
            if (root == null) return;

            for (int i = 0; i < data.splines.Count; i++)
            {
                var s = data.splines[i];
                if (s == null) continue;

                string goName = $"WaterSplinePreview_{i}_{s.name}";
                var goTrans = root.transform.Find(goName);
                if (goTrans == null) continue;

                var mr = goTrans.GetComponent<MeshRenderer>();
                if (mr == null) continue;

                var mpb = new MaterialPropertyBlock();
                mr.GetPropertyBlock(mpb);

                float tilingU = 1f, tilingV = 1f;
                if (s.previewMesh != null)
                {
                    tilingU = (s.previewMesh.bounds.size.magnitude / 1f) * data.uvWorldScaleU;
                    tilingV = (s.width / 1f) * data.uvWorldScaleV;
                }

                if (mr.sharedMaterial == null || (data.previewMaterial != null && mr.sharedMaterial.shader != data.previewMaterial.shader))
                {
                    mr.sharedMaterial = new Material(data.previewMaterial);
                }

                mpb.SetVector("_MainTexTiling", new Vector2(tilingU, tilingV));
                if (s.flowMap != null)
                {
                    mpb.SetTexture("_FlowMap", s.flowMap);
                    mpb.SetFloat("_UVWorldScaleU", data.uvWorldScaleU);
                    mpb.SetFloat("_UVWorldScaleV", data.uvWorldScaleV);
                    mpb.SetFloat("_UseUV", 1f);
                }
                mr.SetPropertyBlock(mpb);
            }

            for (int i = 0; i < data.sectors.Count; i++)
            {
                var s = data.sectors[i];
                string goName = $"WaterSector_{i}_{s.name}";
                var goTrans = root.transform.Find(goName);
                if (goTrans == null) continue;

                var renderers = goTrans.GetComponentsInChildren<MeshRenderer>();
                foreach (var mr in renderers)
                {
                    var mpb = new MaterialPropertyBlock();
                    mr.GetPropertyBlock(mpb);
                    mpb.SetFloat("_UVWorldScaleU", data.uvWorldScaleU);
                    mpb.SetFloat("_UVWorldScaleV", data.uvWorldScaleV);
                    mr.SetPropertyBlock(mpb);
                }
            }

            var ww = root.GetComponent<WaterWaves>();
            if (ww != null) ww.RefreshRenderers();

            SceneView.RepaintAll();
        }

        private static void BuildSingleSplineMesh(WaterCreatorData data, int splineIndex)
        {
            var s = data.splines[splineIndex];
            if (s == null || s.points.Count < 2) return;

            float finalTilingU, finalTilingV, totalLength;
            Vector3 centroid;
            List<Row> rows;

            if (data.adaptiveTessellation)
            {
                Mesh lod0 = GenerateMeshForSpline(data, s, splineIndex, data.tessellationLevel, out finalTilingU, out finalTilingV, out totalLength, out centroid, out rows);
                Mesh lod1 = GenerateMeshForSpline(data, s, splineIndex, data.lod1Tessellation, out _, out _, out _, out _, out _, centroid);
                Mesh lod2 = GenerateMeshForSpline(data, s, splineIndex, data.lod2Tessellation, out _, out _, out _, out _, out _, centroid);

                s.previewMeshes[0] = lod0;
                s.previewMeshes[1] = lod1;
                s.previewMeshes[2] = lod2;
                s.previewMesh = lod0;
            }
            else
            {
                Mesh mesh = GenerateMeshForSpline(data, s, splineIndex, data.tessellationLevel, out finalTilingU, out finalTilingV, out totalLength, out centroid, out rows);
                s.previewMeshes[0] = mesh;
                s.previewMeshes[1] = null;
                s.previewMeshes[2] = null;
                s.previewMesh = mesh;
            }

            CreateOrUpdatePreviewGO_ForSpline(data, s, splineIndex, centroid, finalTilingU, finalTilingV, totalLength);

            Transform probeAnchor = ManageReflectionProbes(data, s, splineIndex, rows, centroid, totalLength);
            if (probeAnchor != null)
            {
                string goName = $"WaterSplinePreview_{splineIndex}_{s.name}";
                var rootGo = GameObject.Find($"WaterCreator_Previews_{data.name}");
                var splineGO = rootGo != null ? rootGo.transform.Find(goName)?.gameObject : null;
                if (splineGO != null)
                {
                    var mr = splineGO.GetComponent<MeshRenderer>();
                    if (mr != null) mr.probeAnchor = probeAnchor;

                    var renderers = splineGO.GetComponentsInChildren<MeshRenderer>();
                    foreach (var r in renderers)
                    {
                        r.probeAnchor = probeAnchor;
                    }
                }
            }
        }

        private static Mesh GenerateMeshForSpline(WaterCreatorData data, WaterSpline s, int splineIndex, int tessellationLevel, out float finalTilingU, out float finalTilingV, out float totalLength, out Vector3 centroid, out List<Row> rows, Vector3? overrideCentroid = null)
        {
            float widthStart = s.width;
            float widthEnd = s.width;
            bool hasNext = (splineIndex + 1 < data.splines.Count) &&
                           data.splines[splineIndex + 1] != null &&
                           data.splines[splineIndex + 1].linkToPrevious;
            if (hasNext) widthEnd = data.splines[splineIndex + 1].width;

            int segments = Mathf.Max(1, s.points.Count - 1);
            int samplesPerSegment = Mathf.Max(1, tessellationLevel);
            int cols = Mathf.Max(8, samplesPerSegment + 1);

            rows = new List<Row>();
            for (int seg = 0; seg < segments; seg++)
            {
                int startSi = (seg == 0) ? 0 : 1;
                for (int si = startSi; si <= samplesPerSegment; si++)
                {
                    float localT = (float)si / (float)samplesPerSegment;
                    Vector3 pos = WaterSplineSampler.SampleSegmentPosition(s, seg, localT);
                    Vector3 tan = WaterSplineSampler.SampleSegmentTangent(s, seg, localT);
                    float w = WaterSplineSampler.SampleSegmentWidth(s, seg, localT);
                    rows.Add(new Row(pos, tan.normalized, w, s.flowSpeed, s.invertNormals));
                }
            }

            if (rows.Count < 2)
            {
                finalTilingU = 1f; finalTilingV = 1f; totalLength = 1f; centroid = Vector3.zero;
                return new Mesh();
            }

            var forwards = new Vector3[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                Vector3 forward = Vector3.zero;
                if (i == 0) forward = rows[i + 1].pos - rows[i].pos;
                else if (i == rows.Count - 1) forward = rows[i].pos - rows[i - 1].pos;
                else forward = (rows[i + 1].pos - rows[i - 1].pos) * 0.5f;
                if (forward.sqrMagnitude < 1e-6f) forward = rows[Mathf.Max(0, i - 1)].tangent;
                forward = Vector3.ProjectOnPlane(forward, Vector3.up);
                if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
                forwards[i] = forward.normalized;
            }
            for (int i = 1; i < forwards.Length; i++)
                if (Vector3.Dot(forwards[i - 1], forwards[i]) < 0f) forwards[i] = -forwards[i];

            var cumLen = new float[rows.Count];
            cumLen[0] = 0f;
            for (int i = 1; i < rows.Count; i++) cumLen[i] = cumLen[i - 1] + Vector3.Distance(rows[i - 1].pos, rows[i].pos);

            totalLength = cumLen[rows.Count - 1];


            if (hasNext && rows.Count > 0)
            {
                float targetW = data.splines[splineIndex + 1].pointWidths.Count > 0 ? data.splines[splineIndex + 1].pointWidths[0] : data.splines[splineIndex + 1].width;

                int bridgeRows = Mathf.Max(1, samplesPerSegment / 2);
                for (int i = 0; i < bridgeRows; i++)
                {
                    int idx = rows.Count - 1 - i;
                    if (idx < 0) break;
                    float t = (float)i / (float)bridgeRows;
                    var r = rows[idx];
                    r.width = Mathf.Lerp(targetW, r.width, t);
                    rows[idx] = r;
                }
            }

            float baseLength = 1f;
            float baseWidth = 1f;
            float autoTilingU = totalLength / baseLength;
            float autoTilingV = s.width / baseWidth;
            finalTilingU = autoTilingU * data.uvWorldScaleU;
            finalTilingV = autoTilingV * data.uvWorldScaleV;

            var verts = new List<Vector3>(rows.Count * cols);
            var uvs = new List<Vector2>(rows.Count * cols);
            var uv2 = new List<Vector4>(rows.Count * cols);
            var uv3 = new List<Vector4>(rows.Count * cols);
            var normals = new List<Vector3>(rows.Count * cols);
            var indices = new List<int>((rows.Count - 1) * (cols - 1) * 6);

            for (int r = 0; r < rows.Count; r++)
            {
                float tWidth = (totalLength > 1e-6f) ? (cumLen[r] / totalLength) : 0f;
                float rowWidth = Mathf.Lerp(widthStart, widthEnd, Mathf.SmoothStep(0f, 1f, tWidth));
                var row = rows[r];

                Vector3 forward = forwards[r];
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
                right.Normalize();
                Vector3 surfNormal = row.invertNormals ? -Vector3.up : Vector3.up;

                Vector3 rowL = row.pos - right * (rowWidth * 0.5f);
                Vector3 rowR = row.pos + right * (rowWidth * 0.5f);

                if (data.snapToRiverbed)
                {


                    Vector3 rayOrigin = row.pos + Vector3.up * 0.1f; 
                    float scanDist = 500f;
                    
                    if (Physics.Raycast(rayOrigin, -right, out RaycastHit hL, scanDist, data.collisionMask)) {
                        rowL = hL.point;
                        Debug.DrawRay(rayOrigin, -right * hL.distance, Color.green);
                    } else {
                        Debug.DrawRay(rayOrigin, -right * 10f, Color.red);
                    }

                    if (Physics.Raycast(rayOrigin, right, out RaycastHit hR, scanDist, data.collisionMask)) {
                        rowR = hR.point;
                        Debug.DrawRay(rayOrigin, right * hR.distance, Color.green);
                    } else {
                        Debug.DrawRay(rayOrigin, right * 10f, Color.red);
                    }

                    if (r == 0) Debug.Log($"[AQUIS Pro] Snapping scan for '{s.name}': L_Hit={rowL}, R_Hit={rowR} (Mask: {data.collisionMask.value})");
                }

                for (int c = 0; c < cols; c++)
                {
                    float t = (float)c / (float)(cols - 1);
                    Vector3 p = Vector3.Lerp(rowL, rowR, t);
                    p.y = row.pos.y;

                    verts.Add(p);
                    normals.Add(surfNormal);
                    float U = (totalLength > 1e-6f) ? (cumLen[r] / totalLength) : 0f;
                    float V = t;
                    uvs.Add(new Vector2(U, V));
                    uv2.Add(new Vector4(forward.x, 0f, row.flow, 0f));
                    uv3.Add(new Vector4(0f, forward.z, 0f, 0f));
                }
            }
            for (int r = 0; r < rows.Count - 1; r++)
            {
                int rowStart = r * cols;
                int nextRowStart = (r + 1) * cols;
                for (int c = 0; c < cols - 1; c++)
                {
                    int i0 = rowStart + c;
                    int i1 = rowStart + c + 1;
                    int i2 = nextRowStart + c;
                    int i3 = nextRowStart + c + 1;
                    indices.Add(i0); indices.Add(i2); indices.Add(i1);
                    indices.Add(i1); indices.Add(i2); indices.Add(i3);
                }
            }
            if (s.invertNormals)
            {
                for (int t = 0; t < indices.Count; t += 3)
                {
                    int a = indices[t];
                    int b = indices[t + 1];
                    int c = indices[t + 2];
                    indices[t] = a; indices[t + 1] = c; indices[t + 2] = b;
                }
            }
            if (overrideCentroid.HasValue)
            {
                centroid = overrideCentroid.Value;
            }
            else
            {
                centroid = Vector3.zero;
                for (int i = 0; i < verts.Count; i++) centroid += verts[i];
                centroid /= Mathf.Max(1, verts.Count);
            }
            for (int i = 0; i < verts.Count; i++) verts[i] -= centroid;

            var mesh = new Mesh();
            mesh.indexFormat = (verts.Count > 65535) ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, uv2);
            mesh.SetUVs(2, uv3);
            mesh.SetNormals(normals);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateBounds();
            Bounds meshBounds = mesh.bounds;
            meshBounds.Expand(new Vector3(0, 50f, 0));
            mesh.bounds = meshBounds;
            mesh.RecalculateTangents();

            return mesh;
        }

        private static Transform ManageReflectionProbes(WaterCreatorData data, WaterSpline s, int splineIndex, List<Row> rows, Vector3 meshCentroid, float meshLength)
        {
            string goName = $"WaterSplinePreview_{splineIndex}_{s.name}";
            var rootGo = GameObject.Find($"WaterCreator_Previews_{data.name}");
            var splineGO = rootGo != null ? rootGo.transform.Find(goName)?.gameObject : null;
            if (splineGO == null) return null;

            Transform probesRoot = splineGO.transform.Find("ReflectionProbes");
            if (probesRoot != null)
            {
                GameObject.DestroyImmediate(probesRoot.gameObject);
            }

            if (data == null || !data.enableReflectionProbes || rows == null || rows.Count < 2) return null;

            GameObject newRoot = new GameObject("ReflectionProbes");
            newRoot.transform.SetParent(splineGO.transform, false);
            newRoot.transform.localPosition = Vector3.zero;

            float spacing = Mathf.Max(5f, data.probeSpacing);
            int probeCount = Mathf.RoundToInt(meshLength / spacing);
            if (probeCount < 1) probeCount = 1;

            float floatStep = (float)(rows.Count - 1) / probeCount;
            Transform centerAnchor = null;
            int centerIdx = probeCount / 2;

            for (int i = 0; i <= probeCount; i++)
            {
                int rIdx = Mathf.Clamp(Mathf.RoundToInt(i * floatStep), 0, rows.Count - 1);
                Row r = rows[rIdx];

                int lateralCount = 1;
                if (data.reflectionProbeLayout == ReflectionProbeLayout.Grid)
                {
                    lateralCount = Mathf.Max(1, Mathf.RoundToInt(r.width / spacing));
                }

                for (int j = 0; j < lateralCount; j++)
                {
                    float lateralT = (lateralCount > 1) ? ((float)j / (lateralCount - 1) - 0.5f) : 0f;
                    Vector3 right = Vector3.Cross(Vector3.up, r.tangent).normalized;
                    if (right.sqrMagnitude < 0.001f) right = Vector3.right;

                    GameObject probeGO = new GameObject($"Probe_{i}_{j}");
                    probeGO.transform.SetParent(newRoot.transform, false);

                    Vector3 pos = r.pos + right * (lateralT * r.width);
                    Vector3 localPos = pos - meshCentroid;
                    localPos.y += data.reflectionProbeVerticalOffset;
                    probeGO.transform.localPosition = localPos;

                    if (r.tangent.sqrMagnitude > 0.001f)
                        probeGO.transform.rotation = Quaternion.LookRotation(r.tangent);

                    var probe = probeGO.AddComponent<ReflectionProbe>();
                    probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
                    probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
                    probe.resolution = (int)data.probeResolution;
                    probe.boxProjection = true;
                    probe.nearClipPlane = 0.01f;

                    float boxLength = Mathf.Max(spacing * 1.25f, 1f);
                    float boxWidth;

                    if (data.reflectionProbeLayout == ReflectionProbeLayout.Grid)
                    {
                        boxWidth = spacing * 1.5f;
                        probe.size = new Vector3(boxWidth, 20f, boxLength);
                    }
                    else
                    {
                        boxWidth = Mathf.Max(r.width * 2f, 10f);
                        probe.size = new Vector3(boxWidth, 20f, boxLength);
                    }

                    probe.center = new Vector3(0f, -1f, 0f);
                    probe.blendDistance = spacing * 0.25f;

                    probe.RenderProbe();

                    if (i == centerIdx && j == (lateralCount / 2))
                        centerAnchor = probeGO.transform;
                }
            }

            return centerAnchor;
        }

        private static GameObject GetOrCreatePreviewRoot(WaterCreatorData data)
        {
            string rootName = data != null ? $"WaterCreator_Previews_{data.name}" : "WaterCreator_Previews";
            var root = FindInactiveByName(rootName);
            if (root == null)
            {
                root = new GameObject(rootName);
            }

            root.hideFlags = HideFlags.DontSaveInBuild;
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            var ww = root.GetComponent<WaterWaves>();
            if (ww == null)
            {
                ww = root.AddComponent<WaterWaves>();
                ww.data = data;
                ww.ConfigureForMeshSize(10f, 5f);
            }
            else
            {
                ww.data = data;
            }

            return root;
        }

        private static void CreateOrUpdatePreviewGO_ForSpline(WaterCreatorData data, WaterSpline s, int splineIndex, Vector3 centroid, float tilingU, float tilingV, float meshLength)
        {
            if (s == null) return;
            var root = GetOrCreatePreviewRoot(data);
            string goName = $"WaterSplinePreview_{splineIndex}_{s.name}";
            var goTrans = root.transform.Find(goName);
            var go = goTrans != null ? goTrans.gameObject : null;

            if (go == null)
            {
                go = new GameObject(goName);
                go.transform.SetParent(root.transform, true);
            }
            go.hideFlags = HideFlags.DontSaveInBuild;

            var rootMf = go.GetComponent<MeshFilter>();
            var rootMr = go.GetComponent<MeshRenderer>();
            var rootMc = go.GetComponent<MeshCollider>();

            if (rootMc == null) rootMc = go.AddComponent<MeshCollider>();
            rootMc.sharedMesh = s.previewMesh;
            rootMc.enabled = UnityEditor.EditorPrefs.GetBool("WaterCreator_PaintMode", false);

            go.transform.position = centroid;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            if (data.adaptiveTessellation && s.previewMeshes[1] != null && s.previewMeshes[2] != null)
            {
                if (rootMf != null) Object.DestroyImmediate(rootMf);
                if (rootMr != null) Object.DestroyImmediate(rootMr);

                var lodGroup = go.GetComponent<LODGroup>();
                if (lodGroup == null) lodGroup = go.AddComponent<LODGroup>();

                LOD[] lods = new LOD[3];
                float[] screenHeights = { data.lod1ScreenHeight, data.lod2ScreenHeight, 0.01f };

                for (int i = 0; i < 3; i++)
                {
                    string childName = $"LOD{i}";
                    var childTrans = go.transform.Find(childName);
                    GameObject childGo = childTrans != null ? childTrans.gameObject : null;
                    if (childGo == null)
                    {
                        childGo = new GameObject(childName);
                        childGo.transform.SetParent(go.transform, false);
                        childGo.transform.localPosition = Vector3.zero;
                        childGo.transform.localRotation = Quaternion.identity;
                        childGo.transform.localScale = Vector3.one;
                        childGo.AddComponent<MeshFilter>();
                        childGo.AddComponent<MeshRenderer>();
                    }
                    childGo.hideFlags = HideFlags.DontSaveInBuild;

                    var mf = childGo.GetComponent<MeshFilter>();
                    var mr = childGo.GetComponent<MeshRenderer>();

                    mf.sharedMesh = s.previewMeshes[i];
                    SetupRenderer(mr, data, s, tilingU, tilingV);

                    lods[i] = new LOD(screenHeights[i], new Renderer[] { mr });
                }

                lodGroup.SetLODs(lods);
                lodGroup.RecalculateBounds();
            }
            else
            {
                var lodGroup = go.GetComponent<LODGroup>();
                if (lodGroup != null) Object.DestroyImmediate(lodGroup);

                for (int i = 0; i < 3; i++)
                {
                    var childTrans = go.transform.Find($"LOD{i}");
                    if (childTrans != null) Object.DestroyImmediate(childTrans.gameObject);
                }

                if (rootMf == null) rootMf = go.AddComponent<MeshFilter>();
                if (rootMr == null) rootMr = go.AddComponent<MeshRenderer>();

                rootMf.sharedMesh = s.previewMesh;
                SetupRenderer(rootMr, data, s, tilingU, tilingV);
            }
        }

        private static void SetupRenderer(MeshRenderer mr, WaterCreatorData data, WaterSpline s, float tilingU, float tilingV)
        {
            if (mr == null) return;

            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;

            if (data != null && data.previewMaterial != null)
            {
                if (mr.sharedMaterial == null || mr.sharedMaterial == data.previewMaterial)
                    mr.sharedMaterial = new Material(data.previewMaterial);
            }
            else
            {
                mr.sharedMaterial = null;
            }

            if (mr.sharedMaterial != null)
            {
                mr.sharedMaterial.SetVector("_MainTexTiling", new Vector2(tilingU, tilingV));
                if (s.flowMap != null)
                {
                    mr.sharedMaterial.SetTexture("_FlowMap", s.flowMap);
                    mr.sharedMaterial.SetFloat("_UVWorldScaleU", (data != null) ? data.uvWorldScaleU : 1f);
                    mr.sharedMaterial.SetFloat("_UVWorldScaleV", (data != null) ? data.uvWorldScaleV : 1f);
                    mr.sharedMaterial.SetFloat("_UseUV", 1f);
                }
            }


            var mpb = new MaterialPropertyBlock();
            mr.GetPropertyBlock(mpb);
            mpb.SetVector("_MainTexTiling", new Vector2(tilingU, tilingV));
            if (s.flowMap != null)
            {
                mpb.SetTexture("_FlowMap", s.flowMap);
                mpb.SetFloat("_UVWorldScaleU", (data != null) ? data.uvWorldScaleU : 1f);
                mpb.SetFloat("_UVWorldScaleV", (data != null) ? data.uvWorldScaleV : 1f);
                mpb.SetFloat("_UseUV", 1f);
            }
            mr.SetPropertyBlock(mpb);
        }

        public static void RefreshAllProbes(WaterCreatorData data = null)
        {
            CleanupAllMismatchedRootsInScene();

            string rootName = data != null ? $"WaterCreator_Previews_{data.name}" : "WaterCreator_Previews";
            var root = FindInactiveByName(rootName);
            if (root == null)
            {
                Debug.LogWarning($"[AQUIS Pro] No {rootName} root found. Generate previews first.");
                return;
            }

            var probes = root.GetComponentsInChildren<ReflectionProbe>(true);
            if (probes.Length == 0)
            {
                Debug.LogWarning("[AQUIS Pro] No reflection probes found. Enable them in Lighting & Reflections and regenerate.");
                return;
            }

            foreach (var probe in probes)
                probe.RenderProbe();

            Debug.Log($"[AQUIS Pro] Refreshed {probes.Length} reflection probe(s).");
        }

        public static void ExportDataSetAsPrefab(WaterCreatorData data)
        {
            if (data == null)
            {
                EditorUtility.DisplayDialog("Export Failed", "Assign a WaterCreatorData asset first.", "OK");
                return;
            }

            string rootName = $"WaterCreator_Previews_{data.name}";
            var root = GameObject.Find(rootName);
            if (root == null)
            {
                EditorUtility.DisplayDialog("Export Failed", $"Could not find the generated water object: {rootName}. Please generate meshes first.", "OK");
                return;
            }

            string dataPath = AssetDatabase.GetAssetPath(data);
            if (string.IsNullOrEmpty(dataPath)) return;

            string folderPath = System.IO.Path.GetDirectoryName(dataPath);
            string defaultPrefabName = $"{data.name}_Prefab";
            string savePath = EditorUtility.SaveFilePanelInProject("Save Prefab", defaultPrefabName, "prefab", "Save the water hierarchy as a Prefab", folderPath);
            if (string.IsNullOrEmpty(savePath)) return;

            ExportDataSetToPath(data, root, savePath, true);
        }

        public static void OverwriteDataSetPrefab(WaterCreatorData data)
        {
            if (data == null || string.IsNullOrEmpty(data.lastExportedPrefabPath)) return;
            string rootName = $"WaterCreator_Previews_{data.name}";
            var root = GameObject.Find(rootName);
            if (root == null)
            {
                EditorUtility.DisplayDialog("Export Failed", $"Could not find the generated water object: {rootName}. Please generate meshes first.", "OK");
                return;
            }
            ExportDataSetToPath(data, root, data.lastExportedPrefabPath, false);
        }

        private static void ExportDataSetToPath(WaterCreatorData data, GameObject root, string savePath, bool showSuccessDialog)
        {
            string assetsFolder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(savePath), System.IO.Path.GetFileNameWithoutExtension(savePath) + "_Assets").Replace("\\", "/");

            var meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
            var meshColliders = root.GetComponentsInChildren<MeshCollider>(true);
            var meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            
            List<Mesh> transientMeshes = new List<Mesh>();
            List<Material> transientMaterials = new List<Material>();

            foreach (var mf in meshFilters)
            {
                if (mf.sharedMesh != null && !AssetDatabase.Contains(mf.sharedMesh) && !transientMeshes.Contains(mf.sharedMesh))
                {
                    transientMeshes.Add(mf.sharedMesh);
                }
            }
            foreach (var mc in meshColliders)
            {
                if (mc.sharedMesh != null && !AssetDatabase.Contains(mc.sharedMesh) && !transientMeshes.Contains(mc.sharedMesh))
                {
                    transientMeshes.Add(mc.sharedMesh);
                }
            }
            foreach (var mr in meshRenderers)
            {
                var mats = mr.sharedMaterials;
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] != null && !AssetDatabase.Contains(mats[m]) && !transientMaterials.Contains(mats[m]))
                    {
                        transientMaterials.Add(mats[m]);
                    }
                }
            }

            if (transientMeshes.Count > 0 || transientMaterials.Count > 0)
            {
                if (!AssetDatabase.IsValidFolder(assetsFolder))
                {
                    string parent = System.IO.Path.GetDirectoryName(assetsFolder).Replace("\\", "/");
                    string newFolderName = System.IO.Path.GetFileName(assetsFolder);
                    AssetDatabase.CreateFolder(parent, newFolderName);
                }

                int i = 0;
                foreach (var mesh in transientMeshes)
                {
                    Mesh savedMesh = Object.Instantiate(mesh);
                    savedMesh.name = $"{data.name}_Mesh_{i}";
                    string assetPath = $"{assetsFolder}/{savedMesh.name}.asset";
                    
                    if (AssetDatabase.LoadAssetAtPath<Object>(assetPath) != null)
                        AssetDatabase.DeleteAsset(assetPath);
                        
                    AssetDatabase.CreateAsset(savedMesh, assetPath);
                    
                    foreach (var mf in meshFilters)
                    {
                        if (mf.sharedMesh == mesh) mf.sharedMesh = savedMesh;
                    }
                    foreach (var mc in meshColliders)
                    {
                        if (mc.sharedMesh == mesh) mc.sharedMesh = savedMesh;
                    }
                    i++;
                }

                int j = 0;
                foreach (var mat in transientMaterials)
                {
                    Material savedMat = Object.Instantiate(mat);
                    savedMat.name = $"{data.name}_Material_{j}";
                    string assetPath = $"{assetsFolder}/{savedMat.name}.mat";
                    
                    if (AssetDatabase.LoadAssetAtPath<Object>(assetPath) != null)
                        AssetDatabase.DeleteAsset(assetPath);
                        
                    AssetDatabase.CreateAsset(savedMat, assetPath);
                    
                    foreach (var mr in meshRenderers)
                    {
                        var mats = mr.sharedMaterials;
                        bool changed = false;
                        for (int k = 0; k < mats.Length; k++)
                        {
                            if (mats[k] == mat)
                            {
                                mats[k] = savedMat;
                                changed = true;
                            }
                        }
                        if (changed) mr.sharedMaterials = mats;
                    }
                    j++;
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            var allTransforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in allTransforms)
            {
                t.gameObject.hideFlags = HideFlags.None;
            }

            GameObject prefabAsset = PrefabUtility.SaveAsPrefabAssetAndConnect(root, savePath, InteractionMode.UserAction);
            if (prefabAsset != null)
            {
                data.lastExportedPrefabPath = savePath;
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();

                if (showSuccessDialog)
                {
                    EditorUtility.DisplayDialog("Export Success", $"Prefab exported successfully to: {savePath}", "OK");
                    EditorGUIUtility.PingObject(prefabAsset);
                }
                else
                {
                    Debug.Log($"[AQUIS Pro] Successfully overwrote prefab at {savePath}");
                }
            }
            else
            {
                EditorUtility.DisplayDialog("Export Failed", "Failed to save prefab.", "OK");
            }
        }

        public static void SaveSelectedPreviewMesh(WaterCreatorData data, int selectedSplineIndex)
        {
            if (data == null)
            {
                EditorUtility.DisplayDialog("No data", "Assign a WaterCreatorData asset first.", "OK");
                return;
            }
            if (selectedSplineIndex < 0 || selectedSplineIndex >= data.splines.Count)
            {
                EditorUtility.DisplayDialog("No spline selected", "Select a spline in the list.", "OK");
                return;
            }
            var s = data.splines[selectedSplineIndex];
            Mesh meshToSave = s.previewMesh;
            if (meshToSave == null)
            {
                int start = FindChainStart(data, selectedSplineIndex);
                int end = FindChainEnd(data, start);
                string goName = (end > start) ? $"WaterSplineChainPreview_{start}_{end}" : $"WaterSplinePreview_{selectedSplineIndex}_{s.name}";
                var rootGo = FindInactiveByName($"WaterCreator_Previews_{data.name}");
                var go = rootGo != null ? rootGo.transform.Find(goName)?.gameObject : null;
                if (go != null)
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null) meshToSave = mf.sharedMesh;
                }
            }
            if (meshToSave == null)
            {
                EditorUtility.DisplayDialog("No preview mesh", "No preview mesh found for the selected spline. Generate previews first.", "OK");
                return;
            }
            string defaultName = s != null ? $"{s.name}_mesh" : "WaterMesh";
            string path = EditorUtility.SaveFilePanelInProject("Save Mesh", defaultName, "asset", "Save generated mesh to project");
            if (string.IsNullOrEmpty(path)) return;
            Mesh assetMesh = Object.Instantiate(meshToSave);
            assetMesh.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(assetMesh, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Saved", $"Mesh saved to: {path}", "OK");
            EditorGUIUtility.PingObject(assetMesh);
        }

        public static int FindChainStart(WaterCreatorData data, int idx)
        {
            if (data == null) return idx;
            while (idx > 0 && data.splines[idx].linkToPrevious)
            {
                idx--;
            }
            return idx;
        }

        public static int FindChainEnd(WaterCreatorData data, int startIdx)
        {
            int end = startIdx;
            while (end + 1 < data.splines.Count && data.splines[end + 1].linkToPrevious)
            {
                end++;
            }
            return end;
        }

        public static void BuildGridSectorMesh(WaterCreatorData data, int idx)
        {
            if (data == null || idx < 0 || idx >= data.sectors.Count) return;
            var gd = data.sectors[idx];
            
            string rootName = $"WaterCreator_Previews_{data.name}";
            var root = FindOrCreateRoot(rootName);
            
            string sectorGroupName = $"WaterSector_{idx}_{gd.name}";
            Transform sectorGroup = root.transform.Find(sectorGroupName);
            if (sectorGroup != null) Object.DestroyImmediate(sectorGroup.gameObject);
            
            GameObject sectorGroupGO = new GameObject(sectorGroupName);
            sectorGroupGO.transform.SetParent(root.transform, false);
            sectorGroupGO.transform.localPosition = gd.position;
            
            float secW = gd.width / Mathf.Max(1, gd.sectorCountX);
            float secL = gd.length / Mathf.Max(1, gd.sectorCountZ);
            
            for (int x = 0; x < gd.sectorCountX; x++)
            {
                for (int z = 0; z < gd.sectorCountZ; z++)
                {
                    string subName = $"Sector_{x}_{z}";
                    GameObject subGO = new GameObject(subName);
                    subGO.transform.SetParent(sectorGroupGO.transform, false);
                    subGO.transform.localPosition = new Vector3(
                        (x - gd.sectorCountX * 0.5f + 0.5f) * secW,
                        0f,
                        (z - gd.sectorCountZ * 0.5f + 0.5f) * secL
                    );
                    
                    GameObject sharedProbeGO = null;
                    if (data.enableReflectionProbes)
                    {
                        sharedProbeGO = SetupSectorProbe(subGO.transform, data, secW, secL);
                    }

                    if (data.adaptiveTessellation)
                    {
                        var lodGroup = subGO.AddComponent<LODGroup>();
                        LOD[] lods = new LOD[3];
                        float[] screenHeights = { data.lod1ScreenHeight, data.lod2ScreenHeight, 0.01f };
                        int[] subsPerLod = { gd.subdivisions, data.lod1Tessellation, data.lod2Tessellation };

                        for (int i = 0; i < 3; i++)
                        {
                            string lodName = $"LOD{i}";
                            GameObject lodGO = new GameObject(lodName);
                            lodGO.transform.SetParent(subGO.transform, false);
                            lodGO.transform.localPosition = Vector3.zero;

                            Mesh mesh = CreateGridMesh(secW, secL, subsPerLod[i], gd.position + subGO.transform.localPosition, data.uvWorldScaleU, data.uvWorldScaleV);
                            mesh.name = $"{subName}_{lodName}";

                            var mf = lodGO.AddComponent<MeshFilter>();
                            mf.sharedMesh = mesh;
                            var mr = lodGO.AddComponent<MeshRenderer>();
                            mr.sharedMaterial = data.previewMaterial;

                            if (sharedProbeGO != null)
                            {
                                mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;
                                mr.probeAnchor = sharedProbeGO.transform;
                            }

                            lods[i] = new LOD(screenHeights[i], new Renderer[] { mr });
                        }
                        lodGroup.SetLODs(lods);
                        lodGroup.RecalculateBounds();
                    }
                    else
                    {
                        Mesh mesh = CreateGridMesh(secW, secL, gd.subdivisions, gd.position + subGO.transform.localPosition, data.uvWorldScaleU, data.uvWorldScaleV);
                        mesh.name = subName;

                        var mf = subGO.AddComponent<MeshFilter>();
                        mf.sharedMesh = mesh;
                        var mr = subGO.AddComponent<MeshRenderer>();
                        mr.sharedMaterial = data.previewMaterial;

                        if (sharedProbeGO != null)
                        {
                            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;
                            mr.probeAnchor = sharedProbeGO.transform;
                        }
                    }
                }
            }
        }

        private static GameObject SetupSectorProbe(Transform parent, WaterCreatorData data, float secW, float secL)
        {
            GameObject probeGO = new GameObject("ReflectionProbe");
            probeGO.transform.SetParent(parent, false);
            probeGO.transform.localPosition = Vector3.up * data.reflectionProbeVerticalOffset;

            var probe = probeGO.AddComponent<ReflectionProbe>();
            probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
            probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
            probe.resolution = (int)data.probeResolution;
            probe.size = new Vector3(secW * 1.5f, 20f, secL * 1.5f);
            probe.boxProjection = true;
            probe.nearClipPlane = 0.01f;
            probe.renderDynamicObjects = true;

            probe.RenderProbe();
            return probeGO;
        }

        private static Mesh CreateGridMesh(float w, float l, int subs, Vector3 worldOffset, float uvU, float uvV)
        {
            Mesh mesh = new Mesh();
            int res = subs + 1;
            Vector3[] verts = new Vector3[res * res];
            Vector2[] uvs = new Vector2[res * res];
            int[] tris = new int[subs * subs * 6];
            
            float stepX = w / subs;
            float stepZ = l / subs;
            
            for (int i = 0; i < res; i++)
            {
                for (int j = 0; j < res; j++)
                {
                    float lx = (i - subs * 0.5f) * stepX;
                    float lz = (j - subs * 0.5f) * stepZ;
                    verts[i * res + j] = new Vector3(lx, 0, lz);

                    float ux = (worldOffset.x + lx) * uvU;
                    float uz = (worldOffset.z + lz) * uvV;
                    uvs[i * res + j] = new Vector2(ux, uz);
                }
            }
            
            int ti = 0;
            for (int i = 0; i < subs; i++)
            {
                for (int j = 0; j < subs; j++)
                {
                    tris[ti++] = i * res + j;
                    tris[ti++] = i * res + j + 1;
                    tris[ti++] = (i + 1) * res + j;
                    
                    tris[ti++] = (i + 1) * res + j;
                    tris[ti++] = i * res + j + 1;
                    tris[ti++] = (i + 1) * res + j + 1;
                }
            }
            
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static GameObject FindOrCreateRoot(string name)
        {
            var r = FindInactiveByName(name);
            if (r == null)
            {
                r = new GameObject(name);
                r.AddComponent<WaterWaves>();
            }
            return r;
        }

        public static GameObject FindInactiveByName(string name)
        {

            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            foreach (var go in all)
            {

                if (go.name == name && go.scene == UnityEngine.SceneManagement.SceneManager.GetActiveScene()) 
                    return go;
            }
            return null;
        }
        public static void CleanupAllMismatchedRootsInScene()
        {
            string activeScenePath = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path;
            string activeSceneGUID = AssetDatabase.AssetPathToGUID(activeScenePath);

            var roots = GameObject.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var root in roots)
            {
                if (root.name.StartsWith("WaterCreator_Previews_"))
                {
                    string dataName = root.name.Substring("WaterCreator_Previews_".Length);

                    string[] guids = AssetDatabase.FindAssets(dataName + " t:WaterCreatorData");
                    bool authorized = false;
                    foreach (string guid in guids)
                    {
                        var d = AssetDatabase.LoadAssetAtPath<WaterCreatorData>(AssetDatabase.GUIDToAssetPath(guid));
                        if (d != null && d.name == dataName)
                        {
                            if (d.sceneGUIDs != null && d.sceneGUIDs.Contains(activeSceneGUID))
                            {
                                authorized = true;
                                break;
                            }
                        }
                    }

                    if (!authorized)
                    {
                        Object.DestroyImmediate(root);
                    }
                }
            }
        }
    }
}
#endif
