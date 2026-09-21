#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AureDevGames
{
    [CustomEditor(typeof(WaterWaves))]
    public class WaterWavesEditor : Editor
    {

        private static bool _showFoam = true;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            WaterWaves waves = (WaterWaves)target;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Persistence", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("data"), new GUIContent("Data Asset", "The ScriptableObject where this configuration will be saved."));
            
            if (waves.data != null)
            {
                GUI.backgroundColor = new Color(0.7f, 1f, 0.7f);
                if (GUILayout.Button("Save configuration to data set", GUILayout.Height(30)))
                {
                    waves.SaveToDataAsset();
                }
                GUI.backgroundColor = Color.white;

                if (GUILayout.Button("Load from data set"))
                {
                    if (EditorUtility.DisplayDialog("Load configuration?", "This will overwrite current wave settings with values from the data asset. Continue?", "Yes", "No"))
                    {
                        Undo.RecordObject(waves, "Load Wave Settings");
                        waves.LoadFromDataAsset();
                        serializedObject.Update();
                        EditorUtility.SetDirty(waves);
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Assign a WaterCreatorData asset to enable persistence.", MessageType.Info);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("General", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("waveStrength"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("waveFrequency"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("waveSpeed"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("waveHeight"));
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Wave Direction & Chaos", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("dominantDirection"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("waveChaos"));
            EditorGUILayout.Space();

            DrawWave("Wave 1", "wavelength1", "amplitude1", "speed1", "steepness1");
            DrawWave("Wave 2", "wavelength2", "amplitude2", "speed2", "steepness2");
            DrawWave("Wave 3", "wavelength3", "amplitude3", "speed3", "steepness3");
            DrawWave("Wave 4", "wavelength4", "amplitude4", "speed4", "steepness4");

            EditorGUILayout.Space();
            _showFoam = EditorGUILayout.BeginFoldoutHeaderGroup(_showFoam, "Foam");
            if (_showFoam)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(serializedObject.FindProperty("foamColor"),
                    new GUIContent("Color", "Color of the foam (Supports HDR for glowing effects)."));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("foamStrength"),
                    new GUIContent("Strength", "Overall foam intensity multiplier."));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("foamThreshold"),
                    new GUIContent("Crest Foam Threshold",
                        "Height ratio above which foam appears on wave crests. "
                        + "0 = foam everywhere, 1 = foam only at the very top."));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("foamWidth"),
                    new GUIContent("Band Width",
                        "Width of the foam transition: 0 = sharp edge, 1 = wide soft band."));

                EditorGUILayout.Space();
                EditorGUILayout.PropertyField(serializedObject.FindProperty("foamNoiseTiling"),
                    new GUIContent("Noise Tiling", "Scale for the foam noise texture calculated inside the Shader Graph."));

                
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();



            EditorGUILayout.Space();
            EditorGUILayout.LabelField("River Settings", EditorStyles.boldLabel);
         
            EditorGUILayout.PropertyField(serializedObject.FindProperty("riverLength"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("riverWidth"));

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawWave(string label,
            string wlProp, string ampProp, string spdProp, string steepProp)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(serializedObject.FindProperty(wlProp), new GUIContent("Wavelength", "Distance between wave crests."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(ampProp), new GUIContent("Amplitude", "Vertical height of the wave."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(spdProp), new GUIContent("Speed", "Speed at which this specific wave travels."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(steepProp), new GUIContent("Steepness", "Sharpness of the wave crest. High values make the wave look choppy."));
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);
        }
    }
}
#endif

