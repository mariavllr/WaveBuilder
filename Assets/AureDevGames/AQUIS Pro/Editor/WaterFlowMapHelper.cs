
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static AureDevGames.WaterCreatorData;
using static AureDevGames.WaterSpline;

namespace AureDevGames
{
    public static class WaterFlowMapHelper
    {


public static void DrawFlowMapSettings(WaterCreatorTool tool)
    {
        var data = tool.GetData();
        EditorGUILayout.LabelField("Flow Map Generation", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;
        data.flowMapMode = (FlowMapMode)EditorGUILayout.EnumPopup("Mode", data.flowMapMode);
        if (data.flowMapMode == FlowMapMode.Automatic)
        {
            data.flowMapWidth          = EditorGUILayout.IntField(new GUIContent("FlowMap Width (U)",    "Horizontal resolution of the flow map texture. Higher preserves better details on very long rivers."), data.flowMapWidth);
            data.flowMapHeight         = EditorGUILayout.IntField(new GUIContent("FlowMap Height (V)",   "Vertical resolution. Usually lower than width since rivers are narrow."), data.flowMapHeight);
            data.slopeInfluence        = EditorGUILayout.Slider(new GUIContent("Slope Influence",       "How much falling down a slope accelerates the water current."), data.slopeInfluence, 0f, 4f);
            data.centerSpeedMultiplier = EditorGUILayout.Slider(new GUIContent("Center Speed Mult.",    "Speed of the water current at the deepest mathematical center of the spline."), data.centerSpeedMultiplier, 0.1f, 4f);
            data.edgeSpeedMultiplier   = EditorGUILayout.Slider(new GUIContent("Edge Speed Mult.",      "Friction effect: speed of the water at the shores."), data.edgeSpeedMultiplier, 0f, 1f);
            data.lateralFalloffPower   = EditorGUILayout.Slider(new GUIContent("Lateral Falloff Power", "Transition sharpness from shore to center. High values squeeze the fast current to the very middle."), data.lateralFalloffPower, 0.5f, 4f);

            if (GUILayout.Button("Generate / Update Flow Maps"))
            {
                GenerateOrUpdateFlowMaps(data, tool);
            }
        }
        if (data.flowMapMode == FlowMapMode.Custom)
        {
            var brushSize = tool.GetBrushSize();
            var brushStrength = tool.GetBrushStrength();
            brushSize = EditorGUILayout.Slider(new GUIContent("Brush Size", "Radius of the Custom Paint brush."), brushSize, 1f, 50f);
            brushStrength = EditorGUILayout.Slider(new GUIContent("Target Flow Speed", "Defines the specific speed parameter applied over the painted area."), brushStrength, 0f, 1f);
            tool.SetBrushSize(brushSize);
            tool.SetBrushStrength(brushStrength);

            EditorGUILayout.Space();
            bool paintActive = EditorPrefs.GetBool("WaterCreator_PaintMode", false);
            GUI.backgroundColor = paintActive ? new Color(0.3f, 0.9f, 0.4f) : Color.white;
            if (GUILayout.Button(paintActive ? "\u1F58C  PAINTING  (click to stop)" : "[Click to Enable Paint Mode]", GUILayout.Height(30)))
            {
                paintActive = !paintActive;
                EditorPrefs.SetBool("WaterCreator_PaintMode", paintActive);
                if (data != null) {
                    var rootGo = GameObject.Find("WaterCreator_Previews_" + data.name);
                    if (rootGo != null) {
                        var colliders = rootGo.GetComponentsInChildren<MeshCollider>(true);
                        foreach (var c in colliders) c.enabled = paintActive;
                    }
                }
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = Color.white;
            if (paintActive)
                EditorGUILayout.HelpBox("Paint Mode Active: click and drag over the water mesh in Scene View to paint flow direction.", MessageType.Info);
        }

        EditorGUILayout.Space();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Reset Selected Flow Map"))
        {
            if (data != null && tool.GetSelectedSplineIndex() >= 0 && tool.GetSelectedSplineIndex() < data.splines.Count)
            {
                ResetFlowMapForSpline(data, tool.GetSelectedSplineIndex());
                tool.QueueGenerateAllPreviewsPublic();
            }
            else EditorUtility.DisplayDialog("Reset Flow Map", "Please select a spline first.", "OK");
        }
        if (GUILayout.Button("Reset Dataset Flow Maps"))
        {
            if (EditorUtility.DisplayDialog("Reset Dataset", "Reset all flow maps in this Data Asset? This will overwrite existing files in the FlowMaps folder.", "Yes", "No"))
            {
                for (int i = 0; i < data.splines.Count; i++) ResetFlowMapForSpline(data, i);
                tool.QueueGenerateAllPreviewsPublic();
            }
        }
        EditorGUILayout.EndHorizontal();
        EditorGUI.indentLevel--;
    }

    private static void ResetFlowMapForSpline(WaterCreatorData data, int splineIndex)
    {
        if (data == null || splineIndex < 0 || splineIndex >= data.splines.Count) return;
        var s = data.splines[splineIndex];
        int w = Mathf.Max(1, (data != null) ? data.flowMapWidth : 256);
        int h = Mathf.Max(1, (data != null) ? data.flowMapHeight : 128);
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        Color[] cols = new Color[w * h];
        for (int i = 0; i < cols.Length; i++) cols[i] = new Color(0.5f, 0.5f, 0f, 1f);
        tex.SetPixels(cols);
        tex.Apply();
        SaveFlowMapAsset(tex, data, splineIndex);
        string goName = $"WaterSplinePreview_{splineIndex}_{s.name}";
        var go = GameObject.Find(goName);
        if (go != null)
        {
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                var mpb = new MaterialPropertyBlock();
                mr.GetPropertyBlock(mpb);
                mpb.SetTexture("_FlowMap", data.splines[splineIndex].flowMap);
                mpb.SetFloat("_UVWorldScaleU", data.uvWorldScaleU);
                mpb.SetFloat("_UVWorldScaleV", data.uvWorldScaleV);
                mr.SetPropertyBlock(mpb);
            }
        }
        EditorUtility.SetDirty(data);
    }

    private static void GenerateOrUpdateFlowMaps(WaterCreatorData data, WaterCreatorTool tool)
    {
        if (data == null)
        {
            EditorUtility.DisplayDialog("No data", "Assign a WaterCreatorData asset first.", "OK");
            return;
        }


        for (int i = 0; i < data.splines.Count; i++)
        {
            Texture2D tex = GenerateFlowMapForChain(data, i, i, data.flowMapWidth, data.flowMapHeight);
            if (tex != null)
            {
                data.splines[i].flowMap = tex;
            }
        }
        
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        
        WaterMeshHelper.RefreshExistingPreviews(data);
    }

    private static Texture2D GenerateFlowMapForChain(
        WaterCreatorData data,
        int startIdx, int endIdx,
        int texWidth,  int texHeight)
    {
        if (data == null) return null;
        if (startIdx < 0 || endIdx >= data.splines.Count || startIdx > endIdx) return null;
        var rawRows = new List<WaterMeshHelper.Row>();
        int samplesPerSegment = Mathf.Max(1, data.tessellationLevel);
        for (int si = startIdx; si <= endIdx; si++)
        {
            var spline = data.splines[si];
            if (spline == null || spline.points == null || spline.points.Count < 2) continue;
            int segCount = Mathf.Max(1, spline.points.Count - 1);
            int samplesThisSpline = segCount * samplesPerSegment + 1;
            for (int k = 0; k < samplesThisSpline; k++)
            {
                int seg = Mathf.Min(segCount - 1, k / samplesPerSegment);
                float localT = (k == samplesThisSpline - 1) ? 1f : (float)(k % samplesPerSegment) / (float)samplesPerSegment;
                if (k == 0 && si > startIdx && spline.linkToPrevious)
                    continue;
                Vector3 pos = WaterSplineSampler.SampleSegmentPosition(spline, seg, localT);
                Vector3 tan = WaterSplineSampler.SampleSegmentTangent(spline, seg, localT);
                rawRows.Add(new WaterMeshHelper.Row(pos, tan.normalized, spline.width, spline.flowSpeed, spline.invertNormals));
            }
        }
        if (rawRows.Count < 2) return null;
        const float MERGE_EPS = 1e-4f;
        var rows = new List<WaterMeshHelper.Row>();
        rows.Add(rawRows[0]);
        for (int i = 1; i < rawRows.Count; i++)
        {
            var prev = rows[rows.Count - 1];
            var cur = rawRows[i];
            if (Vector3.Distance(prev.pos, cur.pos) <= MERGE_EPS)
            {
                Vector3 avgT = (prev.tangent + cur.tangent) * 0.5f;
                if (avgT.sqrMagnitude < 1e-6f) avgT = prev.tangent;
                avgT.Normalize();
                float avgW = Mathf.Lerp(prev.width, cur.width, 0.5f);
                float avgF = Mathf.Lerp(prev.flow, cur.flow, 0.5f);
                bool inv = prev.invertNormals || cur.invertNormals;
                rows[rows.Count - 1] = new WaterMeshHelper.Row(prev.pos, avgT, avgW, avgF, inv);
            }
            else rows.Add(cur);
        }
        if (rows.Count < 2) return null;
        var forwards = new Vector3[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            Vector3 f;
            if (i == 0) f = rows[1].pos - rows[0].pos;
            else if (i == rows.Count - 1) f = rows[i].pos - rows[i - 1].pos;
            else f = (rows[i + 1].pos - rows[i - 1].pos) * 0.5f;
            if (f.sqrMagnitude < 1e-6f) f = rows[i].tangent;
            f = Vector3.ProjectOnPlane(f, Vector3.up);
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            forwards[i] = f.normalized;
        }
        for (int i = 1; i < forwards.Length; i++)
            if (Vector3.Dot(forwards[i - 1], forwards[i]) < 0f) forwards[i] = -forwards[i];
        var cumLen = new List<float>(rows.Count);
        float accumLen = 0f;
        cumLen.Add(0f);
        for (int i = 1; i < rows.Count; i++)
        {
            accumLen += Vector3.Distance(rows[i - 1].pos, rows[i].pos);
            cumLen.Add(accumLen);
        }
        float totalLen = Mathf.Max(1e-6f, cumLen[cumLen.Count - 1]);


        Texture2D tex = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false, true);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        var pixels = new Color[texWidth * texHeight];
        for (int u = 0; u < texWidth; u++)
        {
            float uNorm = (texWidth == 1) ? 0f : (float)u / (float)(texWidth - 1);
            float targetDist = uNorm * totalLen;
            int rowIdx = 0;
            for (int r = 0; r < cumLen.Count - 1; r++)
            {
                if (targetDist >= cumLen[r] && targetDist <= cumLen[r + 1])
                {
                    float mid = (cumLen[r] + cumLen[r + 1]) * 0.5f;
                    rowIdx = (targetDist <= mid) ? r : r + 1;
                    break;
                }
                if (r == cumLen.Count - 2 && targetDist > cumLen[r + 1]) rowIdx = r + 1;
            }
            Vector3 fwd = forwards[rowIdx];
            Vector3 pos = rows[rowIdx].pos;
            float baseSpeed = rows[rowIdx].flow;
            float minSlopeFactor = 0.3f;
            float maxSlopeFactor = 3.0f;
            int rn = Mathf.Min(rowIdx + 1, rows.Count - 1);
            float deltaY = rows[rn].pos.y - rows[rowIdx].pos.y;
            float distSegment = Mathf.Max(1e-6f, Vector3.Distance(rows[rn].pos, rows[rowIdx].pos));
            float slope = deltaY / distSegment;
            float slopeRaw = 1f - slope * data.slopeInfluence;
            slopeRaw = Mathf.Clamp(slopeRaw, minSlopeFactor, maxSlopeFactor);

            Vector3 fwdN = fwd.normalized;
            Vector3 rgt  = Vector3.Cross(Vector3.up, fwdN).normalized;
            if (rows[rowIdx].invertNormals) rgt = -rgt;




            float riverbedSpeedMult  = 1f;
            float riverbedLateralBias = 0f;
            if (data.snapToRiverbed)
            {
                float nominalWidth = rows[rowIdx].width;
                Vector3 rayOrigin  = pos + Vector3.up * 0.1f;
                float   scanDist   = 500f;
                float leftDist     = nominalWidth * 0.5f;
                float rightDist    = nominalWidth * 0.5f;
                if (Physics.Raycast(rayOrigin, -rgt, out RaycastHit hL, scanDist, data.collisionMask))
                    leftDist  = hL.distance;
                if (Physics.Raycast(rayOrigin,  rgt, out RaycastHit hR, scanDist, data.collisionMask))
                    rightDist = hR.distance;

                float actualWidth = Mathf.Max(0.01f, leftDist + rightDist);

                riverbedSpeedMult  = Mathf.Clamp(nominalWidth / actualWidth, 0.25f, 4f);


                riverbedLateralBias = Mathf.Clamp((rightDist - leftDist) / actualWidth, -1f, 1f);
            }


            for (int v = 0; v < texHeight; v++)
            {
                float vNorm = (texHeight == 1) ? 0.5f : (float)v / (float)(texHeight - 1);




                Vector2 fwdN2D  = new Vector2(fwdN.x, fwdN.z).normalized;
                Vector2 rgtN2D  = new Vector2(rgt.x,  rgt.z).normalized;
                Vector2 finalDir2D = fwdN2D;
                if (finalDir2D.sqrMagnitude < 1e-6f) finalDir2D = new Vector2(0f, 1f);

                float lateral = 1f - Mathf.Abs(vNorm - 0.5f) * 2f;
                lateral = Mathf.Pow(Mathf.Clamp01(lateral), data.lateralFalloffPower);
                float center = data.centerSpeedMultiplier * lateral;
                float edge = data.edgeSpeedMultiplier * (1f - lateral);

                float speed = baseSpeed * slopeRaw * Mathf.Clamp01(center + edge) * riverbedSpeedMult;


                Vector2 riverbedAdjDir = finalDir2D;
                if (Mathf.Abs(riverbedLateralBias) > 0.01f)
                {
                    Vector2 biasVec = (riverbedLateralBias * rgtN2D).normalized;
                    riverbedAdjDir = Vector2.Lerp(finalDir2D, (finalDir2D + biasVec * 0.5f).normalized, Mathf.Abs(riverbedLateralBias) * 0.6f).normalized;
                    if (riverbedAdjDir.sqrMagnitude < 1e-6f) riverbedAdjDir = finalDir2D;
                }

                float alongSpline  = (fwdN2D.sqrMagnitude > 1e-6f) ? Vector2.Dot(riverbedAdjDir, fwdN2D) : riverbedAdjDir.y;
                float acrossSpline = (rgtN2D.sqrMagnitude > 1e-6f) ? Vector2.Dot(riverbedAdjDir, rgtN2D) : riverbedAdjDir.x;
                pixels[v * texWidth + u] = new Color(
                    -alongSpline  * 0.5f + 0.5f,
                    -acrossSpline * 0.5f + 0.5f,
                    Mathf.Clamp01(speed),
                    1f);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        string dirPath = GetFlowMapDirectory(data);
        string fname = $"Flow_Spline_{startIdx}.png";
        string path = System.IO.Path.Combine(dirPath, fname);
        byte[] png = tex.EncodeToPNG();
        System.IO.File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti != null)
        {
            ti.textureType = TextureImporterType.Default;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.mipmapEnabled = false;
            try { ti.sRGBTexture = false; } catch { }
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.isReadable = true;
#if UNITY_2019_1_OR_NEWER
            var platformSettings = ti.GetDefaultPlatformTextureSettings();
            platformSettings.overridden = true;
            platformSettings.format = TextureImporterFormat.RGBA32;
            ti.SetPlatformTextureSettings(platformSettings);
#endif
            ti.SaveAndReimport();
        }
        Texture2D imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (imported == null)
        {
            Debug.LogWarning("[AQUIS Pro] Failed to import generated flow texture at: " + path);
            return tex;
        }
        return imported;
    }

    public static void HandlePainting(WaterCreatorTool tool, SceneView sv)
    {

        if (!EditorPrefs.GetBool("WaterCreator_PaintMode", false)) return;
        var data = tool.GetData();
        if (data == null || data.flowMapMode != FlowMapMode.Custom) return;
        if (data != null && tool.GetSelectedSplineIndex() >= 0)
        {
            var s = data.splines[tool.GetSelectedSplineIndex()];
            if (s == null) return;
            if (s.flowMap == null)
            {
                int w = Mathf.Max(1, data.flowMapWidth);
                int h = Mathf.Max(1, data.flowMapHeight);
                var newTex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                Color[] cols = new Color[w * h];
                for (int i = 0; i < cols.Length; i++) cols[i] = new Color(0.5f, 0.5f, 0f, 1f);
                newTex.SetPixels(cols);
                newTex.Apply();
                SaveFlowMapAsset(newTex, data, tool.GetSelectedSplineIndex());
                EditorUtility.SetDirty(data);
            }
            Event e = Event.current;
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            RaycastHit[] hits = Physics.RaycastAll(ray);
            RaycastHit hit = default;
            bool foundSpline = false;
            float minDist = float.MaxValue;

            foreach (var h in hits)
            {
                if (h.collider != null && (h.collider.gameObject.name.Contains("WaterSplinePreview") || h.collider.gameObject.name.Contains("WaterSplineChainPreview")))
                {
                    if (h.distance < minDist)
                    {
                        minDist = h.distance;
                        hit = h;
                        foundSpline = true;
                    }
                }
            }

            if (foundSpline)
            {

float splineLength = 0f;
                    int numSegments = Mathf.Max(1, s.points.Count - 1);
                    for (int i=0; i<numSegments; i++) {
                        Vector3 lastP = WaterSplineSampler.SampleSegmentPosition(s, i, 0f);
                        for (float t=0.1f; t<=1.0f; t+=0.1f) {
                            Vector3 curP = WaterSplineSampler.SampleSegmentPosition(s, i, t);
                            splineLength += Vector3.Distance(lastP, curP);
                            lastP = curP;
                        }
                    }
                    if (splineLength < 0.1f) splineLength = 0.1f;

                    float _texH = (s.flowMap != null) ? (float)s.flowMap.height : (float)data.flowMapHeight;
                    float actualPixelRadius = Mathf.Clamp(tool.GetBrushSize(), 1f, _texH * 0.5f);
                    float handleRadiusWorld = Mathf.Max(0.05f, (actualPixelRadius / _texH) * s.width);
                    
                    float radiusUV_V = handleRadiusWorld / s.width;
                    float radiusUV_U = handleRadiusWorld / splineLength;

                    Handles.color = new Color(0.1f, 0.8f, 0.9f, 0.7f);
                    Handles.DrawWireDisc(hit.point, hit.normal, handleRadiusWorld);
                    float small = HandleUtility.GetHandleSize(hit.point) * 0.03f;
                    Handles.SphereHandleCap(0, hit.point, Quaternion.identity, small, EventType.Repaint);
                    Vector2 uv = hit.textureCoord;
                    if (e.type == EventType.MouseDown && e.button == 0)
                    {
                        tool.SetLastMouseUV(uv);
                        tool.SetLastPaintWorldPos(hit.point);
                        tool.SetLastPaintTime((float)EditorApplication.timeSinceStartup);
                        var selectedSplineIndex = tool.GetSelectedSplineIndex();
                        var runtimeFlowCopies = tool.GetRuntimeFlowCopies();
                        if (!runtimeFlowCopies.ContainsKey(selectedSplineIndex) || runtimeFlowCopies[selectedSplineIndex] == null)
                        {
                            Texture2D baseTex = s.flowMap;
                            Texture2D runtime = null;
                            if (baseTex != null)
                            {
                                runtime = new Texture2D(baseTex.width, baseTex.height, TextureFormat.RGBA32, false, true);
                                var pixels = baseTex.GetPixels();
                                runtime.SetPixels(pixels);
                                runtime.Apply();
                            }
                            else
                            {
                                int w = Mathf.Max(1, data.flowMapWidth);
                                int h = Mathf.Max(1, data.flowMapHeight);
                                runtime = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                                Color[] cols = new Color[w * h];
                                for (int i = 0; i < cols.Length; i++) cols[i] = new Color(0.5f, 0.5f, 0f, 1f);
                                runtime.SetPixels(cols);
                                runtime.Apply();
                            }
                            runtimeFlowCopies[selectedSplineIndex] = runtime;
                            var goName = $"WaterSplinePreview_{selectedSplineIndex}_{s.name}";
                            var go = GameObject.Find(goName);
                            if (go != null)
                            {
                                var mr = go.GetComponent<MeshRenderer>();
                                if (mr != null)
                                {
                                    var mpb = new MaterialPropertyBlock();
                                    mr.GetPropertyBlock(mpb);
                                    mpb.SetTexture("_FlowMap", runtime);
                                    mpb.SetFloat("_UVWorldScaleU", data.uvWorldScaleU);
                                    mpb.SetFloat("_UVWorldScaleV", data.uvWorldScaleV);
                                    mr.SetPropertyBlock(mpb);
                                }
                            }
                        }
                        e.Use();
                    }
                    else if (e.type == EventType.MouseDrag && e.button == 0)
                    {
                        Vector2 curUV = uv;
                        Vector3 curWorld = hit.point;
                        if (tool.GetLastMouseUV() == Vector2.zero || tool.GetLastPaintWorldPos() == Vector3.positiveInfinity)
                        {
                            tool.SetLastMouseUV(curUV);
                            tool.SetLastPaintWorldPos(curWorld);
                        }
                        Vector3 worldDelta = curWorld - tool.GetLastPaintWorldPos();
                        Vector3 projected = Vector3.ProjectOnPlane(worldDelta, hit.normal);
                        Vector2 dir2D;
                        if (projected.sqrMagnitude < 1e-6f)
                        {
                            Vector2 uvDelta = curUV - tool.GetLastMouseUV();
                            dir2D = (uvDelta.sqrMagnitude < 1e-6f) ? new Vector2(0f, 1f) : uvDelta.normalized;
                        }
                        else
                        {
                            dir2D = new Vector2(projected.x, projected.z).normalized;
                            if (dir2D.sqrMagnitude < 1e-6f) dir2D = new Vector2(0f, 1f);
                        }


                        float totalU = uv.x * Mathf.Max(1, s.points.Count - 1);
                        int segIdx = Mathf.Clamp(Mathf.FloorToInt(totalU), 0, Mathf.Max(0, s.points.Count - 2));
                        float localT = totalU - (float)segIdx;
                        Vector3 tangent = WaterSplineSampler.SampleSegmentTangent(s, segIdx, localT);
                        Vector3 right = Vector3.Cross(Vector3.up, tangent).normalized;
                        if (s.invertNormals) right = -right;

                        Vector2 fwdN2D = new Vector2(tangent.x, tangent.z).normalized;
                        Vector2 rgtN2D = new Vector2(right.x, right.z).normalized;


                        float alongSpline = (fwdN2D.sqrMagnitude > 1e-6f) ? Vector2.Dot(dir2D, fwdN2D) : dir2D.y;
                        float acrossSpline = (rgtN2D.sqrMagnitude > 1e-6f) ? Vector2.Dot(dir2D, rgtN2D) : dir2D.x;
                        
                        Vector2 localDir = new Vector2(alongSpline, acrossSpline);
                        var selectedSplineIndex = tool.GetSelectedSplineIndex();
                        var runtimeFlowCopies = tool.GetRuntimeFlowCopies();
                        Texture2D runtime = runtimeFlowCopies.ContainsKey(selectedSplineIndex) ? runtimeFlowCopies[selectedSplineIndex] : null;
                        if (runtime == null)
                        {
                            Texture2D baseTex = s.flowMap;
                            if (baseTex != null)
                            {
                                runtime = new Texture2D(baseTex.width, baseTex.height, TextureFormat.RGBA32, false, true);
                                runtime.SetPixels(baseTex.GetPixels());
                                runtime.Apply();
                                runtimeFlowCopies[selectedSplineIndex] = runtime;
                            }
                        }
                        if (runtime != null)
                        {
                            float distUV = Vector2.Distance(tool.GetLastMouseUV(), curUV);
                            int stamps = Mathf.Max(1, Mathf.CeilToInt(distUV * Mathf.Max(runtime.width, runtime.height) * 2f));
                            for (int si = 0; si <= stamps; si++)
                            {
                                float t = (stamps == 0) ? 0f : (float)si / (float)stamps;
                                Vector2 stampUV = Vector2.Lerp(tool.GetLastMouseUV(), curUV, t);
                                PaintFlowStamp(runtime, stampUV, localDir, radiusUV_U, radiusUV_V, tool.GetBrushStrength());
                            }
                            runtime.Apply();
                            var goName = $"WaterSplinePreview_{selectedSplineIndex}_{s.name}";
                            var rootGo = GameObject.Find($"WaterCreator_Previews_{data.name}");
                            var go = rootGo != null ? rootGo.transform.Find(goName)?.gameObject : GameObject.Find(goName);
                            if (go != null)
                            {
                                var mr = go.GetComponent<MeshRenderer>();
                                if (mr != null)
                                {
                                    var mpb = new MaterialPropertyBlock();
                                    mr.GetPropertyBlock(mpb);
                                    mpb.SetTexture("_FlowMap", runtime);
                                    mpb.SetFloat("_UVWorldScaleU", data.uvWorldScaleU);
                                    mpb.SetFloat("_UVWorldScaleV", data.uvWorldScaleV);
                                    mr.SetPropertyBlock(mpb);
                                }
                            }
                        }
                        SceneView.RepaintAll();
                        tool.SetLastMouseUV(curUV);
                        tool.SetLastPaintWorldPos(curWorld);
                        tool.SetLastPaintTime((float)EditorApplication.timeSinceStartup);
                        e.Use();
                    }
                    else if (e.type == EventType.MouseUp && e.button == 0)
                    {
                        var selectedSplineIndex = tool.GetSelectedSplineIndex();
                        var runtimeFlowCopies = tool.GetRuntimeFlowCopies();
                        if (runtimeFlowCopies.ContainsKey(selectedSplineIndex) && runtimeFlowCopies[selectedSplineIndex] != null)
                        {
                            Texture2D runtime = runtimeFlowCopies[selectedSplineIndex];
                            Undo.RegisterCompleteObjectUndo(data, "Paint Flow Map");
                            if (s.flowMap != null)
                                Undo.RegisterCompleteObjectUndo(s.flowMap, "Paint Flow Map");
                            SaveFlowMapAsset(runtime, data, selectedSplineIndex);
                            EditorUtility.SetDirty(data);
                            AssetDatabase.SaveAssets();
                            Object.DestroyImmediate(runtime);
                            runtimeFlowCopies.Remove(selectedSplineIndex);
                            var goName = $"WaterSplinePreview_{selectedSplineIndex}_{s.name}";
                            var go = GameObject.Find(goName);
                            if (go != null)
                            {
                                var mr = go.GetComponent<MeshRenderer>();
                                if (mr != null)
                                {
                                    var mpb = new MaterialPropertyBlock();
                                    mr.GetPropertyBlock(mpb);
                                    if (data.splines[selectedSplineIndex].flowMap != null)
                                    {
                                        mpb.SetTexture("_FlowMap", data.splines[selectedSplineIndex].flowMap);
                                        mpb.SetFloat("_UVWorldScaleU", data.uvWorldScaleU);
                                        mpb.SetFloat("_UVWorldScaleV", data.uvWorldScaleV);
                                    }
                                    mr.SetPropertyBlock(mpb);
                                }
                            }
                            tool.QueueGenerateAllPreviewsPublic();
                        }
                        tool.SetLastMouseUV(Vector2.zero);
                        tool.SetLastPaintWorldPos(Vector3.positiveInfinity);
                        tool.SetLastPaintTime(0f);
                        e.Use();
                    }
            }
        }
    }

    private static void ApplyFlowMapToPreview(WaterCreatorData data, int splineIndex, WaterSpline s, Texture2D tex)
    {
        if (tex == null) return;
        string goName = $"WaterSplinePreview_{splineIndex}_{s.name}";
        var    rootGo = GameObject.Find($"WaterCreator_Previews_{data.name}");
        var    go     = rootGo != null ? rootGo.transform.Find(goName)?.gameObject : GameObject.Find(goName);
        if (go == null) return;
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;
        var mpb = new MaterialPropertyBlock();
        mr.GetPropertyBlock(mpb);
        mpb.SetTexture("_FlowMap",     tex);
        mpb.SetFloat("_UVWorldScaleU", data.uvWorldScaleU);
        mpb.SetFloat("_UVWorldScaleV", data.uvWorldScaleV);
        mr.SetPropertyBlock(mpb);
    }

    private static void PaintFlowStamp(Texture2D tex, Vector2 uv, Vector2 dir, float radiusU, float radiusV, float strength)
    {
        if (tex == null || radiusU <= 0f || radiusV <= 0f) return;
        int cx = Mathf.RoundToInt(Mathf.Clamp01(uv.x) * (tex.width - 1));
        int cy = Mathf.RoundToInt(Mathf.Clamp01(uv.y) * (tex.height - 1));
        
        int rx = Mathf.CeilToInt(radiusU * tex.width);
        int ry = Mathf.CeilToInt(radiusV * tex.height);
        
        int xmin = Mathf.Clamp(cx - rx, 0, tex.width - 1);
        int xmax = Mathf.Clamp(cx + rx, 0, tex.width - 1);
        int ymin = Mathf.Clamp(cy - ry, 0, tex.height - 1);
        int ymax = Mathf.Clamp(cy + ry, 0, tex.height - 1);
        
        Vector2 direction = dir;
        if (direction.sqrMagnitude < 1e-6f) direction = new Vector2(0f, 1f);
        direction.Normalize();
        Vector2 encDir = new Vector2(-direction.x * 0.5f + 0.5f, -direction.y * 0.5f + 0.5f);
        
        for (int y = ymin; y <= ymax; y++)
        {
            for (int x = xmin; x <= xmax; x++)
            {
                float du = Mathf.Abs((float)(x - cx) / tex.width);
                float dv = Mathf.Abs((float)(y - cy) / tex.height);
                float dist = Mathf.Sqrt((du * du) / (radiusU * radiusU) + (dv * dv) / (radiusV * radiusV));
                
                if (dist > 1f) continue;
                
                float falloff = 1f - dist;
                float blend = 0.3f * falloff;
                Color cur = tex.GetPixel(x, y);
                cur.r = Mathf.Lerp(cur.r, encDir.x, blend);
                cur.g = Mathf.Lerp(cur.g, encDir.y, blend);
                cur.b = Mathf.Lerp(cur.b, strength, blend);
                tex.SetPixel(x, y, cur);
            }
        }
    }

    private static void SaveFlowMapAsset(Texture2D tex, WaterCreatorData data, int splineIndex)
    {
        if (tex == null) return;
        string dir = GetFlowMapDirectory(data);
        string fname = $"Flow_Spline_{splineIndex}.png";
        string path = System.IO.Path.Combine(dir, fname);
        byte[] png = tex.EncodeToPNG();
        System.IO.File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti != null)
        {
            ti.textureType = TextureImporterType.Default;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.mipmapEnabled = false;
            try { ti.sRGBTexture = false; } catch { }
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.isReadable = true;
#if UNITY_2019_1_OR_NEWER
            var platformSettings = ti.GetDefaultPlatformTextureSettings();
            platformSettings.overridden = true;
            platformSettings.format = TextureImporterFormat.RGBA32;
            ti.SetPlatformTextureSettings(platformSettings);
#endif
            ti.SaveAndReimport();
        }
        Texture2D imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (imported != null && data != null && splineIndex >= 0 && splineIndex < data.splines.Count)
        {
            data.splines[splineIndex].flowMap = imported;
            EditorUtility.SetDirty(data);
        }
    }
    
    private static string GetFlowMapDirectory(WaterCreatorData data)
    {
        if (data == null) return "Assets/WaterFlowMaps";
        string assetPath = AssetDatabase.GetAssetPath(data);
        if (string.IsNullOrEmpty(assetPath)) return "Assets/WaterFlowMaps";
        
        string assetDir = System.IO.Path.GetDirectoryName(assetPath);
        string targetDir = System.IO.Path.Combine(assetDir, "FlowMaps_" + data.name);
        
        if (!System.IO.Directory.Exists(targetDir))
        {
            System.IO.Directory.CreateDirectory(targetDir);
            AssetDatabase.Refresh();
        }
        return targetDir;
    }
}
}
#endif

