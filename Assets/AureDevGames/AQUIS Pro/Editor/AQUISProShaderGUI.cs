#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

namespace AureDevGames
{
    public class AQUISProShaderGUI : ShaderGUI
    {
        private HashSet<string> drawnProperties = new HashSet<string>();


        private static bool foldGeneral = true;
        private static bool foldFlow = true;
        private static bool foldDepth = true;
        private static bool foldHozFresnel = true;
        private static bool foldSurfFoam = true;
        private static bool foldIntersecFoam = true;
        private static bool foldLighting = true;
        private static bool foldWavesRelated = true;
        private static bool foldCaves = true;
        private static bool foldReflection = true;
        private static bool foldCaustics = true;


        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            drawnProperties.Clear();

            GUILayout.Space(5);
            EditorGUILayout.HelpBox("AQUIS Pro Material Inspector\nManually categorized for full control. Hover over labels for tooltips.", MessageType.Info);
            GUILayout.Space(10);

            // ==========================================
            // 1. GENERAL
            // ==========================================
            DrawCategory("General", ref foldGeneral, new string[] {
            "_Hue", "_Saturation", "_Value", "_Alpha", "_WaterBaseColor"
        }, materialEditor, properties);

            // ==========================================
            // 2. FLOW MAP SETTINGS
            // ==========================================
            DrawCategory("Flow Map Settings", ref foldFlow, new string[] {
            "_FlowMap_1", "_FlowMapLerpBlend", "_FlowMapTextureColor", "_FlowMap","_FlowMapRemap",
            "_FlowMapTilingOffset", "_Tiling", "_FlowMapStrength",
            "_SampleJump", "_SampleDivideFactor", "_CycleLength", "_RampsCurve", "_NoiseTex", "_NoiseStrength", "_MainTex",
            "_MainTexTiling", "_BlueMaskMultiplier", "_DebugFlowMap", "_R", "_G", "_B", "_FlowReflectionStrength"
        }, materialEditor, properties);

            // ==========================================
            // 3. DEPTH
            // ==========================================
            DrawCategory("Depth", ref foldDepth, new string[] {
            "_DepthFadeDistance", "_RefractionStrength","_ColorAbsorbtionFactor","_RefractionBaseSpeed", "_RefractionBaseStrength",
            "_RefractionEdgeSoftness", "_CustomDepthColor", "_TopColor", "_DeepColor"
        }, materialEditor, properties);

            // ==========================================
            // 4. HORIZON FRESNEL
            // ==========================================
            DrawCategory("Horizon Fresnel", ref foldHozFresnel, new string[] {
            "_HorizonDistance", "_HorizonColor", "_LinearHorizon"
        }, materialEditor, properties);

            // ==========================================
            // 5. SURFACE FOAM
            // ==========================================
            DrawCategory("Surface Foam", ref foldSurfFoam, new string[] {
            "_SurfaceFoamDirection", "_SurfaceFoamSpeed", "_SurfaceFoamTiling", "_SurfaceFoamDistorsion",
            "_SurfaceFoamTexture", "_SurfaceFoamColor", "_SecondaryFoamTex", "_SecondaryFoamColor",
            "_SecondaryFoamSpeedMultiplier", "_FoamUVsOffset"
        }, materialEditor, properties);

            // ==========================================
            // 6. INTERSECTION FOAM
            // ==========================================
            DrawCategory("Intersection Foam", ref foldIntersecFoam, new string[] {
            "_IntersectionFoamTexture", "_IntersectionFoamColor", "_IntersectionFoamDepth",
            "_IntersectionFoamTiling", "_IntersectionFoamSpeed", "_IntersectionFoamCutoff",
            "_IntersectionFoamDirection"
        }, materialEditor, properties);

            // ==========================================
            // 7. LIGHTING
            // ==========================================
            DrawCategory("Lighting", ref foldLighting, new string[] {
            "_HaloGloss", "_HaloIntensity", "_BaseGloss", "_BaseSpecIntensity", "_SunGloss",
            "_SunSpecIntensity", "_SunF0", "_FresnelSpecPow", "_SparkleThreshold","_SparkleSharpness", "_SparkleIntensity",
            "_SparkleColor", "_SparkleTiling", "_SparkleSpeed", "_AddLightIntensity", "_SmoothPointLights",
            "_SpecularPowerAdditional", "_SparkleNoiseScale", "_ShadowStrength", "_NormalMap", "_NormalStrength",
            "_NormalTiling1", "_NormalSpeed1", "_NormalTiling2", "_NormalSpeed2", "_WaterDeepColor", "_SSSColor",
            "_SSSIntensity", "_CrestThreshold", "_CrestIntensity"
        }, materialEditor, properties);


            // ==========================================
            // 8. WAVES RELATED
            // ==========================================
            DrawCategory("Waves Related", ref foldWavesRelated, new string[] {
            "_FoamColor", "_FoamStrength", "_FoamThreshold", "_FoamTipThreshold", "_FoamWidth",
            "_FoamNoiseTiling", "_FoamTexture"
        }, materialEditor, properties);

            // ==========================================
            // 9. CAVES
            // ==========================================
            DrawCategory("Caves", ref foldCaves, new string[] {
            "_Caves", "_CaveTexture", "_CaveColor", "_CaveScale", "_CaveDistortion", "_CaveOffset"
        }, materialEditor, properties);


            // ==========================================
            // 10. REFLECTION
            // ==========================================
            DrawCategory("Reflection", ref foldReflection, new string[] {
            "_ReflectionMode", "_ReflectionBlend", "_PerceptualGlosiness", "_RealisticReflectionIntensity",
            "_ReflectionNormalSmoothness", "_SkyboxCubemap", "_ReflectionJitter", "_JitterNoiseScale"
        }, materialEditor, properties);


            // ==========================================
            // 11. CAUSTICS
            // ==========================================
            DrawCategory("Caustics", ref foldCaustics, new string[] {
            "_CausticsTex", "_CausticsScale", "_CausticsSpeed", "_DepthCausticsDistance",
            "_DepthCausticsPower", "_CausticsColor", "_IncludeSceneColor"
        }, materialEditor, properties);





            EditorGUILayout.Space(20);
            materialEditor.RenderQueueField();
            materialEditor.EnableInstancingField();
            materialEditor.DoubleSidedGIField();
        }


        private void DrawCategory(string title, ref bool foldState, string[] internalNames, MaterialEditor me, MaterialProperty[] allProps)
        {
            foldState = EditorGUILayout.BeginFoldoutHeaderGroup(foldState, title);
            if (foldState)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                foreach (string name in internalNames)
                {
                    MaterialProperty p = FindProperty(name.Trim(), allProps, false);
                    if (p != null)
                    {
                        DrawField(me, p);
                        drawnProperties.Add(p.name);
                    }
                }
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            EditorGUILayout.Space(2);
        }


        private void DrawField(MaterialEditor me, MaterialProperty prop)
        {
            GUIContent label = new GUIContent(prop.displayName);
            label.tooltip = GetTooltip(prop.name); // Search by _InternalName for accuracy
            me.ShaderProperty(prop, label);
        }


        private string GetTooltip(string internalName)
        {
            switch (internalName)
            {
                // --- GENERAL ---
                case "_Hue": return "Adjusts the overall hue (color shift) of the water.";
                case "_Saturation": return "Controls color vividness and intensity.";
                case "_Value": return "Sets the global brightness of the material.";
                case "_Alpha": return "Global transparency. 0 is invisible, 1 is opaque.";
                case "_WaterBaseColor": return "Initial base tint of the water surface.";

                // --- FLOW MAP ---
                case "_FlowMap_1": return "Enables or disables the river current logic.";
                case "_FlowMap": return "Texture defining direction (RG) and speed (B).";
                case "_FlowMapLerpBlend": return "Smooths transitions between flow animation frames.";
                case "_FlowMapRemap": return "Remaps flow vectors for range adjustment.";
                case "_FlowMapTextureColor": return "Tints the generated flow patterns.";
                case "_FlowMapStrength": return "Intensity of surface distortion from flow.";
                case "_FlowMapTilingOffset": return "Scaling and offset for the flow texture.";
                case "_Tiling": return "Global tiling for ripples and patterns.";
                case "_SampleJump": return "Phase offset to break visual repetition.";
                case "_SampleDivideFactor": return "Frequency breakup factor.";
                case "_CycleLength": return "Loop duration in seconds for flow animation.";
                case "_RampsCurve": return "Falloff curve for highlight transitions.";
                case "_NoiseTex": return "Master noise for surface breakup and foam.";
                case "_NoiseStrength": return "Global intensity of noise-based features.";
                case "_MainTex": return "Optional overlay texture for the surface.";
                case "_MainTexTiling": return "Scaling for the overlay texture.";
                case "_BlueMaskMultiplier": return "Masking factor for deep water effects.";
                case "_DebugFlowMap": return "Visualizes the current vectors for debugging.";
                case "_R": case "_G": case "_B": return "Channel-specific physical multipliers.";
                case "_FlowReflectionStrength": return "How much reflections are influenced by flow.";

                // --- DEPTH ---
                case "_DepthFadeDistance": return "Metres for the water to reach full deep color.";
                case "_RefractionStrength": return "Bending intensity for objects under water.";
                case "_ColorAbsorbtionFactor": return "How fast light is swallowed by depth.";
                case "_RefractionBaseSpeed": return "Animation rate of refractive distortion.";
                case "_RefractionBaseStrength": return "Master multiplier for underwater distortion.";
                case "_RefractionEdgeSoftness": return "Linear falloff of refraction at depth edges.";
                case "_CustomDepthColor": return "Manual tint for depth transitions.";
                case "_TopColor": return "Color tint at the shallowest points.";
                case "_DeepColor": return "Base tint for deep abyss regions.";

                // --- HORIZON ---
                case "_HorizonDistance": return "Distance where water geometry fades to sky.";
                case "_HorizonColor": return "Tint of the fog at the horizon line.";
                case "_LinearHorizon": return "Linearity of the horizon fade transition.";

                // --- SURFACE FOAM ---
                case "_SurfaceFoamDirection": return "Direction vector for surface foam movement.";
                case "_SurfaceFoamSpeed": return "Movement speed of the surface foam.";
                case "_SurfaceFoamTiling": return "Scaling of the surface foam detail.";
                case "_SurfaceFoamDistorsion": return "Wavy distortion applied to surface foam.";
                case "_SurfaceFoamTexture": return "Primary texture for surface bubbling.";
                case "_SurfaceFoamColor": return "Tint color for surface foam.";
                case "_SecondaryFoamTex": return "Extra foam detail pattern.";
                case "_SecondaryFoamColor": return "Tint for secondary foam layer.";
                case "_SecondaryFoamSpeedMultiplier": return "Speed ratio for the secondary foam.";
                case "_FoamUVsOffset": return "Positional shift for foam mapping.";

                // --- INTERSECTION FOAM ---
                case "_IntersectionFoamTexture": return "Pattern for foam touching shoreline.";
                case "_IntersectionFoamColor": return "Color for shore-based foam.";
                case "_IntersectionFoamDepth": return "Distance foam extends from solid edges.";
                case "_IntersectionFoamTiling": return "Scale of the edge foam pattern.";
                case "_IntersectionFoamSpeed": return "Movement rate along solid edges.";
                case "_IntersectionFoamCutoff": return "Hardness/Softness of the foam boundary.";
                case "_IntersectionFoamDirection": return "Direction of edge foam flow.";

                // --- LIGHTING ---
                case "_HaloGloss": return "Smoothness of the sun glow effect.";
                case "_HaloIntensity": return "Brightness of the glow around sun reflections.";
                case "_BaseGloss": return "General surface reflectivity and smoothness.";
                case "_BaseSpecIntensity": return "Overall power of shiny highlights.";
                case "_SunGloss": return "Sharpness of the primary sun reflection.";
                case "_SunSpecIntensity": return "Direct sun highlight brightness.";
                case "_SunF0": return "Reflectivity index at perpendicular angles.";
                case "_FresnelSpecPow": return "Curve power for edge reflections.";
                case "_SparkleThreshold": return "Minimum light needed to trigger sparkles.";
                case "_SparkleSharpness": return "Visual crispness of single sun sparkles.";
                case "_SparkleIntensity": return "Overall brightness of wave-top sparkles.";
                case "_SparkleColor": return "Tint color for wave highlights.";
                case "_SparkleTiling": return "Density of the sparkle noise pattern.";
                case "_SparkleSpeed": return "Flutter speed of the sun sparkles.";
                case "_AddLightIntensity": return "Power of additional specular highlights.";
                case "_SmoothPointLights": return "Softness factor for point light reflections.";
                case "_SpecularPowerAdditional": return "Extra power for high-intensity lights.";
                case "_SparkleNoiseScale": return "Size of noise breakup for sparkles.";
                case "_ShadowStrength": return "How dark shadows appear on the water.";
                case "_NormalMap": return "Master texture for surface rippling.";
                case "_NormalStrength": return "Bumpy intensity of surface waves.";
                case "_NormalTiling1": case "_NormalTiling2": return "Size of detail ripple patterns.";
                case "_NormalSpeed1": case "_NormalSpeed2": return "Animation rate of detail ripples.";
                case "_WaterDeepColor": return "Alternative deep color used in lighting math.";
                case "_SSSColor": return "Sub-Surface Scattering tint (light in waves).";
                case "_SSSIntensity": return "Intensity of light scattering through crests.";
                case "_CrestThreshold": return "Wave height needed to spawn foam.";
                case "_CrestIntensity": return "Brightness of foam at wave peaks.";

                // --- WAVES RELATED ---
                case "_FoamColor": return "Global foam tint color.";
                case "_FoamStrength": return "Master multiplier for all foam visibility.";
                case "_FoamThreshold": return "Sensitivity for foam generation height.";
                case "_FoamTipThreshold": return "Limits foam to only the sharpest wave tips.";
                case "_FoamWidth": return "Spread width of foam on wave crests.";
                case "_FoamNoiseTiling": return "Scale of noise breaking up foam layers.";
                case "_FoamTexture": return "Detail texture for all foam types.";

                // --- CAVES ---
                case "_Caves": return "Master multiplier for cave darkening logic.";
                case "_CaveTexture": return "Texture used to map occluded/shadowed areas.";
                case "_CaveColor": return "Tint applied to shadowed/cave regions.";
                case "_CaveScale": return "Scaling factor for cave mapping projection.";
                case "_CaveDistortion": return "Amount of shadow applied under geometry.";
                case "_CaveOffset": return "Positional shift for cave logic.";

                // --- REFLECTION ---
                case "_ReflectionMode": return "Sets source: 0=Cubemap, 1=Probes.";
                case "_ReflectionBlend": return "Mix between skybox and local world reflections.";
                case "_PerceptualGlosiness": return "Gamma-corrected smoothness factor.";
                case "_RealisticReflectionIntensity": return "Physical scaling factor for reflections.";
                case "_ReflectionNormalSmoothness": return "How much ripples affect reflection clarity.";
                case "_SkyboxCubemap": return "Static texture for panoramic reflections.";
                case "_ReflectionJitter": return "Distortion factor for mirrored objects.";
                case "_JitterNoiseScale": return "Size of noise used for reflection distortion.";

                // --- CAUSTICS ---
                case "_CausticsTex": return "Light pattern cast on submerged surfaces.";
                case "_CausticsScale": return "Size of the underwater light rays.";
                case "_CausticsSpeed": return "Animation rate of moving light patterns.";
                case "_DepthCausticsDistance": return "Depth limit for caustic projection.";
                case "_DepthCausticsPower": return "Intensity falloff of caustics by depth.";
                case "_CausticsColor": return "Tint color for underwater light rays.";
                case "_IncludeSceneColor": return "Captures background (required for all effects).";

                default: return "";

            }
        }
    }
#endif
}

