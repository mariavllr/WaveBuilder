using System.Collections.Generic;
using UnityEngine;

namespace AureDevGames
{
    [System.Serializable]
    public class WaveIndividualSettings
    {
        public float wavelength = 10f;
        public float amplitude = 0.5f;
        public float speed = 1f;
        public float steepness = 0.5f;
    }

    [System.Serializable]
    public class WaveSettings
    {
        [Header("General")]
        public float waveStrength = 1f;
        public float waveFrequency = 1f;
        public float waveSpeed = 1f;
        [Range(0f, 10f)] public float waveHeight = 1f;
        public float dominantDirection = 0f;
        public float waveChaos = 45f;

        [Header("Waves")]
        public WaveIndividualSettings wave1 = new WaveIndividualSettings { wavelength = 10f, amplitude = 0.5f, speed = 1f, steepness = 0.5f };
        public WaveIndividualSettings wave2 = new WaveIndividualSettings { wavelength = 7f, amplitude = 0.3f, speed = 1.2f, steepness = 0.4f };
        public WaveIndividualSettings wave3 = new WaveIndividualSettings { wavelength = 5f, amplitude = 0.15f, speed = 0.8f, steepness = 0.3f };
        public WaveIndividualSettings wave4 = new WaveIndividualSettings { wavelength = 3f, amplitude = 0.08f, speed = 1.5f, steepness = 0.2f };

        [Header("Foam")]
        public Color foamColor = Color.white;
        public float foamStrength = 1f;
        public float foamThreshold = 0.7f;
        public float foamWidth = 0.3f;
        public float foamNoiseTiling = 1f;

        [Header("River")]
        public float riverFlowDirection = 0f;
        public float riverDirectionality = 0.85f;
        public float riverFlowSpeed = 1.5f;
        public float riverLength = 50f;
        public float riverWidth = 5f;
        public float useUV = 0f;
    }

    [System.Serializable]
    public class WaterSectorGrid
    {
        public string name = "OceanSector";
        public Vector3 position = Vector3.zero;
        public float width = 100f;
        public float length = 100f;
        public int subdivisions = 64;
        public int sectorCountX = 1;
        public int sectorCountZ = 1;
        public int riverId = 0;
    }

    [CreateAssetMenu(fileName = "WaterCreatorData", menuName = "Water/AQUIS Pro Data")]
    public class WaterCreatorData : ScriptableObject
    {
        [Header("Preview Settings")]
        [Tooltip("The base material to clone when generating preview rivers.")]
        public Material previewMaterial;
 
        [HideInInspector]
        public List<string> sceneGUIDs = new List<string>();

        [HideInInspector]
        public string lastExportedPrefabPath = "";

        [Header("Mesh Generation Strategy")]
        [Tooltip("Number of length sample subdivisions per spline segment. Higher = smoother curves and better wave fidelity, but more polygons (Exponentially!).")]
        [Range(1, 2048)] public int tessellationLevel = 64;

        [Tooltip("Automatically expand/snap the mesh to collide with the riverbed walls in the scene.")]
        public bool snapToRiverbed = false;
        [Tooltip("LayerMask for detecting riverbed walls during mesh snapping.")]
        public LayerMask collisionMask = -1; 

        [Tooltip("Enable Adaptive Tessellation for performance. Generates LOD meshes for distance optimization.")]
        public bool adaptiveTessellation = false;

        [Tooltip("Tessellation level for LOD 1 (medium distance).")]
        [Range(1, 1024)] public int lod1Tessellation = 2;

        [Tooltip("Tessellation level for LOD 2 (far distance).")]
        [Range(1, 1024)] public int lod2Tessellation = 1;

        [Tooltip("Screen relative transition height for LOD 1. Lower means it switches further away.")]
        [Range(0.01f, 1f)] public float lod1ScreenHeight = 0.3f;

        [Tooltip("Screen relative transition height for LOD 2. Lower means it switches further away.")]
        [Range(0.01f, 1f)] public float lod2ScreenHeight = 0.1f;

        [HideInInspector]
        public List<WaterSpline> splines = new List<WaterSpline>();

        [HideInInspector]
        public List<WaterSectorGrid> sectors = new List<WaterSectorGrid>();

        [Header("UV Tiling Modifiers")]
        [Tooltip("Global multiplier for UV scaling along the river length. Adjust this to stretch or repeat normal maps.")]
        public float uvWorldScaleU = 5f;
        [Tooltip("Global multiplier for UV scaling across the river width.")]
        public float uvWorldScaleV = 1f;

        public enum FlowMapMode { Automatic = 0, Custom = 1 }
        public enum ProbeResolution { _64 = 64, _128 = 128, _256 = 256, _512 = 512 }
        public enum ReflectionProbeLayout { Longitudinal = 0, Grid = 1 }

        [Header("Flow Map Rendering")]
        [Tooltip("Select whether Flow Maps are calculated procedurally by slope/curve, or manually painted by the user.")]
        public FlowMapMode flowMapMode = FlowMapMode.Automatic;



        [Tooltip("Texture resolution width for the generated flow maps (X-axis/Length).")]
        public int flowMapWidth = 512;
        [Tooltip("Texture resolution height for the generated flow maps (Y-axis/Width).")]
        public int flowMapHeight = 32;

        [Header("Automatic Flow Map Physics")]
        [Tooltip("How much the vertical slope (Y delta) accelerates the water current.")]
        [Range(0f, 4f)] public float slopeInfluence = 1.0f;
        [Tooltip("Multiplier for the water speed at the exact mathematical center of the spline.")]
        [Range(0f, 4f)] public float centerSpeedMultiplier = 1.5f;
        [Tooltip("Multiplier for the water speed at the outer shores (edge friction).")]
        [Range(0f, 1f)] public float edgeSpeedMultiplier = 0.25f;
        [Tooltip("How sharp the transition from shore to center current is. Higher values mean the strong center current pushes closer to the edge.")]
        [Range(0.5f, 4f)] public float lateralFalloffPower = 1.2f;

        [Header("Lighting & Reflections")]
        [Tooltip("Automatically generate and distribute Reflection Probes along the river so the water reflects its surroundings realistically.")]
        public bool enableReflectionProbes = false;
        [Tooltip("Distance in meters between each Reflection Probe on the spline.")]
        [Range(5f, 500f)] public float probeSpacing = 50f;
        [Tooltip("Cubemap resolution per reflection probe. Higher = sharper reflections but more GPU cost.")]
        public ProbeResolution probeResolution = ProbeResolution._128;

        [Tooltip("How reflection probes are distributed. Longitudinal = along the center, Grid = covers entire width.")]
        public ReflectionProbeLayout reflectionProbeLayout = ReflectionProbeLayout.Longitudinal;

        [Tooltip("Vertical offset for reflection probes above the water surface. Adjust this if probes are getting clipped or are 'underground'.")]
        [Range(-50f, 50f)] public float reflectionProbeVerticalOffset = 0.5f;

        [Header("Wave & Foam Persistence")]
        public WaveSettings waveSettings = new WaveSettings();
    }
}



