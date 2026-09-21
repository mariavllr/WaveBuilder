using UnityEngine;

namespace AureDevGames
{
    [ExecuteInEditMode]
    public class WaterWaves : MonoBehaviour
    {
        
        [Tooltip("Global multiplier for wave intensity.")]
        [Range(0f, 10f)] public float waveStrength = 1f;
        [Tooltip("How many wave cycles fit across the mesh.")]
        [Range(0.01f, 8f)] public float waveFrequency = 1f;
        [Tooltip("Wave animation speed multiplier.")]
        [Range(0f, 5f)] public float waveSpeed = 1f;
        [Tooltip("Amplitude multiplier (auto-configured mode).")]
        [Range(0f, 10f)] public float waveHeight = 1f;

        [Space(10)]
        [Tooltip("The dominant direction the waves travel in degrees.")]
        public float dominantDirection = 0f;
        [Tooltip("How erratic/scattered the waves are from the dominant direction. 0 = perfectly parallel, 90+ = very chaotic.")]
        [Range(0f, 180f)] public float waveChaos = 45f;

        [Header("Wave 1")]
        [HideInInspector] public float direction1 = 0f;
        [Tooltip("Distance between wave crests.")] public float wavelength1 = 10f;
        [Tooltip("Vertical height of the wave.")] public float amplitude1 = 0.5f;
        [Tooltip("Speed at which this specific wave travels.")] public float speed1 = 1f;
        [Tooltip("Sharpness of the wave crest. High values make the wave look choppy.")] [Range(0f, 1f)] public float steepness1 = 0.5f;

        [Header("Wave 2")]
        [HideInInspector] public float direction2 = 30f;
        [Tooltip("Distance between wave crests.")] public float wavelength2 = 7f;
        [Tooltip("Vertical height of the wave.")] public float amplitude2 = 0.3f;
        [Tooltip("Speed at which this specific wave travels.")] public float speed2 = 1.2f;
        [Tooltip("Sharpness of the wave crest. High values make the wave look choppy.")] [Range(0f, 1f)] public float steepness2 = 0.4f;

        [Header("Wave 3")]
        [HideInInspector] public float direction3 = -15f;
        [Tooltip("Distance between wave crests.")] public float wavelength3 = 5f;
        [Tooltip("Vertical height of the wave.")] public float amplitude3 = 0.15f;
        [Tooltip("Speed at which this specific wave travels.")] public float speed3 = 0.8f;
        [Tooltip("Sharpness of the wave crest. High values make the wave look choppy.")] [Range(0f, 1f)] public float steepness3 = 0.3f;

        [Header("Wave 4")]
        [HideInInspector] public float direction4 = 60f;
        [Tooltip("Distance between wave crests.")] public float wavelength4 = 3f;
        [Tooltip("Vertical height of the wave.")] public float amplitude4 = 0.08f;
        [Tooltip("Speed at which this specific wave travels.")] public float speed4 = 1.5f;
        [Tooltip("Sharpness of the wave crest. High values make the wave look choppy.")] [Range(0f, 1f)] public float steepness4 = 0.2f;

        [Header("Foam")]
        [Tooltip("Color of the foam (Supports HDR for glowing effects).")]
        [ColorUsage(true, true)] public Color foamColor = Color.white;
        [Tooltip("Overall foam intensity multiplier.")]
        [Range(0f, 1f)] public float foamStrength = 1f;

        [Tooltip("Lower bound for wave height foam (0 = bottom of wave, 1 = absolute crest).")]
        [Range(0f, 1f)] public float foamThreshold = 0.7f;
        [Tooltip("Width of the foam band: 0 = sharp edge, 1 = very wide soft fringe.")]
        [Range(0f, 1f)] public float foamWidth = 0.3f;
        [Tooltip("Scale for the foam noise texture calculated inside the Shader Graph.")]
        public float foamNoiseTiling = 1f;



        [Header("River Settings")]
        [Tooltip("The base flow direction angle (in degrees).")] private float riverFlowDirection = 0f;
        [Tooltip("How much the river flow aligns to the dominant direction vs spline direction.")] [Range(0f, 1f)] private float riverDirectionality = 0.85f;
        [Tooltip("General speed of the river flow.")] private float riverFlowSpeed = 1.5f;
        [Tooltip("Approx. river length in metres.")] public float riverLength = 50f;
        [Tooltip("Approx. river width in metres.")] public float riverWidth = 5f;

        [Tooltip("Force use of UV-based flow (Splines) instead of global wave direction. Usually set to 1 for rivers.")]
        [Range(0f, 1f)] public float useUV = 0f;

        [Header("Persistence")]
        public WaterCreatorData data;

        private MeshRenderer[] _renderers;
        private bool _isDirty = true;
        private MaterialPropertyBlock _mpb;

        private const int kWaveCount = 4;

        [HideInInspector] public float autoMeshLength = 10f;
        [HideInInspector] public float autoMeshWidth = 10f;

        void OnEnable()
        {
            _mpb = new MaterialPropertyBlock();
            RefreshRenderers();
            
            if (data != null && Application.isPlaying)
            {
                LoadFromDataAsset();
            }

            _isDirty = true;
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            _isDirty = true;
        }
#endif

        public void RefreshRenderers()
        {
            var self = GetComponent<MeshRenderer>();
            _renderers = self != null
                ? new[] { self }
                : GetComponentsInChildren<MeshRenderer>(false);

            _isDirty = true;
        }

        void Update()
        {
            if (_renderers == null || _renderers.Length == 0) RefreshRenderers();

            if (!_isDirty && !Application.isPlaying) return;

            if (_renderers == null || _renderers.Length == 0) return;

            SyncMaterialProperties();

            _isDirty = false;
        }

        public void SyncMaterialProperties()
        {
            if (_renderers == null || _renderers.Length == 0) RefreshRenderers();
            if (_renderers == null || _renderers.Length == 0) return;

            direction1 = dominantDirection + StableHash(1) * waveChaos;
            direction2 = dominantDirection + StableHash(2) * waveChaos;
            direction3 = dominantDirection + StableHash(3) * waveChaos;
            direction4 = dominantDirection + StableHash(4) * waveChaos;

            foreach (var r in _renderers)
            {
                if (!r || (!r.sharedMaterial && !r.material)) continue;

                r.GetPropertyBlock(_mpb);

                _mpb.SetFloat("_UseUV", useUV);
                _mpb.SetFloat("_HorizontalStrength", 1f);
                _mpb.SetFloat("_RiverLength", riverLength);
                _mpb.SetFloat("_RiverWidth", riverWidth);
                _mpb.SetFloat("_FoamThreshold", foamThreshold);
                _mpb.SetFloat("_FoamStrength", foamStrength);
                _mpb.SetFloat("_FoamWidth", foamWidth);
                _mpb.SetFloat("_FoamNoiseTiling", foamNoiseTiling);
                _mpb.SetColor("_FoamColor", foamColor);

                SetWave(0, direction1, wavelength1, amplitude1, speed1, steepness1);
                SetWave(1, direction2, wavelength2, amplitude2, speed2, steepness2);
                SetWave(2, direction3, wavelength3, amplitude3, speed3, steepness3);
                SetWave(3, direction4, wavelength4, amplitude4, speed4, steepness4);

                r.SetPropertyBlock(_mpb);
            }
        }

        void ZeroWaves()
        {
            foreach (var r in _renderers)
            {
                if (!r) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetVector("_Wave0_AmpSteep", Vector4.zero);
                _mpb.SetVector("_Wave1_AmpSteep", Vector4.zero);
                _mpb.SetVector("_Wave2_AmpSteep", Vector4.zero);
                _mpb.SetVector("_Wave3_AmpSteep", Vector4.zero);
                r.SetPropertyBlock(_mpb);
            }
        }

        public void ConfigureForMeshSize(float length, float width)
        {
            autoMeshLength = length; autoMeshWidth = width;

            if (data == null)
            {
                ApplyAutoConfig();
            }
            else
            {
                LoadFromDataAsset();
            }

            _isDirty = true;
        }

        void ApplyAutoConfig()
        {
            float L = Mathf.Max(autoMeshLength, 0.5f);
            float W = Mathf.Max(autoMeshWidth, 0.5f);
            float amp = Mathf.Min(L, W) * 0.02f;

            wavelength1 = (L / 3f); amplitude1 = amp; speed1 = 1.2f; steepness1 = 0.5f;
            wavelength2 = (L / 4.5f); amplitude2 = amp * 0.65f; speed2 = 1.0f; steepness2 = 0.4f;
            wavelength3 = (W / 2f); amplitude3 = amp * 0.4f; speed3 = 0.9f; steepness3 = 0.3f;
            wavelength4 = (W / 3f); amplitude4 = amp * 0.2f; speed4 = 1.5f; steepness4 = 0.2f;
        }

        static float StableHash(int waveIndex)
        {
            return Mathf.Sin(waveIndex * 127.1f + 311.7f);
        }

        void SetWave(int i, float dirDeg, float wavelength, float amplitude, float speed, float steepness)
        {
            float finalWl = wavelength / Mathf.Max(0.01f, waveFrequency);
            float finalAmp = amplitude * waveHeight;
            float finalSpd = speed * waveSpeed;

            float dirRad = dirDeg * Mathf.Deg2Rad;
            float freq = 2f * Mathf.PI / Mathf.Max(0.0001f, finalWl);
            float phase = finalSpd * Mathf.Sqrt(9.81f * freq);
            Vector2 dir = new Vector2(Mathf.Cos(dirRad), Mathf.Sin(dirRad)).normalized;

            float actualAmp = finalAmp * waveStrength;
            float maxQ = 1f / Mathf.Max(0.0001f, freq * actualAmp * kWaveCount);
            float q = Mathf.Min(steepness / Mathf.Max(0.0001f, freq * actualAmp * 4f), maxQ);

            _mpb.SetVector($"_Wave{i}_DirFreqPhase", new Vector4(dir.x, dir.y, freq, phase));
            _mpb.SetVector($"_Wave{i}_AmpSteep", new Vector4(actualAmp, q, 0f, 0f));
        }

        public float GetWaveHeight(Vector3 worldPos)
        {
            float h = 0f;

            float wlMult = 1f / Mathf.Max(0.01f, waveFrequency);
            float ampMult = waveHeight;
            float spdMult = waveSpeed;

            h += WH(worldPos, direction1, wavelength1 * wlMult, amplitude1 * ampMult, speed1 * spdMult);
            h += WH(worldPos, direction2, wavelength2 * wlMult, amplitude2 * ampMult, speed2 * spdMult);
            h += WH(worldPos, direction3, wavelength3 * wlMult, amplitude3 * ampMult, speed3 * spdMult);
            h += WH(worldPos, direction4, wavelength4 * wlMult, amplitude4 * ampMult, speed4 * spdMult);
            return h * waveStrength;
        }

        float WH(Vector3 pos, float dirDeg, float wl, float amp, float spd)
        {
            float freq = 2f * Mathf.PI / Mathf.Max(0.0001f, wl);
            float phase = spd * Mathf.Sqrt(9.81f * freq);
            float dirRad = dirDeg * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(dirRad), Mathf.Sin(dirRad)).normalized;

            float spatialOffset = Mathf.Sin(pos.x * 0.1f) * Mathf.Cos(pos.z * 0.15f) * 1.5f;

            float theta = freq * (pos.x * dir.x + pos.z * dir.y) + phase * Time.time + spatialOffset;

            return amp * Mathf.Sin(theta);
        }

        public void SaveToDataAsset()
        {
            if (data == null) return;
#if UNITY_EDITOR
            UnityEditor.Undo.RecordObject(data, "Save Wave Settings to Asset");
#endif
            var ws = data.waveSettings;
            ws.waveStrength = waveStrength;
            ws.waveFrequency = waveFrequency;
            ws.waveSpeed = waveSpeed;
            ws.waveHeight = waveHeight;
            ws.dominantDirection = dominantDirection;
            ws.waveChaos = waveChaos;

            ws.wave1.wavelength = wavelength1; ws.wave1.amplitude = amplitude1; ws.wave1.speed = speed1; ws.wave1.steepness = steepness1;
            ws.wave2.wavelength = wavelength2; ws.wave2.amplitude = amplitude2; ws.wave2.speed = speed2; ws.wave2.steepness = steepness2;
            ws.wave3.wavelength = wavelength3; ws.wave3.amplitude = amplitude3; ws.wave3.speed = speed3; ws.wave3.steepness = steepness3;
            ws.wave4.wavelength = wavelength4; ws.wave4.amplitude = amplitude4; ws.wave4.speed = speed4; ws.wave4.steepness = steepness4;

            ws.foamColor = foamColor;
            ws.foamStrength = foamStrength;
            ws.foamThreshold = foamThreshold;
            ws.foamWidth = foamWidth;
            ws.foamNoiseTiling = foamNoiseTiling;

            ws.riverFlowDirection = riverFlowDirection;
            ws.riverDirectionality = riverDirectionality;
            ws.riverFlowSpeed = riverFlowSpeed;
            ws.riverLength = riverLength;
            ws.riverWidth = riverWidth;
            ws.useUV = useUV;

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(data);
#endif
        }

        public void LoadFromDataAsset()
        {
            if (data == null) return;
            var ws = data.waveSettings;
            waveStrength = ws.waveStrength;
            waveFrequency = ws.waveFrequency;
            waveSpeed = ws.waveSpeed;
            waveHeight = ws.waveHeight;
            dominantDirection = ws.dominantDirection;
            waveChaos = ws.waveChaos;

            wavelength1 = ws.wave1.wavelength; amplitude1 = ws.wave1.amplitude; speed1 = ws.wave1.speed; steepness1 = ws.wave1.steepness;
            wavelength2 = ws.wave2.wavelength; amplitude2 = ws.wave2.amplitude; speed2 = ws.wave2.speed; steepness2 = ws.wave2.steepness;
            wavelength3 = ws.wave3.wavelength; amplitude3 = ws.wave3.amplitude; speed3 = ws.wave3.speed; steepness3 = ws.wave3.steepness;
            wavelength4 = ws.wave4.wavelength; amplitude4 = ws.wave4.amplitude; speed4 = ws.wave4.speed; steepness4 = ws.wave4.steepness;

            foamColor = ws.foamColor;
            foamStrength = ws.foamStrength;
            foamThreshold = ws.foamThreshold;
            foamWidth = ws.foamWidth;
            foamNoiseTiling = ws.foamNoiseTiling;

            riverFlowDirection = ws.riverFlowDirection;
            riverDirectionality = ws.riverDirectionality;
            riverFlowSpeed = ws.riverFlowSpeed;
            riverLength = ws.riverLength;
            riverWidth = ws.riverWidth;
            useUV = ws.useUV;

            SyncMaterialProperties();
            _isDirty = true;
        }
    }
}

