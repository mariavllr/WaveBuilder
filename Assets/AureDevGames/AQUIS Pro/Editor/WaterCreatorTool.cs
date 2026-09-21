using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using static AureDevGames.WaterSpline;

namespace AureDevGames
{
    [InitializeOnLoad]
    public class WaterCreatorTool : EditorWindow
    {
        static WaterCreatorTool()
        {
            EditorApplication.delayCall += () => {
                RefreshAllDataPersistence();
            };
        }

    private static void RefreshAllDataPersistence()
    {
        string[] guids = AssetDatabase.FindAssets("t:WaterCreatorData");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var data = AssetDatabase.LoadAssetAtPath<WaterCreatorData>(path);
            if (data != null)
            {

                string rootName = $"WaterCreator_Previews_{data.name}";
                if (WaterMeshHelper.FindInactiveByName(rootName) == null)
                {
                    WaterMeshHelper.GenerateAllPreviews(data);
                }
            }
        }
    }

    [SerializeField] private WaterCreatorData data;
    [SerializeField] private int selectedSplineIndex = -1;
    [SerializeField] private Vector2 globalScroll = Vector2.zero;
    [SerializeField] private int selectedMode = 0;
    [SerializeField] private int selectedSectorIndex = -1;
    [SerializeField] private string renameString = "";
    private int prevTessellationLevel = -1;
    private enum RegenRequest { None, RefreshVisuals, RebuildMeshes }
    private RegenRequest currentRegenRequest = RegenRequest.None;
    private bool pendingForceFullCleanup = false;
    private bool regenQueued = false;
    private Quaternion m_HandleRotation = Quaternion.identity;
    private Vector3 m_HandleScale = Vector3.one;
    private Vector2 lastMouseUV;
    private Vector3 lastPaintWorldPos = Vector3.positiveInfinity;
    private float lastPaintTime = 0f;
    private float brushSize = 10f;
    private float brushStrength = 0.5f;
    private EntityId lastActiveParentID = -1;

    private bool drawMode = false;
    private Vector3 ghostPoint = Vector3.zero;
    private bool ghostValid = false;
    private bool drawModeHasAnchor = false;
    private int lastSplineCount = 0;
    private int lastSectorCount = 0;
    private Vector3 drawAnchor = Vector3.zero;
    private float drawModeYOffset = 0.1f;
    private bool drawModeIsDraggingCurve = false;
    private int  drawModeDragSegmentIndex = -1;
    private bool drawModeDragEndpoint = false;
    private bool drawModeDragCurvature = false;
    private bool drawModeChainSmoothing = true;

    private Dictionary<int, Texture2D> runtimeFlowCopies = new Dictionary<int, Texture2D>();

    [MenuItem("Tools/AQUIS Pro")]
    public static void ShowWindow()
    {
        var w = GetWindow<WaterCreatorTool>("AQUIS Pro");
        w.minSize = new Vector2(420, 500);
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        Undo.undoRedoPerformed += OnUndoRedo;
        drawMode = EditorPrefs.GetBool("WaterCreator_DrawMode", false);
        drawModeChainSmoothing = EditorPrefs.GetBool("WaterCreator_ChainSmoothing", true);
        Tools.hidden = drawMode;
        
        WaterMeshHelper.CleanupAllMismatchedRootsInScene();
        
        prevTessellationLevel = (data != null) ? data.tessellationLevel : -1;
        if (data != null)
        {
            foreach (var s in data.splines) s.EnsureHandleCounts();
            renameString = data.name;
            lastSplineCount = data.splines.Count;
            lastSectorCount = data.sectors.Count;
        }
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        Undo.undoRedoPerformed -= OnUndoRedo;
        Tools.hidden = false;
        drawMode = false;
        drawModeDragEndpoint = false;
        drawModeDragCurvature = false;
        drawModeIsDraggingCurve = false;
        EditorPrefs.SetBool("WaterCreator_DrawMode", false);
        if (runtimeFlowCopies != null)
        {
            foreach (var kv in runtimeFlowCopies)
            {
                if (kv.Value != null) Object.DestroyImmediate(kv.Value);
            }
            runtimeFlowCopies.Clear();
        }
    }

    private void OnUndoRedo()
    {
        if (drawMode && data != null)
        {
            if (data.splines.Count > 0)
            {
                var last = data.splines[data.splines.Count - 1];
                if (last?.points != null && last.points.Count > 0)
                {
                    drawAnchor          = last.points[last.points.Count - 1];
                    drawModeHasAnchor   = true;
                    selectedSplineIndex = data.splines.Count - 1;
                }
            }
            else
            {
                drawModeHasAnchor = false;
                drawAnchor        = Vector3.zero;
                selectedSplineIndex = -1;
            }
    
            drawModeIsDraggingCurve  = false;
            drawModeDragEndpoint     = false;
            drawModeDragCurvature    = false;
            drawModeDragSegmentIndex = -1;
        }

        if (data != null && (data.splines.Count != lastSplineCount || data.sectors.Count != lastSectorCount))
        {
            QueueGenerateAllPreviews(true);
        }
        else
        {
            QueueRefreshPreviews();
        }
    }

    private void OnSelectionChange()
    {
        WaterMeshHelper.CleanupAllMismatchedRootsInScene();

        if (Selection.activeGameObject != null)
        {
            string goName = Selection.activeGameObject.name;
            
            if (goName.StartsWith("WaterSplinePreview_"))
            {
                Transform parent = Selection.activeGameObject.transform.parent;
                if (parent != null && parent.name.StartsWith("WaterCreator_Previews_"))
                {
                    string dataName = parent.name.Substring("WaterCreator_Previews_".Length);
                    
                    if (data == null || data.name != dataName)
                    {
                        string[] guids = AssetDatabase.FindAssets("t:WaterCreatorData");
                        foreach (string guid in guids)
                        {
                            string path = AssetDatabase.GUIDToAssetPath(guid);
                            var d = AssetDatabase.LoadAssetAtPath<WaterCreatorData>(path);
                            if (d != null && d.name == dataName) { data = d; break; }
                        }
                    }
                    
                    string[] parts = goName.Split(new char[] { '_' }, 3);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int index))
                    {
                        if (data != null && index < data.splines.Count)
                        {
                            selectedSplineIndex = index;
                            Repaint();
                        }
                    }
                }
            }
            else if (goName.StartsWith("WaterCreator_Previews_"))
            {
                string dataName = goName.Substring("WaterCreator_Previews_".Length);
                if (data == null || data.name != dataName)
                {
                    string[] guids = AssetDatabase.FindAssets("t:WaterCreatorData");
                    foreach (string guid in guids)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        var d = AssetDatabase.LoadAssetAtPath<WaterCreatorData>(path);
                        if (d != null && d.name == dataName) { data = d; break; }
                    }
                }

if (data != null && Selection.activeGameObject.GetEntityId() != lastActiveParentID)
                {
                    lastActiveParentID = Selection.activeGameObject.GetEntityId();
                    selectedSplineIndex = -1;
                    
                    Vector3 centroid = Vector3.zero;
                    int totalPoints = 0;
                    foreach (var s in data.splines)
                    {
                        if (s.points == null) continue;
                        foreach (var p in s.points) { centroid += p; totalPoints++; }
                    }
                    if (totalPoints > 0)
                    {
                        centroid /= totalPoints;
                        m_HandleRotation = Quaternion.identity;
                        m_HandleScale = Vector3.one;
                        Tools.hidden = true;
                        SceneView.RepaintAll();
                    }
                }
                Repaint();
            }
            else
            {
                if (lastActiveParentID != -1)
                {
                    lastActiveParentID = -1;
                    if (!drawMode) Tools.hidden = false;
                }
            }
        }
        else if (Selection.activeObject is WaterCreatorData dObj) 
        {
            if (data != dObj)
            {
                data = dObj;
                selectedSplineIndex = -1;
                if (data != null) renameString = data.name;
                Repaint();
            }
        }
        else
        {
            if (lastActiveParentID != -1)
            {
                lastActiveParentID = -1;
                if (!drawMode) Tools.hidden = false;
            }
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.Space();
        globalScroll = EditorGUILayout.BeginScrollView(globalScroll);
        EditorGUILayout.LabelField("AQUIS Pro (CPU only)", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        var newData = (WaterCreatorData)EditorGUILayout.ObjectField("Data Asset", data, typeof(WaterCreatorData), false);
        if (data != null)
        {
            GUI.backgroundColor = new Color(1f, 0.7f, 0.7f);
            if (GUILayout.Button("Remove", GUILayout.Width(60)))
            {
                if (EditorUtility.DisplayDialog("Delete Water Data Asset?", 
                    $"Are you sure you want to PERMANENTLY delete '{data.name}' and all its splines from the project?\n\nThis cannot be undone.", 
                    "Yes, Delete Asset", "Cancel"))
                {

                    WaterMeshHelper.GenerateAllPreviews(null);
                    var root = WaterMeshHelper.FindInactiveByName($"WaterCreator_Previews_{data.name}");
                    if (root != null) Object.DestroyImmediate(root);

                    string path = AssetDatabase.GetAssetPath(data);
                    AssetDatabase.DeleteAsset(path);
                    data = null;
                    AssetDatabase.Refresh();
                }
            }
            GUI.backgroundColor = Color.white;
        }
        EditorGUILayout.EndHorizontal();

        if (newData != data)
        {
            data = newData;
            if (data != null) 
            {
                foreach (var s in data.splines) s.EnsureHandleCounts();
                renameString = data.name;
            }
        }

        if (data != null)
        {
            if (data.sceneGUIDs == null) data.sceneGUIDs = new List<string>();
            string currentScenePath = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path;
            string currentSceneGUID = AssetDatabase.AssetPathToGUID(currentScenePath);
            bool isBound = data.sceneGUIDs.Contains(currentSceneGUID);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Scene Binding", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("This asset is bound to " + data.sceneGUIDs.Count + " scene(s).");
            
            EditorGUILayout.BeginHorizontal();
            if (isBound)
            {
                GUI.backgroundColor = new Color(0.7f, 1f, 0.7f);
                EditorGUILayout.LabelField("✓ COMPATIBLE: Bound to this scene", EditorStyles.miniBoldLabel);
                GUI.backgroundColor = Color.white;
                if (GUILayout.Button("Unbind Scene", GUILayout.Width(100)))
                {
                    Undo.RecordObject(data, "Unbind Scene");
                    data.sceneGUIDs.Remove(currentSceneGUID);
                    EditorUtility.SetDirty(data);
                    QueueGenerateAllPreviews(true);
                }
            }
            else
            {
                GUI.backgroundColor = new Color(1f, 0.7f, 0.7f);
                EditorGUILayout.LabelField("⚠ UNBOUND: Not visible in this scene", EditorStyles.miniBoldLabel);
                GUI.backgroundColor = Color.white;
                if (GUILayout.Button("Bind Scene", GUILayout.Width(100)))
                {
                    Undo.RecordObject(data, "Bind Scene");
                    data.sceneGUIDs.Add(currentSceneGUID);
                    EditorUtility.SetDirty(data);
                    QueueGenerateAllPreviews();
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            if (!isBound)
            {
                EditorGUILayout.HelpBox("Select another asset or Bind this one to see the water.", MessageType.Info);
                GUI.enabled = false;
            }

            EditorGUILayout.BeginHorizontal();
            renameString = EditorGUILayout.TextField("New Asset Name", renameString);
            if (GUILayout.Button("Rename Asset", GUILayout.Width(100)))
            {
                if (!string.IsNullOrEmpty(renameString) && renameString != data.name)
                {
                    string oldName = data.name;
                    string path = AssetDatabase.GetAssetPath(data);
                    string result = AssetDatabase.RenameAsset(path, renameString);
                    if (string.IsNullOrEmpty(result))
                    {
                        AssetDatabase.SaveAssets();
                        AssetDatabase.Refresh();

                        string oldRootName = $"WaterCreator_Previews_{oldName}";
                        var oldRoot = WaterMeshHelper.FindInactiveByName(oldRootName);
                        if (oldRoot != null)
                        {
                            oldRoot.name = $"WaterCreator_Previews_{data.name}";
                        }

                        renameString = data.name;
                    }
                    else
                    {
                        Debug.LogError($"[AQUIS Pro] Rename failed: {result}");
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Create New Dataset"))
        {
            string path = EditorUtility.SaveFilePanelInProject("Create WaterCreatorData", "WaterCreatorData", "asset", "Create data asset");
            if (!string.IsNullOrEmpty(path))
            {
                var nd = ScriptableObject.CreateInstance<WaterCreatorData>();
                AssetDatabase.CreateAsset(nd, path);
                AssetDatabase.SaveAssets();
                data = nd;
                selectedSplineIndex = -1;
                Selection.activeObject = data;
            }
        }
        GUI.enabled = (data != null);
        if (GUILayout.Button("Duplicate Dataset"))
        {
            if (data != null)
            {
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();

                string path = AssetDatabase.GetAssetPath(data);
                if (!string.IsNullOrEmpty(path))
                {
                    string newPath = AssetDatabase.GenerateUniqueAssetPath(path);
                    var dCopy = Instantiate(data);
                    AssetDatabase.CreateAsset(dCopy, newPath);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    
                    data = dCopy;
                    selectedSplineIndex = -1;
                    Selection.activeObject = data;
                    QueueGenerateAllPreviews();
                }
            }
        }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        if (data == null)
        {
            EditorGUILayout.HelpBox("Welcome to AQUIS Pro.\\nCreate or assign a WaterCreatorData ScriptableObject to begin.", MessageType.Info);
        }
        else
        {
            EditorGUILayout.Space();
            DrawGeneralSettings();
            if (selectedMode == 0)
            {
                EditorGUILayout.Space();
                DrawLightingSettings();
                EditorGUILayout.Space();
                WaterFlowMapHelper.DrawFlowMapSettings(this);
            }
            EditorGUILayout.Space();
            
            selectedMode = GUILayout.Toolbar(selectedMode, new string[] { "Splines (Rivers)", "Sectors (Oceans)" });
            EditorGUILayout.Space();

            if (selectedMode == 0)
            {
                DrawSplineList();
            }
            else
            {
                DrawSectorList();
            }
            
            if (selectedMode == 0)
            {
                EditorGUILayout.Space();
                DrawMaterialSyncSettings();
            }
            
            if (GUI.changed)
            {
                if (selectedMode == 0 && selectedSplineIndex >= 0 && selectedSplineIndex < data.splines.Count)
                {
                    data.splines[selectedSplineIndex].EnsureHandleCounts();
                }
                EditorUtility.SetDirty(data);
                QueueGenerateAllPreviews();
            }
            
            EditorGUILayout.Space();
            if (GUILayout.Button("Regenerate All Meshes & Probes"))
            {
                QueueGenerateAllPreviews();
            }
            
            EditorGUILayout.Space();
            GUI.backgroundColor = new Color(0.7f, 1f, 0.7f);

            bool canOverwrite = !string.IsNullOrEmpty(data.lastExportedPrefabPath);
            if (canOverwrite)
            {
                var existingPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(data.lastExportedPrefabPath);
                if (existingPrefab == null)
                {
                    data.lastExportedPrefabPath = "";
                    UnityEditor.EditorUtility.SetDirty(data);
                    canOverwrite = false;
                }
            }

            if (canOverwrite)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("Overwrite Prefab", $"Overwrites the previously exported prefab at:\n{data.lastExportedPrefabPath}"), GUILayout.Height(30)))
                {
                    WaterMeshHelper.OverwriteDataSetPrefab(data);
                }
                
                GUI.backgroundColor = new Color(0.85f, 0.95f, 0.85f);
                if (GUILayout.Button(new GUIContent("Export As...", "Export to a new location."), GUILayout.Width(100), GUILayout.Height(30)))
                {
                    WaterMeshHelper.ExportDataSetAsPrefab(data);
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                if (GUILayout.Button(new GUIContent("Export selected Data Set as Prefab", "Exports the generated water hierarchy as a Prefab, saving all generated meshes as assets."), GUILayout.Height(30)))
                {
                    WaterMeshHelper.ExportDataSetAsPrefab(data);
                }
            }
            
            GUI.backgroundColor = Color.white;

            GUI.enabled = true;
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawGeneralSettings()
    {
        EditorGUILayout.LabelField("General Settings", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;
        
        EditorGUI.BeginChangeCheck();
        if (data.collisionMask.value == 0) data.collisionMask.value = -1;
        
        Material newPreviewMat = (Material)EditorGUILayout.ObjectField(new GUIContent("Preview Material", "The material applied to preview splines in the scene."), data.previewMaterial, typeof(Material), false);
        
        int newTessLevel = data.tessellationLevel;
        if (selectedMode == 0)
        {
            newTessLevel = EditorGUILayout.IntSlider(new GUIContent("Tessellation Level (samples)", "How many subdivisions each spline segment gets. Higher = smoother river, but more polygons."), data.tessellationLevel, 1, 2048);
        }
        bool newAdaptiveTess = EditorGUILayout.Toggle(new GUIContent("Adaptive Tessellation", "Adaptatively tessellate mesh using LODGroups based on distance."), data.adaptiveTessellation);
        
        
        int newLod1Tess = data.lod1Tessellation;
        int newLod2Tess = data.lod2Tessellation;
        float newLod1Screen = data.lod1ScreenHeight;
        float newLod2Screen = data.lod2ScreenHeight;
 
        if (newAdaptiveTess)
        {
            EditorGUI.indentLevel++;
            newLod1Tess = EditorGUILayout.IntSlider(new GUIContent("LOD 1 Tessellation", "Tessellation level for medium distance."), data.lod1Tessellation, 1, 2048);
            newLod1Screen = EditorGUILayout.Slider(new GUIContent("LOD 1 Screen Height", "Screen size transition ratio for LOD 1. Lower = further away."), data.lod1ScreenHeight, 0.01f, 1f);
            
            newLod2Tess = EditorGUILayout.IntSlider(new GUIContent("LOD 2 Tessellation", "Tessellation level for far distance."), data.lod2Tessellation, 1, 2048);
            newLod2Screen = EditorGUILayout.Slider(new GUIContent("LOD 2 Screen Height", "Screen size transition ratio for LOD 2."), data.lod2ScreenHeight, 0.01f, 1f);
            EditorGUI.indentLevel--;
        }

        
        EditorGUILayout.LabelField("Texture Tiling Multipliers", EditorStyles.miniLabel);
        float newUvu = EditorGUILayout.Slider(new GUIContent("U Tiling Multiplier", "Scale the texture along the river length."), data.uvWorldScaleU, 0.1f, 10f);
        float newUvv = EditorGUILayout.Slider(new GUIContent("V Tiling Multiplier", "Scale the texture across the river width."), data.uvWorldScaleV, 0.1f, 10f);
        
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(data, "Change General Settings");
            bool geometryChanged = (data.tessellationLevel != newTessLevel) || (data.adaptiveTessellation != newAdaptiveTess) || (data.lod1Tessellation != newLod1Tess) || (data.lod2Tessellation != newLod2Tess);
            
            data.previewMaterial = newPreviewMat;
            data.tessellationLevel = newTessLevel;
            data.adaptiveTessellation = newAdaptiveTess;
            
            data.lod1Tessellation = newLod1Tess;
            data.lod2Tessellation = newLod2Tess;
            data.lod1ScreenHeight = newLod1Screen;
            data.lod2ScreenHeight = newLod2Screen;

            data.uvWorldScaleU = newUvu;
            data.uvWorldScaleV = newUvv;
            EditorUtility.SetDirty(data);
            
            if (geometryChanged) QueueGenerateAllPreviews();
            else QueueRefreshPreviews();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Adaptive Riverbed Snapping (Experimental)", EditorStyles.miniBoldLabel);
        EditorGUI.BeginChangeCheck();
        bool newSnap = EditorGUILayout.Toggle(new GUIContent("Snap Mesh to Riverbed", "If enabled, the water mesh will automatically expand sideways to hit any collider in the Collision Mask. Perfect for fitting water to terrain banks."), data.snapToRiverbed);

        List<string> namedLayersList = new List<string>();
        List<int> layerIndices = new List<int>();
        for (int i = 0; i < 32; i++) {
            string n = LayerMask.LayerToName(i);
            if (!string.IsNullOrEmpty(n)) {
                namedLayersList.Add(n);
                layerIndices.Add(i);
            }
        }
        int conciseMask = 0;
        for (int i = 0; i < layerIndices.Count; i++) {
            if ((data.collisionMask.value & (1 << layerIndices[i])) != 0) conciseMask |= (1 << i);
        }
        int newConciseMask = EditorGUILayout.MaskField("Collision Mask", conciseMask, namedLayersList.ToArray());

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(data, "Change Snapping Settings");
            data.snapToRiverbed = newSnap;
            int finalMask = 0;
            for (int i = 0; i < layerIndices.Count; i++) {
                if ((newConciseMask & (1 << i)) != 0) finalMask |= (1 << layerIndices[i]);
            }
            data.collisionMask.value = finalMask;
            EditorUtility.SetDirty(data);
            QueueGenerateAllPreviews();
        }
        
        EditorGUILayout.HelpBox("Tiling is automatically calculated based on spline perimeter. Modifiers act as a final scale.", MessageType.Info);
        
        if (data != null && data.tessellationLevel != prevTessellationLevel)
        {
            prevTessellationLevel = data.tessellationLevel;
            QueueGenerateAllPreviews();
        }
        EditorGUI.indentLevel--;
    }

    private void DrawLightingSettings()
    {
        EditorGUILayout.LabelField("Lighting & Reflections", EditorStyles.boldLabel);
        EditorGUI.indentLevel++;
        
        bool prevEnable = data.enableReflectionProbes;
        float prevSpacing = data.probeSpacing;
        var prevRes = data.probeResolution;
        var prevLayout = data.reflectionProbeLayout;
        float prevOffset = data.reflectionProbeVerticalOffset;

        EditorGUI.BeginChangeCheck();
        bool newEnable = EditorGUILayout.Toggle(new GUIContent("Enable Reflection Probes", "Generate local reflection probes mimicking the environment along the river."), data.enableReflectionProbes);
        
        float newSpacing = prevSpacing;
        WaterCreatorData.ProbeResolution newRes = prevRes;
        WaterCreatorData.ReflectionProbeLayout newLayout = prevLayout;
        float newOffset = prevOffset;

        if (newEnable)
        {
            newSpacing = EditorGUILayout.Slider(new GUIContent("Probe Spacing (m)", "Distance between each reflection probe. Small spacing equals more accuracy but more calculation overhead."), data.probeSpacing, 5f, 500f);
            newRes = (WaterCreatorData.ProbeResolution)EditorGUILayout.EnumPopup(new GUIContent("Probe Resolution", "Resolution per-probe cubemap."), data.probeResolution);
            newLayout = (WaterCreatorData.ReflectionProbeLayout)EditorGUILayout.EnumPopup(new GUIContent("Probe Layout", "Longitudinal = center only, Grid = covers entire width."), data.reflectionProbeLayout);
            newOffset = EditorGUILayout.Slider(new GUIContent("Probe Vertical Offset", "Vertical offset above the water. Increase this if you see artifacts from terrain/ground."), data.reflectionProbeVerticalOffset, -50f, 50f);

            EditorGUI.indentLevel--;
            if (GUILayout.Button(new GUIContent("Refresh Probes", "Re-renders all reflection probes. Use this after adding or moving objects in the scene so they appear in the water reflections.")))
            {
                WaterMeshHelper.RefreshAllProbes(data);
            }
            EditorGUI.indentLevel++;
        }

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(data, "Change Lighting Settings");
            bool layoutChanged = (data.enableReflectionProbes != newEnable) || (data.probeSpacing != newSpacing) || (data.reflectionProbeLayout != newLayout) || (data.reflectionProbeVerticalOffset != newOffset);
            
            data.enableReflectionProbes = newEnable;
            data.probeSpacing = newSpacing;
            data.probeResolution = (WaterCreatorData.ProbeResolution)newRes;
            data.reflectionProbeLayout = newLayout;
            data.reflectionProbeVerticalOffset = newOffset;
            EditorUtility.SetDirty(data);

            if (layoutChanged) QueueGenerateAllPreviews();
            else WaterMeshHelper.RefreshAllProbes(data);
        }

        if (data != null && (data.enableReflectionProbes != prevEnable || data.probeSpacing != prevSpacing || data.probeResolution != prevRes || data.reflectionProbeLayout != prevLayout || data.reflectionProbeVerticalOffset != prevOffset))
        {
            QueueGenerateAllPreviews();
        }

        EditorGUI.indentLevel--;
    }

    private void DrawSplineList()
    {
        EditorGUILayout.LabelField("Splines", EditorStyles.boldLabel);

        var prevBgColor = GUI.backgroundColor;
        GUI.backgroundColor = drawMode ? new Color(0.3f, 1f, 0.4f, 1f) : new Color(0.85f, 0.85f, 0.85f, 1f);
        string drawBtnLabel = drawMode
            ? "\u270f  Draw Mode ON  \u2014  Each click creates a new linked river segment"
            : "\u270f  Draw Mode  \u2014  Click to draw river segments directly in the Scene";
        if (GUILayout.Button(drawBtnLabel, GUILayout.Height(26)))
        {
            drawMode = !drawMode;
            EditorPrefs.SetBool("WaterCreator_DrawMode", drawMode);
            Tools.hidden = drawMode;
            if (drawMode)
            {

                if (data != null && data.splines.Count > 0)
                {
                    var last = data.splines[data.splines.Count - 1];
                    if (last.points.Count > 0)
                    {
                        drawAnchor = last.points[last.points.Count - 1];
                        drawModeHasAnchor = true;
                    }
                }
            }
            else
            {
                drawModeHasAnchor = false;
                ghostValid = false;
            }
            SceneView.RepaintAll();
            Repaint();
        }
        GUI.backgroundColor = prevBgColor;
        
        if (drawMode)
        {
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Collision Mask (Shared)", EditorStyles.miniBoldLabel);

            List<string> namedLayersList = new List<string>();
            List<int> layerIndices = new List<int>();
            for (int i = 0; i < 32; i++) {
                string n = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(n)) {
                    namedLayersList.Add(n);
                    layerIndices.Add(i);
                }
            }
            int conciseMask = 0;
            for (int i = 0; i < layerIndices.Count; i++) {
                if ((data.collisionMask.value & (1 << layerIndices[i])) != 0) conciseMask |= (1 << i);
            }

            EditorGUI.BeginChangeCheck();
            int newConciseMask = EditorGUILayout.MaskField("Draw/Snap Mask", conciseMask, namedLayersList.ToArray());
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(data, "Change Collision Mask");
                int finalMask = 0;
                for (int i = 0; i < layerIndices.Count; i++) {
                    if ((newConciseMask & (1 << i)) != 0) finalMask |= (1 << layerIndices[i]);
                }
                data.collisionMask.value = finalMask;
                EditorUtility.SetDirty(data);
                QueueGenerateAllPreviews();
            }

            EditorGUILayout.HelpBox("Only objects on these layers will be hit during drawing and mesh snapping.", MessageType.None);
        }
        if (drawMode)
        {
            EditorGUILayout.HelpBox("1st click = set start anchor (yellow dot). Each next click = new linked spline segment. Hold+drag after click to adjust curvature. Backspace = undo last segment. Toggle button again to exit.", MessageType.Info);
            
            if (selectedSplineIndex >= 0 && selectedSplineIndex < data.splines.Count)
            {
                if (GUILayout.Button(new GUIContent("Auto-Fit Selected Spline to Riverbed", "Scans the surrounding geometry (using the mask above) and adjusts width/position to fit the gap."), GUILayout.Height(30)))
                {
                    AutoFitSplineToRiverbed(data.splines[selectedSplineIndex]);
                }
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            drawModeChainSmoothing = EditorGUILayout.Toggle(new GUIContent("Chain Smoothing", "If enabled, smoothing a point will look at previous/next splines in the chain for C1 continuity."), drawModeChainSmoothing);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool("WaterCreator_ChainSmoothing", drawModeChainSmoothing);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Y Offset", "Vertical lift added to every placed point above the hit surface."));
            drawModeYOffset = EditorGUILayout.Slider(drawModeYOffset, -5f, 10f);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.Space(2);


        int splineToDelete = -1;
        for (int i = 0; i < data.splines.Count; i++)
        {
            var s = data.splines[i];
            EditorGUILayout.BeginHorizontal();
            string buttonLabel = (i == selectedSplineIndex) ? "Selected" : "Select";
            if (GUILayout.Button(buttonLabel, GUILayout.Width(70)))
            {
                if (i == selectedSplineIndex)
                {
                    selectedSplineIndex = -1;
                }
                else
                {
                    selectedSplineIndex = i;
                }
                SceneView.RepaintAll();
            }
            EditorGUI.BeginChangeCheck();
            string newName = EditorGUILayout.TextField(s.name);
            if (EditorGUI.EndChangeCheck() && newName != s.name)
            {

                string oldGoName = $"WaterSplinePreview_{i}_{s.name}";
                var rootGo = GameObject.Find($"WaterCreator_Previews_{data.name}");
                var oldGo = rootGo != null ? rootGo.transform.Find(oldGoName)?.gameObject : null;
                
                if (oldGo != null)
                {
                    oldGo.name = $"WaterSplinePreview_{i}_{newName}";
                }

int start = WaterMeshHelper.FindChainStart(data, i);
                int end = WaterMeshHelper.FindChainEnd(data, start);
                if (end > start)
                {

}

                s.name = newName;
                EditorUtility.SetDirty(data);
            }
            if (GUILayout.Button("Up", GUILayout.Width(40)) && i > 0)
            {
                var tmp = data.splines[i - 1];
                data.splines[i - 1] = data.splines[i];
                data.splines[i] = tmp;
            }
            if (GUILayout.Button("X", GUILayout.Width(24)))
            {
                if (EditorUtility.DisplayDialog("Delete spline?", "Are you sure you want to delete spline '" + s.name + "'? The associated Flow Map will not be deleted from disk automatically.", "Yes", "No"))
                {
                    splineToDelete = i;
                }
            }
            EditorGUILayout.EndHorizontal();
            if (i == selectedSplineIndex)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.Space();
                
                EditorGUI.BeginChangeCheck();
                float newWidth = EditorGUILayout.FloatField(new GUIContent("Width", "Width of this specific river spline."), s.width);
                float newFlowSpeed = EditorGUILayout.FloatField(new GUIContent("Flow Speed", "Current speed multiplier for this spline segment."), s.flowSpeed);
                bool newInvertNormals = EditorGUILayout.Toggle(new GUIContent("Invert Normals (preview)", "Flips the mesh faces. Useful if the river generates upside-down due to spline direction."), s.invertNormals);
                
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(data, "Modify Spline Settings");
                    s.width = newWidth;
                    s.flowSpeed = newFlowSpeed;
                    s.invertNormals = newInvertNormals;
                    EditorUtility.SetDirty(data);
                }

                if (s.points.Count >= 2)
                {
                    EditorGUILayout.LabelField("Point 0 Coordinates", EditorStyles.boldLabel);
                    
                    EditorGUI.BeginChangeCheck();
                    Vector3 newP0 = EditorGUILayout.Vector3Field("", s.points[0]);
                    EditorGUILayout.LabelField("Point 0 Locks", EditorStyles.miniBoldLabel);
                    EditorGUILayout.BeginHorizontal();
                    bool newL0x = EditorGUILayout.Toggle("X", s.point0Locks.x, GUILayout.Width(40));
                    bool newL0y = EditorGUILayout.Toggle("Y", s.point0Locks.y, GUILayout.Width(40));
                    bool newL0z = EditorGUILayout.Toggle("Z", s.point0Locks.z, GUILayout.Width(40));
                    EditorGUILayout.EndHorizontal();
                    
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(data, "Modify Spline Point 0");
                        s.points[0] = newP0;
                        s.point0Locks.x = newL0x;
                        s.point0Locks.y = newL0y;
                        s.point0Locks.z = newL0z;
                        EditorUtility.SetDirty(data);
                    }

                    if (s.useBezier)
                    {
                        EditorGUILayout.LabelField("Out Handle for Point 0", EditorStyles.boldLabel);
                        EditorGUI.BeginChangeCheck();
                        Vector3 newOH0 = EditorGUILayout.Vector3Field("", s.outHandles[0]);
                        EditorGUILayout.LabelField("Out Handle 0 Locks", EditorStyles.miniBoldLabel);
                        EditorGUILayout.BeginHorizontal();
                        bool newOL0x = EditorGUILayout.Toggle("X", s.outHandle0Locks.x, GUILayout.Width(40));
                        bool newOL0y = EditorGUILayout.Toggle("Y", s.outHandle0Locks.y, GUILayout.Width(40));
                        bool newOL0z = EditorGUILayout.Toggle("Z", s.outHandle0Locks.z, GUILayout.Width(40));
                        EditorGUILayout.EndHorizontal();
                        
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(data, "Modify Handle 0");
                            s.outHandles[0] = newOH0;
                            s.outHandle0Locks.x = newOL0x;
                            s.outHandle0Locks.y = newOL0y;
                            s.outHandle0Locks.z = newOL0z;
                            EditorUtility.SetDirty(data);
                        }
                    }
                    int lastPointIdx = s.points.Count - 1;
                    EditorGUILayout.LabelField("Point 1 (Last) Coordinates", EditorStyles.boldLabel);
                    
                    EditorGUI.BeginChangeCheck();
                    Vector3 newPL = EditorGUILayout.Vector3Field("", s.points[lastPointIdx]);
                    EditorGUILayout.LabelField("Last Point Locks", EditorStyles.miniBoldLabel);
                    EditorGUILayout.BeginHorizontal();
                    bool newLLx = EditorGUILayout.Toggle("X", s.lastPointLocks.x, GUILayout.Width(40));
                    bool newLLy = EditorGUILayout.Toggle("Y", s.lastPointLocks.y, GUILayout.Width(40));
                    bool newLLz = EditorGUILayout.Toggle("Z", s.lastPointLocks.z, GUILayout.Width(40));
                    EditorGUILayout.EndHorizontal();
                    
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(data, "Modify Last Point");
                        s.points[lastPointIdx] = newPL;
                        s.lastPointLocks.x = newLLx;
                        s.lastPointLocks.y = newLLy;
                        s.lastPointLocks.z = newLLz;
                        EditorUtility.SetDirty(data);
                    }

                    if (s.useBezier)
                    {
                        EditorGUILayout.LabelField("In Handle for Point 1 (Last)", EditorStyles.boldLabel);
                        EditorGUI.BeginChangeCheck();
                        Vector3 newIL = EditorGUILayout.Vector3Field("", s.inHandles[lastPointIdx]);
                        EditorGUILayout.LabelField("In Handle Last Locks", EditorStyles.miniBoldLabel);
                        EditorGUILayout.BeginHorizontal();
                        bool newILLx = EditorGUILayout.Toggle("X", s.inHandleLastLocks.x, GUILayout.Width(40));
                        bool newILLy = EditorGUILayout.Toggle("Y", s.inHandleLastLocks.y, GUILayout.Width(40));
                        bool newILLz = EditorGUILayout.Toggle("Z", s.inHandleLastLocks.z, GUILayout.Width(40));
                        EditorGUILayout.EndHorizontal();
                        
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(data, "Modify Last Handle");
                            s.inHandles[lastPointIdx] = newIL;
                            s.inHandleLastLocks.x = newILLx;
                            s.inHandleLastLocks.y = newILLy;
                            s.inHandleLastLocks.z = newILLz;
                            EditorUtility.SetDirty(data);
                        }
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("Spline needs at least 2 points to show coordinates and locks.", MessageType.Info);
                }

                EditorGUI.indentLevel--;
            }
        }

        if (splineToDelete >= 0)
        {
            data.splines.RemoveAt(splineToDelete);
            if (selectedSplineIndex == splineToDelete) selectedSplineIndex = -1;
            else if (selectedSplineIndex > splineToDelete) selectedSplineIndex--;
            EditorUtility.SetDirty(data);

var previewRoot = GameObject.Find($"WaterCreator_Previews_{data.name}");
            if (previewRoot != null) 
            {
                while (previewRoot.transform.childCount > 0)
                {
                    Object.DestroyImmediate(previewRoot.transform.GetChild(0).gameObject);
                }
            }
            WaterMeshHelper.GenerateAllPreviews(data);
        }
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Add Spline"))
        {
            AddSplineAtSceneForward();
        }
        if (GUILayout.Button("Clear All"))
        {
            if (EditorUtility.DisplayDialog("Clear All Splines?", "This will remove all splines from the data asset. Are you sure?", "Yes", "No"))
            {
                data.splines.Clear();
                selectedSplineIndex = -1;
                EditorUtility.SetDirty(data);
                var root = GameObject.Find($"WaterCreator_Previews_{data.name}");
                if (root != null) 
                {
                    while (root.transform.childCount > 0)
                    {
                        DestroyImmediate(root.transform.GetChild(0).gameObject);
                    }
                }
            }
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSectorList()
    {
        EditorGUILayout.LabelField("Water Sectors (Grid-based)", EditorStyles.boldLabel);

        int sectorToDelete = -1;
        for (int i = 0; i < data.sectors.Count; i++)
        {
            var s = data.sectors[i];
            EditorGUILayout.BeginHorizontal();
            string buttonLabel = (i == selectedSectorIndex) ? "SELECTED" : "Select";
            if (GUILayout.Button(buttonLabel, GUILayout.Width(70)))
            {
                selectedSectorIndex = i;
            }
            s.name = EditorGUILayout.TextField(s.name);
            if (GUILayout.Button("X", GUILayout.Width(25)))
            {
                sectorToDelete = i;
            }
            EditorGUILayout.EndHorizontal();
        }

        if (sectorToDelete != -1)
        {
            Undo.RecordObject(data, "Delete Sector");
            data.sectors.RemoveAt(sectorToDelete);
            selectedSectorIndex = -1;
            EditorUtility.SetDirty(data);
            QueueGenerateAllPreviews();
        }

        if (GUILayout.Button("Add New Water Sector"))
        {
            Undo.RecordObject(data, "Add Sector");
            var ns = new WaterSectorGrid();
            ns.name = $"Sector_{data.sectors.Count}";

            if (data.sectors.Count > 0) ns.position = data.sectors[data.sectors.Count - 1].position + Vector3.right * 110f;
            data.sectors.Add(ns);
            selectedSectorIndex = data.sectors.Count - 1;
            EditorUtility.SetDirty(data);
            QueueGenerateAllPreviews();
        }

        if (selectedSectorIndex >= 0 && selectedSectorIndex < data.sectors.Count)
        {
            var s = data.sectors[selectedSectorIndex];
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField($"Editing: {s.name}", EditorStyles.miniBoldLabel);

            EditorGUI.BeginChangeCheck();
            Vector3 newPos = EditorGUILayout.Vector3Field("Position", s.position);
            float newW = EditorGUILayout.FloatField("Total Width", s.width);
            float newL = EditorGUILayout.FloatField("Total Length", s.length);
            int newSubs = EditorGUILayout.IntSlider("Subdivisions (Density)", s.subdivisions, 1, 256);
            int newSecX = EditorGUILayout.IntSlider("Sectors X", s.sectorCountX, 1, 32);
            int newSecZ = EditorGUILayout.IntSlider("Sectors Z", s.sectorCountZ, 1, 32);
            int newId = EditorGUILayout.IntField("River/Water ID", s.riverId);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(data, "Edit Sector");
                s.position = newPos;
                s.width = Mathf.Max(1f, newW);
                s.length = Mathf.Max(1f, newL);
                s.subdivisions = newSubs;
                s.sectorCountX = newSecX;
                s.sectorCountZ = newSecZ;
                s.riverId = newId;
                EditorUtility.SetDirty(data);
                QueueGenerateAllPreviews();
            }
            EditorGUILayout.HelpBox("Grid Sectors are ideal for oceans. Increasing 'Sectors X/Z' splits the area into more meshes, improving reflection probe accuracy and camera culling in Forward+.", MessageType.Info);
            EditorGUILayout.EndVertical();
        }
    }

    private void DrawMaterialSyncSettings()
    {
        EditorGUILayout.LabelField("Material Synchronization", EditorStyles.boldLabel);
        
        GUI.enabled = (selectedSplineIndex >= 0 && selectedSplineIndex < data.splines.Count);
        if (GUILayout.Button("This material -> All splines"))
        {
            SyncMaterialToAllSplines(selectedSplineIndex);
        }
        
        if (GUILayout.Button(new GUIContent("This material -> Original Material", "Saves the selected spline's material options to the original preview material.")))
        {
            SaveDefaultMaterialFromSpline(selectedSplineIndex);
        }
        GUI.enabled = true;

        if (GUILayout.Button(new GUIContent("Original Material -> All splines", "Overwrites all splines' materials with the current preview material.")))
        {
            OverwriteAllWithPreviewMaterial();
        }
    }

    private void AddSplineAtSceneForward()
    {
        var ns = new WaterSpline();
        Vector3 camPos = Vector3.zero;
        Vector3 camForward = Vector3.forward;
        if (SceneView.lastActiveSceneView != null)
        {
            var cam = SceneView.lastActiveSceneView.camera;
            if (cam != null)
            {
                camPos = cam.transform.position;
                camForward = cam.transform.forward;
            }
        }
        if (data.splines.Count > 0)
        {
            var last = data.splines[data.splines.Count - 1];
            if (last.points == null || last.points.Count == 0)
            {
                Vector3 dir = new Vector3(camForward.x, 0f, camForward.z).normalized;
                if (dir.magnitude < 0.1f) dir = Vector3.forward;
                Vector3 starPos = camPos + camForward * 10f;
                Vector3 endPos = starPos + dir * 1f;
                last.points = new List<Vector3>() { starPos, endPos };
                last.EnsureHandleCounts();
            }
            Vector3 lastPoint = last.points[last.points.Count - 1];
            Vector3 direction = Vector3.forward;
            if (last.points.Count >= 2)
            {
                direction = (lastPoint - last.points[last.points.Count - 2]).normalized;
            }
            else
            {
                direction = new Vector3(camForward.x, 0f, camForward.z).normalized;
                if (direction.magnitude < 0.1f) direction = Vector3.forward;
            }
            Vector3 start = lastPoint;
            Vector3 end = start + direction * 1f;
            ns.points = new List<Vector3>() { start, end };
            ns.linkToPrevious = true;
            ns.width = last.width;
            ns.flowSpeed = last.flowSpeed;
            ns.useBezier = last.useBezier;
        }
        else
        {
            Vector3 dir = new Vector3(camForward.x, 0f, camForward.z).normalized;
            if (dir.magnitude < 0.1f) dir = Vector3.forward;
            Vector3 center = new Vector3(camPos.x, 0f, camPos.z);
            Vector3 start = center;
            Vector3 end = start + dir * 1f;
            ns.points = new List<Vector3>() { start, end };
            ns.linkToPrevious = false;
            ns.width = 1f;
        }
        ns.EnsureHandleCounts();
        data.splines.Add(ns);
        selectedSplineIndex = data.splines.Count - 1;
        EditorUtility.SetDirty(data);
        if (data.splines.Count > 1 && ns.useBezier)
        {
            var prev = data.splines[data.splines.Count - 2];
            if (prev.useBezier)
            {
                int prevLast = prev.points.Count - 1;
                ns.outHandles[0] = 2 * ns.points[0] - prev.inHandles[prevLast];
            }
        }
        QueueGenerateAllPreviews();
    }

    private void QueueGenerateAllPreviews(bool forceFullCleanup = false)
    {
        currentRegenRequest = RegenRequest.RebuildMeshes;
        if (forceFullCleanup) pendingForceFullCleanup = true;

        if (regenQueued) return;

        regenQueued = true;
        EditorApplication.delayCall += () =>
        {
            regenQueued = false;
            RegenRequest requestToProcess = currentRegenRequest;
            bool force = pendingForceFullCleanup;
            
            currentRegenRequest = RegenRequest.None;
            pendingForceFullCleanup = false;

            if (data != null) 
            {
                if (requestToProcess == RegenRequest.RebuildMeshes)
                {
                    WaterMeshHelper.GenerateAllPreviews(data, force);
                }
                else if (requestToProcess == RegenRequest.RefreshVisuals)
                {
                    WaterMeshHelper.RefreshExistingPreviews(data);
                }
                
                lastSplineCount = data.splines.Count;
                lastSectorCount = data.sectors.Count;
            }
        };
    }

    private void QueueRefreshPreviews()
    {
        if (currentRegenRequest == RegenRequest.None)
            currentRegenRequest = RegenRequest.RefreshVisuals;

        if (regenQueued) return;

        regenQueued = true;
        EditorApplication.delayCall += () =>
        {
            regenQueued = false;
            RegenRequest requestToProcess = currentRegenRequest;
            bool force = pendingForceFullCleanup;

            currentRegenRequest = RegenRequest.None;
            pendingForceFullCleanup = false;

            if (data != null)
            {
                if (requestToProcess == RegenRequest.RebuildMeshes)
                {
                    WaterMeshHelper.GenerateAllPreviews(data, force);
                }
                else if (requestToProcess == RegenRequest.RefreshVisuals)
                {
                    WaterMeshHelper.RefreshExistingPreviews(data);
                }
                
                lastSplineCount = data.splines.Count;
                lastSectorCount = data.sectors.Count;
            }
        };
    }

    private void SyncMaterialToAllSplines(int sourceIndex)
    {
        if (data == null || data.splines == null || sourceIndex < 0 || sourceIndex >= data.splines.Count) return;

        var sourceSpline = data.splines[sourceIndex];
        string sourceGoName = $"WaterSplinePreview_{sourceIndex}_{sourceSpline.name}";
        var rootGoSource = GameObject.Find($"WaterCreator_Previews_{data.name}");
        var sourceGo = rootGoSource != null ? rootGoSource.transform.Find(sourceGoName)?.gameObject : null;
        
        if (sourceGo == null)
        {
            EditorUtility.DisplayDialog("Error", "Could not find the target mesh in the scene to copy the material from. Try selecting it and modifying the material first.", "OK");
            return;
        }

        var sourceRenderer = sourceGo.GetComponent<MeshRenderer>();
        if (sourceRenderer == null || sourceRenderer.sharedMaterial == null) return;

        Material matToCopy = sourceRenderer.sharedMaterial;
        int syncCount = 0;

        for (int i = 0; i < data.splines.Count; i++)
        {
            if (i == sourceIndex) continue;

            var targetSpline = data.splines[i];
            string targetGoName = $"WaterSplinePreview_{i}_{targetSpline.name}";
            var rootGo = GameObject.Find($"WaterCreator_Previews_{data.name}");
            var targetGo = rootGo != null ? rootGo.transform.Find(targetGoName)?.gameObject : null;

            if (targetGo != null)
            {
                var targetRenderer = targetGo.GetComponent<MeshRenderer>();
                if (targetRenderer != null && targetRenderer.sharedMaterial != null)
                {
                    Undo.RecordObject(targetRenderer.sharedMaterial, "Sync Material Properties");
                    targetRenderer.sharedMaterial.CopyPropertiesFromMaterial(matToCopy);
                    syncCount++;
                }
            }
        }
        
        Debug.Log($"Synchronized material properties across {syncCount} water segments.");
        SceneView.RepaintAll();
    }

    private void SaveDefaultMaterialFromSpline(int sourceIndex)
    {
        if (data == null || data.previewMaterial == null || data.splines == null || sourceIndex < 0 || sourceIndex >= data.splines.Count) 
        {
            EditorUtility.DisplayDialog("Error", "Missing Data Asset, Preview Material, or Spline selection.", "OK");
            return;
        }

        var sourceSpline = data.splines[sourceIndex];
        string sourceGoName = $"WaterSplinePreview_{sourceIndex}_{sourceSpline.name}";
        var rootGo = GameObject.Find($"WaterCreator_Previews_{data.name}");
        var sourceGo = rootGo != null ? rootGo.transform.Find(sourceGoName)?.gameObject : null;
        
        if (sourceGo == null)
        {
            EditorUtility.DisplayDialog("Error", "Could not find the target mesh in the scene to copy the material from.", "OK");
            return;
        }

        var sourceRenderer = sourceGo.GetComponent<MeshRenderer>();
        if (sourceRenderer == null || sourceRenderer.sharedMaterial == null) return;

        if (EditorUtility.DisplayDialog("Save as Default Material?", 
            $"This will overwrite the settings of the asset '{data.previewMaterial.name}' with the current settings from spline '{sourceSpline.name}'.\n\nThis affects all future splines and any splines currently sharing the default material.\n\nContinue?", 
            "Yes, Overwrite", "Cancel"))
        {
            Undo.RecordObject(data.previewMaterial, "Save Default Material");
            data.previewMaterial.CopyPropertiesFromMaterial(sourceRenderer.sharedMaterial);
            EditorUtility.SetDirty(data.previewMaterial);
            AssetDatabase.SaveAssets();
            
            Debug.Log($"[AQUIS Pro] Successfully updated '{data.previewMaterial.name}' with settings from '{sourceSpline.name}'.");
            SceneView.RepaintAll();
        }
    }

    private void OverwriteAllWithPreviewMaterial()
    {
        if (data == null || data.previewMaterial == null || data.splines == null) return;
        
        if (EditorUtility.DisplayDialog("Overwrite All Materials?", "This will overwrite all spline materials with the current preview material properties. Continue?", "Yes", "Cancel"))
        {
            int syncCount = 0;
            for (int i = 0; i < data.splines.Count; i++)
            {
                var targetSpline = data.splines[i];
                string targetGoName = $"WaterSplinePreview_{i}_{targetSpline.name}";
                var rootGo = GameObject.Find($"WaterCreator_Previews_{data.name}");
                var targetGo = rootGo != null ? rootGo.transform.Find(targetGoName)?.gameObject : null;

                if (targetGo != null)
                {
                    var targetRenderer = targetGo.GetComponent<MeshRenderer>();
                    if (targetRenderer != null && targetRenderer.sharedMaterial != null)
                    {
                        Undo.RecordObject(targetRenderer.sharedMaterial, "Overwrite with Preview Material");
                        targetRenderer.sharedMaterial.CopyPropertiesFromMaterial(data.previewMaterial);
                        syncCount++;
                    }
                }
            }
            Debug.Log($"Overwrote properties for {syncCount} water segments with the preview material.");
            SceneView.RepaintAll();
        }
    }

    private void OnSceneGUI(SceneView sv)
    {
        if (data == null) return;

        string activeScenePath = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path;
        string activeSceneGUID = AssetDatabase.AssetPathToGUID(activeScenePath);
        bool isBound = data.sceneGUIDs != null && data.sceneGUIDs.Contains(activeSceneGUID);

        if (!isBound) return;
        
        if (Event.current.type == EventType.MouseUp)
        {
            m_HandleRotation = Quaternion.identity;
            m_HandleScale = Vector3.one;
        }
        
        bool paintMode = UnityEditor.EditorPrefs.GetBool("WaterCreator_PaintMode", false);
        
        bool isParentSelected = (Selection.activeGameObject != null && Selection.activeGameObject.name == $"WaterCreator_Previews_{data.name}");
        bool isChildSelected = (Selection.activeGameObject != null && Selection.activeGameObject.name.StartsWith("WaterSplinePreview_") && Selection.activeGameObject.transform.parent != null && Selection.activeGameObject.transform.parent.name == $"WaterCreator_Previews_{data.name}");

        Tools.hidden = isParentSelected || isChildSelected || paintMode;

        if (paintMode)
        {
            WaterFlowMapHelper.HandlePainting(this, sv);
            SceneView.RepaintAll();
            return;
        }

if (isParentSelected && data.splines != null && data.splines.Count > 0)
        {
            Vector3 globalCentroid = Vector3.zero;
            int totalPts = 0;
            foreach (var sp in data.splines)
            {
                if (sp != null && sp.points != null)
                {
                    foreach (var pt in sp.points)
                    {
                        globalCentroid += pt;
                        totalPts++;
                    }
                }
            }
            if (totalPts > 0)
            {
                globalCentroid /= totalPts;
                
                if (!drawMode && (Tools.current == Tool.Move || Tools.current == Tool.Rotate || Tools.current == Tool.Scale))
                {
                    EditorGUI.BeginChangeCheck();
                    Vector3 newCentroid = globalCentroid;
                    Quaternion newRotation = m_HandleRotation;
                    Vector3 newScale = m_HandleScale;

                    if (Tools.current == Tool.Move)
                    {
                        newCentroid = Handles.PositionHandle(globalCentroid, m_HandleRotation);
                    }
                    else if (Tools.current == Tool.Rotate)
                    {
                        newRotation = Handles.RotationHandle(m_HandleRotation, globalCentroid);
                    }
                    else if (Tools.current == Tool.Scale)
                    {
                        newScale = Handles.ScaleHandle(m_HandleScale, globalCentroid, m_HandleRotation, HandleUtility.GetHandleSize(globalCentroid));
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(data, "Transform All Splines");

                        Vector3 diffPos = newCentroid - globalCentroid;
                        Quaternion deltaRot = newRotation * Quaternion.Inverse(m_HandleRotation);
                        
                        Vector3 deltaScale = Vector3.one;
                        if (m_HandleScale.x != 0 && m_HandleScale.y != 0 && m_HandleScale.z != 0)
                        {
                            deltaScale = new Vector3(newScale.x / m_HandleScale.x, newScale.y / m_HandleScale.y, newScale.z / m_HandleScale.z);
                        }

                        m_HandleRotation = newRotation;
                        m_HandleScale = newScale;

                        Matrix4x4 trs = Matrix4x4.TRS(diffPos, deltaRot, deltaScale);

                        foreach (var sc in data.splines)
                        {
                            if (sc == null || sc.points == null) continue;
                            for (int iInner = 0; iInner < sc.points.Count; iInner++)
                            {
                                Vector3 localP = sc.points[iInner] - globalCentroid;
                                sc.points[iInner] = globalCentroid + trs.MultiplyPoint3x4(localP);

                                if (sc.useBezier)
                                {
                                    Vector3 localOut = sc.outHandles[iInner] - globalCentroid;
                                    sc.outHandles[iInner] = globalCentroid + trs.MultiplyPoint3x4(localOut);

                                    Vector3 localIn = sc.inHandles[iInner] - globalCentroid;
                                    sc.inHandles[iInner] = globalCentroid + trs.MultiplyPoint3x4(localIn);
                                }
                            }
                            sc.EnsureHandleCounts();
                        }
                        EditorUtility.SetDirty(data);
                        QueueGenerateAllPreviews();
                    }
                }
            }
        }

        for (int i = 0; i < data.splines.Count; i++)
        {
            DrawSplineHandles(data.splines[i], i == selectedSplineIndex);
        }



      if (drawMode)
{
    sv.wantsMouseMove = true;

    if (Event.current.type != EventType.Repaint && Event.current.type != EventType.Layout)
    {
        Ray ghostRay = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        if (Physics.Raycast(ghostRay, out RaycastHit ghostHit, Mathf.Infinity, data != null ? data.collisionMask : (LayerMask)(-1)))
        {
            ghostPoint = ghostHit.point;
            ghostPoint.y += drawModeYOffset;
            ghostValid = true;
        }
        else
        {

            ghostValid = false;
        }
    }

    int drawCtrl = GUIUtility.GetControlID(FocusType.Passive);
    if (Event.current.type == EventType.Layout)
        HandleUtility.AddDefaultControl(drawCtrl);




    if (Event.current.type == EventType.KeyDown &&
        Event.current.keyCode == KeyCode.Backspace)
    {
        if (drawModeHasAnchor && data.splines.Count > 0)
        {


            Undo.RecordObject(data, "Undo Draw Segment");
 
            var removed = data.splines[data.splines.Count - 1];

            drawAnchor = removed.points[0];
            data.splines.RemoveAt(data.splines.Count - 1);
 
            if (data.splines.Count == 0)
                drawModeHasAnchor = false;
 
            selectedSplineIndex      = Mathf.Clamp(data.splines.Count - 1, -1, data.splines.Count - 1);
            drawModeIsDraggingCurve  = false;
            drawModeDragEndpoint     = false;
            drawModeDragSegmentIndex = -1;
 
            EditorUtility.SetDirty(data);
            QueueGenerateAllPreviews();
            Event.current.Use();
            Repaint();
        }
        else
        {

            drawModeHasAnchor = false;
            Event.current.Use();
        }
    }




    if (Event.current.type == EventType.MouseDown &&
        Event.current.button == 0 &&
        !Event.current.alt && !Event.current.control &&
        ghostValid && !drawModeIsDraggingCurve && !drawModeDragEndpoint)
    {
        GUIUtility.hotControl = drawCtrl;
 
        if (!drawModeHasAnchor)
        {

            drawAnchor        = ghostPoint;
            drawModeHasAnchor = true;
            Event.current.Use();
            Repaint();
        }
        else
        {

            DrawModeCreateSegment(drawAnchor, ghostPoint);
            drawAnchor               = ghostPoint;
            drawModeIsDraggingCurve  = true;
            drawModeDragSegmentIndex = data.splines.Count - 1;
            Event.current.Use();
            Repaint();
        }
    }




    if (drawModeIsDraggingCurve &&
        Event.current.type == EventType.MouseDrag &&
        Event.current.button == 0)
    {
        if (drawModeDragSegmentIndex >= 0 &&
            drawModeDragSegmentIndex < data.splines.Count)
        {
            Ray dragRay = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
            Vector3 dragWorld = Vector3.zero;
            bool dragHit = Physics.Raycast(dragRay, out RaycastHit dragInfo, Mathf.Infinity);
            if (dragHit)
            {
                dragWorld    = dragInfo.point;
                dragWorld.y += drawModeYOffset;
            }
            else
            {
                Plane pg = new Plane(Vector3.up, Vector3.zero);
                if (pg.Raycast(dragRay, out float de))
                {
                    dragWorld    = dragRay.GetPoint(de);
                    dragWorld.y += drawModeYOffset;
                    dragHit      = true;
                }
            }
 
            if (dragHit)
            {
                var seg = data.splines[drawModeDragSegmentIndex];
                seg.EnsureHandleCounts();
                int segLast = seg.points.Count - 1;
                seg.inHandles[segLast] = dragWorld;
                EditorUtility.SetDirty(data);
                QueueGenerateAllPreviews();
                Event.current.Use();
            }
        }
    }








    if (Event.current.type == EventType.MouseDown &&
        Event.current.button == 1 &&
        Event.current.control &&
        drawModeHasAnchor)
    {
        Undo.RecordObject(data, "Adjust Segment");
        if (Event.current.shift)
            drawModeDragCurvature = true;
        else
            drawModeDragEndpoint = true;

        GUIUtility.hotControl = drawCtrl;
        Event.current.Use();
    }



    if (drawModeDragEndpoint &&
        Event.current.type == EventType.MouseDrag &&
        Event.current.button == 1)
    {
        Ray epRay = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        Vector3 epWorld = Vector3.zero;
        bool epHit = false;
        if (Physics.Raycast(epRay, out RaycastHit epInfo, Mathf.Infinity, data != null ? data.collisionMask : (LayerMask)(-1)))
        {
            epWorld = epInfo.point;
            epWorld.y += drawModeYOffset;
            epHit = true;
        }
        else
        {

            epHit = false;
        }

        if (epHit)
        {
            if (data.splines.Count > 0)
            {
                int nsIdx = data.splines.Count - 1;
                var lastSpl = data.splines[nsIdx];
                lastSpl.EnsureHandleCounts();
                int lp      = lastSpl.points.Count - 1;
                lastSpl.points[lp] = epWorld;

                if (drawModeChainSmoothing && nsIdx > 0 && lastSpl.linkToPrevious)
                {
                    var prev = data.splines[nsIdx - 1];
                    AutoSmoothHandles(prev, prev.points.Count - 1, nsIdx - 1);
                    AutoSmoothHandles(lastSpl, 0, nsIdx);
                }
                AutoSmoothHandles(lastSpl, lp, nsIdx);

                drawAnchor = epWorld;
            }
            else
            {

                drawAnchor = epWorld;
            }
 
            EditorUtility.SetDirty(data);
            QueueGenerateAllPreviews();
            Event.current.Use();
        }
    }



    if (drawModeDragCurvature &&
        Event.current.type == EventType.MouseDrag &&
        Event.current.button == 1)
    {
        Ray cvRay = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
        if (Physics.Raycast(cvRay, out RaycastHit cvInfo, Mathf.Infinity))
        {
            if (data.splines.Count > 0)
            {
                var lastSpl = data.splines[data.splines.Count - 1];
                lastSpl.EnsureHandleCounts();
                int lp = lastSpl.points.Count - 1;
                
                Vector3 dragWorld = cvInfo.point;
                dragWorld.y += drawModeYOffset;
                
                lastSpl.inHandles[lp] = dragWorld;
                
                EditorUtility.SetDirty(data);
                QueueGenerateAllPreviews();
            }
            Event.current.Use();
        }
    }



    if (Event.current.type == EventType.MouseUp)
    {
        if (drawModeIsDraggingCurve && Event.current.button == 0)
        {
            drawModeIsDraggingCurve  = false;
            drawModeDragSegmentIndex = -1;
            if (data != null) QueueGenerateAllPreviews();
        }
        if ((drawModeDragEndpoint || drawModeDragCurvature) && Event.current.button == 1)
        {
            drawModeDragEndpoint = false;
            drawModeDragCurvature = false;
            if (data != null) QueueGenerateAllPreviews();
        }
        if (GUIUtility.hotControl == drawCtrl)
            GUIUtility.hotControl = 0;
    }



    if (Event.current.type == EventType.Repaint)
    {
        if (drawModeDragEndpoint || drawModeDragCurvature)
        {

            bool isCurv = drawModeDragCurvature;
            Vector3 ep = (data.splines.Count > 0)
                ? data.splines[data.splines.Count - 1].points[data.splines[data.splines.Count - 1].points.Count - 1]
                : drawAnchor;
            
            if (isCurv && data.splines.Count > 0)
            {
                var s = data.splines[data.splines.Count - 1];
                ep = s.inHandles[s.points.Count - 1];
            }

            float epSize = HandleUtility.GetHandleSize(ep) * 0.22f;
            Handles.color = isCurv ? new Color(1f, 0.8f, 0.0f, 0.95f) : new Color(1f, 0.5f, 0.0f, 0.95f);
            Handles.DrawSolidDisc(ep, Vector3.up, epSize);
            
            if (isCurv && data.splines.Count > 0)
            {
                var s = data.splines[data.splines.Count - 1];
                Handles.DrawLine(s.points[s.points.Count - 1], ep, 2f);
            }
 
            GUIStyle epLbl = new GUIStyle
            {
                normal   = { textColor = isCurv ? new Color(1f, 0.8f, 0.2f) : new Color(1f, 0.6f, 0.1f) },
                fontSize = 11,
                fontStyle = FontStyle.Bold
            };
            string txt = isCurv ? "Ctrl+Shift+RMB — Adjusting Curvature" : "Ctrl+RMB — Moving endpoint";
            Handles.Label(ep + Vector3.up * epSize * 2.5f, txt, epLbl);
        }
        else if (ghostValid)
        {
            float discSize = HandleUtility.GetHandleSize(ghostPoint) * 0.18f;
 
            if (drawModeIsDraggingCurve &&
                drawModeDragSegmentIndex >= 0 &&
                drawModeDragSegmentIndex < data.splines.Count)
            {
                var seg = data.splines[drawModeDragSegmentIndex];
                seg.EnsureHandleCounts();
                int segLast = seg.points.Count - 1;

                Handles.color = new Color(1f, 0.65f, 0f, 0.9f);
                Handles.DrawLine(seg.points[segLast], seg.inHandles[segLast], 2.5f);
                Handles.DrawSolidDisc(seg.inHandles[segLast], Vector3.up,
                    HandleUtility.GetHandleSize(seg.inHandles[segLast]) * 0.1f);

                Handles.color = new Color(0.7f, 0.7f, 0.7f, 0.5f);
                Handles.DrawLine(seg.points[0], seg.outHandles[0], 1.5f);
                Handles.DrawWireDisc(seg.outHandles[0], Vector3.up,
                    HandleUtility.GetHandleSize(seg.outHandles[0]) * 0.08f);
 
                GUIStyle curveLbl = new GUIStyle
                {
                    normal   = { textColor = new Color(1f, 0.75f, 0.1f) },
                    fontSize = 10
                };
                Handles.Label(
                    seg.inHandles[segLast] + Vector3.up * HandleUtility.GetHandleSize(seg.inHandles[segLast]) * 0.4f,
                    "Drag to curve  |  Ctrl+RMB to move endpoint",
                    curveLbl);
            }
            else if (drawModeHasAnchor)
            {

                Handles.color = new Color(0.3f, 1f, 0.4f, 0.85f);
                Handles.DrawDottedLine(drawAnchor, ghostPoint, 5f);
                Handles.DrawWireDisc(ghostPoint, Vector3.up, discSize);

                float anchorSize = HandleUtility.GetHandleSize(drawAnchor) * 0.14f;
                Handles.color = new Color(1f, 0.85f, 0.1f, 0.9f);
                Handles.DrawSolidDisc(drawAnchor, Vector3.up, anchorSize);
 
                GUIStyle hintLbl = new GUIStyle
                {
                    normal   = { textColor = new Color(0.7f, 0.9f, 0.7f) },
                    fontSize = 10
                };
                Handles.Label(
                    ghostPoint + Vector3.up * discSize * 2.5f,
                    "Click = segment  |  Ctrl+RMB = move endpoint  |  Backspace = undo",
                    hintLbl);
            }
            else
            {

                Handles.color = new Color(0.3f, 1f, 0.4f, 0.7f);
                Handles.DrawWireDisc(ghostPoint, Vector3.up, discSize);
 
                GUIStyle lbl = new GUIStyle
                {
                    normal   = { textColor = new Color(0.3f, 1f, 0.4f) },
                    fontSize = 10
                };
                Handles.Label(ghostPoint + Vector3.up * discSize * 2.5f, "Click to start", lbl);
            }
        }
    }
}

        if (selectedSplineIndex >= 0 && selectedSplineIndex < data.splines.Count)
        {
            var s = data.splines[selectedSplineIndex];
            if (s == null) return;
            s.EnsureHandleCounts();

            Handles.color = s.debugColor;
            int lastIdx = Mathf.Max(0, s.points.Count - 1);

            if (!drawMode)
            {
            for (int p = 0; p < s.points.Count; p++)
            {
                Vector3 anchor = s.points[p];
                EditorGUI.BeginChangeCheck();
                float pointHandleSize = HandleUtility.GetHandleSize(anchor) * 0.15f;
                Vector3 newAnchor = Handles.FreeMoveHandle(anchor, pointHandleSize, Vector3.zero, FilledRectangleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(data, "Move Spline Point");
                    Vector3Bool locks = (p == 0) ? s.point0Locks : (p == lastIdx) ? s.lastPointLocks : new Vector3Bool(false, false, false);
                    s.points[p] = s.ApplyConstraints(newAnchor, anchor, locks);
                    
                    Vector3 delta = s.points[p] - anchor;

                    if (s.useBezier)
                    {
                        if (p < lastIdx) s.outHandles[p] += delta;
                        if (p > 0) s.inHandles[p] += delta;
                    }

                    s.EnsureHandleCounts();
                    EditorUtility.SetDirty(data);
                    
                    if (p == s.points.Count - 1 && selectedSplineIndex + 1 < data.splines.Count)
                    {
                        var next = data.splines[selectedSplineIndex + 1];
                        if (next != null && next.linkToPrevious)
                        {
                            next.points[0] = s.points[p];
                            if (next.useBezier) next.outHandles[0] += delta;
                            next.EnsureHandleCounts();
                            EditorUtility.SetDirty(data);
                        }
                    }
                    if (p == 0 && s.linkToPrevious && selectedSplineIndex - 1 >= 0)
                    {
                        var prev = data.splines[selectedSplineIndex - 1];
                        if (prev != null && prev.points != null && prev.points.Count > 0)
                        {
                            int prevLast = prev.points.Count - 1;
                            prev.points[prevLast] = s.points[p];
                            if (prev.useBezier) prev.inHandles[prevLast] += delta;
                            prev.EnsureHandleCounts();
                            EditorUtility.SetDirty(data);
                        }
                    }
                    QueueGenerateAllPreviews();
                    this.Repaint();
                }
                if (s.useBezier)
                {
                    s.EnsureHandleCounts();
                    if (p < lastIdx)
                    {
                        Vector3 outPos = s.outHandles[p];
                        float sphereSize = HandleUtility.GetHandleSize(outPos) * 0.3f;
                        Handles.color = Color.yellow;
                        EditorGUI.BeginChangeCheck();
                        Vector3 newOut = Handles.FreeMoveHandle(outPos, sphereSize, Vector3.zero, Handles.SphereHandleCap);
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(data, "Move Out Handle");
                            Vector3Bool locks = (p == 0) ? s.outHandle0Locks : new Vector3Bool(false, false, false);
                            s.outHandles[p] = s.ApplyConstraints(newOut, outPos, locks);
                            EditorUtility.SetDirty(data);
                            if (p == 0 && s.linkToPrevious && selectedSplineIndex - 1 >= 0)
                            {
                                var prev = data.splines[selectedSplineIndex - 1];
                                if (prev.useBezier)
                                {
                                    int prevLast = prev.points.Count - 1;
                                    Undo.RecordObject(data, "Link In Handle");
                                    prev.inHandles[prevLast] = 2 * s.points[0] - s.outHandles[p];
                                    EditorUtility.SetDirty(data);
                                }
                            }
                            QueueGenerateAllPreviews();
                            this.Repaint();
                        }
                        Handles.color = Color.yellow;
                        Handles.DrawLine(s.points[p], s.outHandles[p], 3f);
                    }
                    if (p > 0)
                    {
                        Vector3 inPos = s.inHandles[p];
                        float sphereSize = HandleUtility.GetHandleSize(inPos) * 0.2f;
                        Handles.color = Color.yellow;
                        EditorGUI.BeginChangeCheck();
                        Vector3 newIn = Handles.FreeMoveHandle(inPos, sphereSize, Vector3.zero, Handles.SphereHandleCap);
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(data, "Move In Handle");
                            Vector3Bool locks = (p == lastIdx) ? s.inHandleLastLocks : new Vector3Bool(false, false, false);
                            s.inHandles[p] = s.ApplyConstraints(newIn, inPos, locks);
                            EditorUtility.SetDirty(data);
                            if (p == lastIdx && selectedSplineIndex + 1 < data.splines.Count)
                            {
                                var next = data.splines[selectedSplineIndex + 1];
                                if (next.linkToPrevious && next.useBezier)
                                {
                                    Undo.RecordObject(data, "Link Out Handle");
                                    next.outHandles[0] = 2 * s.points[p] - s.inHandles[p];
                                    EditorUtility.SetDirty(data);
                                }
                            }
                            QueueGenerateAllPreviews();
                            this.Repaint();
                        }
                        Handles.color = new Color(1f, 0.6f, 0.0f, 0.9f);
                        Handles.DrawLine(s.points[p], s.inHandles[p], 3f);
                    }
                    Handles.color = s.debugColor;
                }
            }
            }
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            if (!drawMode && Event.current.type == EventType.MouseDown && Event.current.control)
            {
                Ray r = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
                if (Physics.Raycast(r, out RaycastHit hit))
                {
                    Undo.RecordObject(data, "Add Spline Point");
                    s.points.Add(hit.point);
                    s.EnsureHandleCounts();
                    AutoSmoothHandles(s, s.points.Count - 1, selectedSplineIndex);
                    if (s.points.Count >= 3) AutoSmoothHandles(s, s.points.Count - 2, selectedSplineIndex);
                    EditorUtility.SetDirty(data);
                    QueueGenerateAllPreviews();
                    Event.current.Use();
                }
            }

            if (!drawMode && Event.current.type == EventType.MouseDown &&
                Event.current.button == 0 && Event.current.shift && !Event.current.alt)
            {
                Ray sr = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
                bool shiftHit = Physics.Raycast(sr, out RaycastHit sHit, Mathf.Infinity);
                Vector3 shiftPt = Vector3.zero;
                if (shiftHit)
                {
                    shiftPt = sHit.point;
                }
                else
                {
                    Plane pg = new Plane(Vector3.up, Vector3.zero);
                    if (pg.Raycast(sr, out float se)) { shiftPt = sr.GetPoint(se); shiftHit = true; }
                }
                if (shiftHit)
                {
                    Undo.RecordObject(data, "Shift+Click Spline Point");
                    s.points.Add(shiftPt);
                    s.EnsureHandleCounts();
                    AutoSmoothHandles(s, s.points.Count - 1);
                    if (s.points.Count >= 3) AutoSmoothHandles(s, s.points.Count - 2);
                    EditorUtility.SetDirty(data);
                    QueueGenerateAllPreviews();
                    Event.current.Use();
                    Repaint();
                }
            }

            if (s.points.Count > 0)
            {
                Vector3 extrudePt = s.points[s.points.Count - 1];
                float extrudeSize = HandleUtility.GetHandleSize(extrudePt) * 0.28f;
                Handles.color = drawMode ? new Color(0.2f, 1f, 0.3f, 1f) : new Color(0.4f, 1f, 0.5f, 0.9f);
                if (Handles.Button(extrudePt, Quaternion.identity, extrudeSize, extrudeSize, Handles.CircleHandleCap))
                {
                    drawMode = true;
                    EditorPrefs.SetBool("WaterCreator_DrawMode", true);
                    ghostValid = false;
                    SceneView.RepaintAll();
                    Repaint();
                }
                if (Event.current.type == EventType.Repaint)
                {
                    GUIStyle extrudeLabel = new GUIStyle();
                    extrudeLabel.normal.textColor = drawMode ? new Color(0.1f, 0.9f, 0.2f) : new Color(0.2f, 0.8f, 0.3f);
                    extrudeLabel.fontSize = 11;
                    extrudeLabel.fontStyle = FontStyle.Bold;
                    Handles.Label(extrudePt + Vector3.up * extrudeSize * 2.2f, "+", extrudeLabel);
                }
            }
        }

        SceneView.RepaintAll();
    }




    private void DrawModeCreateSegment(Vector3 from, Vector3 to)
    {

        Undo.RecordObject(data, "Draw Spline Segment");
    
        var prev = (data.splines.Count > 0) ? data.splines[data.splines.Count - 1] : null;
    
        var ns   = new WaterSpline();
        ns.name  = $"RiverSegment_{data.splines.Count}";
        ns.points = new System.Collections.Generic.List<Vector3>() { from, to };

        if (prev != null)
        {
            ns.linkToPrevious = true;
            ns.width          = prev.width;
            ns.flowSpeed      = prev.flowSpeed;
            ns.useBezier      = prev.useBezier;
            ns.debugColor     = prev.debugColor;
            ns.riverId        = prev.riverId;
        }
    
        data.splines.Add(ns);
        int nsIdx = data.splines.Count - 1;

        if (drawModeChainSmoothing && prev != null && ns.linkToPrevious)
        {
            AutoSmoothHandles(prev, prev.points.Count - 1, nsIdx - 1);
            AutoSmoothHandles(ns, 0, nsIdx);
        }
        else
        {

            AutoSmoothHandles(ns, 0, nsIdx);
        }
        AutoSmoothHandles(ns, 1, nsIdx);
        selectedSplineIndex = data.splines.Count - 1;
        EditorUtility.SetDirty(data);
        QueueGenerateAllPreviews();
        Repaint();
    }




    private void AutoSmoothHandles(WaterSpline s, int idx, int splineIndex = -1)
    {
        if (!s.useBezier) return;
        int n = s.points.Count;
        if (n < 2) return;
        s.EnsureHandleCounts();

        Vector3 cur = s.points[idx];
        Vector3 prevPos;
        Vector3 nextPos;

        if (idx == 0)
        {

            if (drawModeChainSmoothing && s.linkToPrevious && splineIndex > 0 && data.splines.Count > splineIndex - 1)
            {
                var prevSpline = data.splines[splineIndex - 1];
                if (prevSpline.points.Count >= 2)
                    prevPos = prevSpline.points[prevSpline.points.Count - 2];
                else
                    prevPos = cur;
            }
            else
            {
                prevPos = cur;
            }
            nextPos = (n > 1) ? s.points[1] : cur;
        }
        else if (idx == n - 1)
        {

            prevPos = (n > 1) ? s.points[n - 2] : cur;
            if (drawModeChainSmoothing && splineIndex >= 0 && splineIndex + 1 < data.splines.Count)
            {
                var nextSpline = data.splines[splineIndex + 1];
                if (nextSpline.linkToPrevious && nextSpline.points.Count >= 2)
                    nextPos = nextSpline.points[1];
                else
                    nextPos = cur;
            }
            else
            {
                nextPos = cur;
            }
        }
        else
        {

            prevPos = s.points[idx - 1];
            nextPos = s.points[idx + 1];
        }

        Vector3 dir = nextPos - prevPos;
        float dlen = dir.magnitude;
        Vector3 tangent;
        
        if (dlen > 1e-4f)
        {
            tangent = dir.normalized;
        }
        else
        {

            if (idx < n - 1) tangent = (s.points[idx + 1] - cur).normalized;
            else if (idx > 0) tangent = (cur - s.points[idx - 1]).normalized;
            else tangent = Vector3.forward;
        }

        float localDist = 1f;
        if (idx < n - 1) localDist = Vector3.Distance(cur, s.points[idx + 1]);
        else if (idx > 0) localDist = Vector3.Distance(cur, s.points[idx - 1]);

        float handleLen = Mathf.Clamp(localDist * 0.35f, 0.1f, 20f);

        if (idx < n - 1) s.outHandles[idx] = cur + tangent * handleLen;
        if (idx > 0)     s.inHandles[idx]  = cur - tangent * handleLen;
    }

    private static void FilledRectangleCap(int controlID, Vector3 position, Quaternion rotation, float size, EventType eventType)
    {
        switch (eventType)
        {
            case EventType.Repaint:
                Vector3 camRight = Camera.current.transform.right * size;
                Vector3 camUp = Camera.current.transform.up * size;
                Vector3[] verts = new Vector3[]
                {
                    position - camRight - camUp,
                    position + camRight - camUp,
                    position + camRight + camUp,
                    position - camRight + camUp
                };
                Handles.DrawSolidRectangleWithOutline(verts, Color.white, Color.black);
                break;
            case EventType.Layout:
                HandleUtility.AddControl(controlID, HandleUtility.DistanceToCircle(position, size));
                break;
        }
    }

    private void DrawSplineHandles(WaterSpline s, bool isSelected)
    {
        Handles.color = s.debugColor;
        if (s.points.Count >= 2)
        {
            Vector3 prev = s.points[0];
            for (int i = 1; i < s.points.Count; i++)
            {
                Vector3 cur = s.points[i];
                Handles.DrawLine(prev, cur);
                prev = cur;
            }
            float halfW = s.width * 0.5f;
            for (int i = 0; i < s.points.Count - 1; i++)
            {
                Vector3 a = s.points[i];
                Vector3 b = s.points[i + 1];
                Vector3 dir = (b - a).normalized;
                Vector3 right = Vector3.Cross(dir, Vector3.up).normalized;
                Handles.DrawLine(a + right * halfW, a - right * halfW);
                Handles.DrawLine(b + right * halfW, b - right * halfW);
            }
        }
        DrawCurvePreview(s);
    }

    private void DrawCurvePreview(WaterSpline s)
    {
        if (s == null || s.points.Count < 2) return;
        int segCount = Mathf.Max(1, s.points.Count - 1);
        int samplesPerSegment = Mathf.Clamp(Mathf.CeilToInt((float)(data != null ? data.tessellationLevel : 4) * 2f), 8, 1024);
        Handles.color = s.debugColor;
        for (int seg = 0; seg < segCount; seg++)
        {
            Vector3 prev = WaterSplineSampler.SampleSegmentPosition(s, seg, 0f);
            for (int si = 1; si <= samplesPerSegment; si++)
            {
                float localT = (float)si / (float)samplesPerSegment;
                Vector3 cur = WaterSplineSampler.SampleSegmentPosition(s, seg, localT);
                Handles.DrawLine(prev, cur);
                prev = cur;
            }
        }
    }

    public WaterCreatorData GetData() => data;
    public int GetSelectedSplineIndex() => selectedSplineIndex;
    public Dictionary<int, Texture2D> GetRuntimeFlowCopies() => runtimeFlowCopies;
    public float GetBrushSize() => brushSize;
    public float GetBrushStrength() => brushStrength;
    public Vector2 GetLastMouseUV() => lastMouseUV;
    public Vector3 GetLastPaintWorldPos() => lastPaintWorldPos;
    public float GetLastPaintTime() => lastPaintTime;
    public void SetLastMouseUV(Vector2 value) => lastMouseUV = value;
    public void SetLastPaintWorldPos(Vector3 value) => lastPaintWorldPos = value;
    public void SetLastPaintTime(float value) => lastPaintTime = value;
    public void SetBrushSize(float value) => brushSize = value;
    public void SetBrushStrength(float value) => brushStrength = value;
    public void QueueGenerateAllPreviewsPublic() => QueueGenerateAllPreviews();

    private void AutoFitSplineToRiverbed(WaterSpline s)
    {
        if (s == null || s.points.Count < 2) return;
        Undo.RecordObject(data, "Auto-Fit Spline to Riverbed");
        s.EnsureHandleCounts();

        for (int i = 0; i < s.points.Count; i++)
        {
            Vector3 p = s.points[i];
            Vector3 prev = (i > 0) ? s.points[i - 1] : s.points[0];
            Vector3 next = (i < s.points.Count - 1) ? s.points[i + 1] : s.points[s.points.Count - 1];

            Vector3 dir = (next - prev);
            if (dir.sqrMagnitude < 0.001f) dir = Vector3.forward;
            Vector3 forward = dir.normalized;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

            Vector3 rayOrigin = p + Vector3.up * 1f;
            RaycastHit hitR, hitL;
            float maxScanDist = 50f;

            bool hasHitR = Physics.Raycast(rayOrigin, right, out hitR, maxScanDist, data.collisionMask);
            bool hasHitL = Physics.Raycast(rayOrigin, -right, out hitL, maxScanDist, data.collisionMask);

            if (hasHitR && hasHitL)
            {
                float newWidth = hitR.distance + hitL.distance;
                if (s.pointWidths.Count > i) s.pointWidths[i] = newWidth;

                Vector3 centerPoint = (hitR.point + hitL.point) * 0.5f;
                p.x = centerPoint.x;
                p.z = centerPoint.z;
                s.points[i] = p;
            }
            else if (hasHitR || hasHitL)
            {

                float dist = hasHitR ? hitR.distance : hitL.distance;
                if (s.pointWidths.Count > i) s.pointWidths[i] = dist * 2f;
            }
        }

        EditorUtility.SetDirty(data);
        QueueGenerateAllPreviews();
        Repaint();
        Debug.Log($"Auto-fitted spline '{s.name}' to riverbed geometry.");
    }
    }
}


    



