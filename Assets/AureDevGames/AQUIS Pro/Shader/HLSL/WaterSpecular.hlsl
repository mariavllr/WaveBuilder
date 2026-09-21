#ifndef WATER_SPECULAR_INCLUDED
#define WATER_SPECULAR_INCLUDED

#ifndef SHADERGRAPH_PREVIEW
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl"
#endif

// ─────────────────────────────────────────────────────────────────────────────
// PRAGMAS
// Estas directivas generan las variantes de keyword necesarias para que URP
// active los paths correctos de sombras y luces en cada renderer.
// Son procesadas por el compilador de Unity aunque estén en un .hlsl incluido
// desde un Custom Function node de Shader Graph.
// ─────────────────────────────────────────────────────────────────────────────
#pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
#pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
#pragma multi_compile _ _FORWARD_PLUS
#pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
#pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
#pragma multi_compile _ _MAIN_LIGHT_SHADOWS
#pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
#pragma multi_compile _ _SHADOWS_SOFT


float3 _WS_FresnelSchlick(float cosTheta, float3 F0)
{
    return F0 + (1.0 - F0) * pow(saturate(1.0 - cosTheta), 5.0);
}

float _WS_BlinnPhongBright(float NdotH, float gloss)
{
    return pow(saturate(NdotH), gloss) * ((gloss + 2.0) / 8.0);
}

float _WS_SparkleMask(float noise, float threshold, float sharpness)
{
    float t = saturate((noise - threshold) / max(0.0001, 1.0 - threshold));
    return pow(t, max(sharpness * 10.0, 1.0));
}

#define WATER_SPEC_INPUTS \
    float3 NormalWS, float3 ViewWS, float3 PositionWS, \
    float HaloGloss, float HaloIntensity, \
    float BaseGloss, float BaseIntensity, \
    float SunGloss, float SunIntensity, float SunF0, float FresnelBoost, \
    float SparkleNoise, float SparkleThreshold, float SparkleSharpness, float SparkleIntensity, float3 SparkleColor, \
    float ShadowStrength, \
    float2 FlowDir, float FlowSpecularStrength, \
    float3 WaterDeepColor, float3 SSSColor, float SSSIntensity, \
    float CrestMask, float CrestThreshold, float CrestIntensity

#define WATER_SPEC_PASS \
    NormalWS, ViewWS, PositionWS, HaloGloss, HaloIntensity, BaseGloss, BaseIntensity, SunGloss, SunIntensity, SunF0, FresnelBoost, SparkleNoise, SparkleThreshold, SparkleSharpness, SparkleIntensity, SparkleColor, ShadowStrength, FlowDir, FlowSpecularStrength, \
    WaterDeepColor, SSSColor, SSSIntensity, CrestMask, CrestThreshold, CrestIntensity

void WaterSpecular_float(
    WATER_SPEC_INPUTS,
    out float3 Specular,
    out float3 Sparkles,
    out float3 Shadows,
    out float  MainLightIntensity)
{
#if defined(SHADERGRAPH_PREVIEW)
    float3 mainDir    = normalize(float3(0.5, 1.0, 0.3));
    float3 mainColor  = float3(1.0, 0.98, 0.95);
    float shadowAtten = 1.0;

#else
    // ── Obtención de shadow coord ─────────────────────────────────────────────
    // TransformWorldToShadowCoord necesita al menos una de las keywords de
    // sombras activa para devolver datos válidos. Si no hay ninguna, usamos
    // un coord nulo que produce shadowAttenuation = 1 (sin coste).
    #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
        float4 shadowCoord = TransformWorldToShadowCoord(PositionWS);
    #else
        float4 shadowCoord = float4(0, 0, 0, 1);
    #endif

    // ── GetMainLight según renderer ───────────────────────────────────────────
    // Forward+ expone una sobrecarga de 3 argumentos que incorpora posWS para
    // seleccionar la cascade correcta en el cluster buffer. Esta sobrecarga
    // NO existe en Forward clásico ni en Deferred → error de compilación si
    // se llama sin la guard _FORWARD_PLUS.
    // El tercer argumento es half4 (shadowMask), no float: pasamos 1.0 como
    // half4(1,1,1,1) para indicar que no hay baked occlusion.
    #if defined(_FORWARD_PLUS)
        Light mainLight = GetMainLight(shadowCoord, PositionWS, half4(1, 1, 1, 1));
    #else
        // Forward clásico y Deferred clásico: sobrecarga de 1 argumento,
        // disponible en todas las versiones de URP.
        Light mainLight = GetMainLight(shadowCoord);
    #endif

    float rawShadow   = mainLight.shadowAttenuation;
    float shadowAtten = lerp(1.0, rawShadow, ShadowStrength);
    float3 mainDir    = mainLight.direction;
    float3 mainColor  = mainLight.color;
#endif

    Shadows = float3(shadowAtten, shadowAtten, shadowAtten);

    MainLightIntensity = dot(mainColor, float3(1.0/3.0, 1.0/3.0, 1.0/3.0));

    HaloGloss = max(HaloGloss, 1.0);
    BaseGloss = max(BaseGloss, 1.0);
    SunGloss  = max(SunGloss,  1.0);
    float cameraZenith = saturate(ViewWS.y);

    float2 flowScaled       = FlowDir * FlowSpecularStrength;
    float3 flowRaw          = float3(flowScaled.x, 0.0, flowScaled.y);
    float3 flowPerturbation = flowRaw - NormalWS * dot(NormalWS, flowRaw);
    float3 specNormal       = normalize(NormalWS + flowPerturbation);

    float3 H    = normalize(mainDir + ViewWS);
    float NdotH = saturate(dot(specNormal, H));
    float NdotV = saturate(dot(specNormal, ViewWS));
    float NdotL = saturate(dot(specNormal, mainDir));

    float spatialMask = _WS_SparkleMask(SparkleNoise, SparkleThreshold, SparkleSharpness);

    float fresnelEdge = pow(saturate(1.0 - NdotV), 2.0);
    float rawHalo     = _WS_BlinnPhongBright(NdotH, HaloGloss) * HaloIntensity * lerp(0.25, 1.0, fresnelEdge);
    float rawShimmer  = _WS_BlinnPhongBright(NdotH, BaseGloss) * BaseIntensity * fresnelEdge;
    
    float3 F0      = float3(SunF0, SunF0, SunF0);
    float3 fresnel = _WS_FresnelSchlick(NdotV, F0);
    float  frBst   = pow(saturate(1.0 - NdotV), max(FresnelBoost, 0.001));

    float rawSunGlint = _WS_BlinnPhongBright(NdotH, SunGloss) * SunIntensity * (fresnel.x + frBst) * NdotL;

    float sssSmooth = saturate(dot(mainDir, -ViewWS) * 0.5 + 0.5);
    float sssTerm   = pow(sssSmooth, 6.0) * SSSIntensity * (1.0 - cameraZenith * 0.85);
    float3 sssColor = SSSColor * sssTerm * mainColor;

    float3 ambientSpecular = WaterDeepColor * 0.035 * (1.0 - NdotV * 0.6) * (1.0 - cameraZenith * 0.5);

    float3 specTint = lerp(mainColor, WaterDeepColor, lerp(0.4, 0.7, cameraZenith));
    
    float3 halo    = specTint * saturate(rawHalo);
    float  shimmer = saturate(rawShimmer);
    float3 glint   = mainColor * saturate(rawSunGlint); 

    float heightMask     = smoothstep(max(0.0, CrestThreshold - 0.05), min(1.0, CrestThreshold + 0.05), CrestMask);
    float fresnelContour = pow(saturate(1.0 - NdotV), 4.0);
    float crestRim       = heightMask * fresnelContour;
    float3 crestColor    = lerp(float3(1.0, 1.0, 1.0), SSSColor, 0.2);
    float3 crestGlow     = crestColor * crestRim * CrestIntensity * (mainColor + 0.2);

    Specular = (halo + (shimmer * WaterDeepColor * 0.5 + shimmer * mainColor * 0.5) + glint) * shadowAtten + ambientSpecular + sssColor + crestGlow;

    float combinedSpecularIntensity = rawHalo + rawSunGlint;
    float specularGate = smoothstep(1.5, 4.0, combinedSpecularIntensity);

    float finalSparkleMask = spatialMask * specularGate;
    Sparkles = finalSparkleMask * SparkleIntensity * SparkleColor * shadowAtten;
}

void WaterAdditionalLights_float(
    float3 PositionWS, float3 NormalWS, float3 ViewWS,
    float SpecularPower,
    float Intensity,
    float ShadowStrength,
    float NormalSoftness,
    out float3 DiffuseColor,
    out float3 SpecularColor,
    out float  AdditionalShadow)
{
    DiffuseColor     = float3(0, 0, 0);
    SpecularColor    = float3(0, 0, 0);
    AdditionalShadow = 1.0;

#if !defined(SHADERGRAPH_PREVIEW)

    InputData inputData = (InputData)0;
    inputData.positionWS = PositionWS;
    float4 clipPos   = TransformWorldToHClip(PositionWS);
    float4 screenPos = ComputeScreenPos(clipPos);
    inputData.normalizedScreenSpaceUV = screenPos.xy / screenPos.w;

    float shadowAccum = 0.0;
    float weightAccum = 0.0;

    float3 softenedNormal = normalize(lerp(NormalWS, float3(0, 1, 0), NormalSoftness));

    #if defined(_FORWARD_PLUS)

        uint pixelLightCount = GetAdditionalLightsCount();

        LIGHT_LOOP_BEGIN(pixelLightCount)
            Light addLight = GetAdditionalLight(lightIndex, PositionWS, half4(1, 1, 1, 1));

            float3 lightColor = addLight.color * addLight.distanceAttenuation * Intensity;
            float  NdotL      = saturate(dot(softenedNormal, addLight.direction));
            DiffuseColor     += lightColor * NdotL;

            float3 H     = normalize(addLight.direction + ViewWS);
            float  NdotH = saturate(dot(softenedNormal, H));
            SpecularColor += lightColor * pow(NdotH, max(SpecularPower, 1.0));

            float weight    = addLight.distanceAttenuation;
            float rawShadow = lerp(1.0, addLight.shadowAttenuation, ShadowStrength);
            shadowAccum    += rawShadow * weight;
            weightAccum    += weight;
        LIGHT_LOOP_END

    #elif defined(_ADDITIONAL_LIGHTS)

        uint pixelLightCount = GetAdditionalLightsCount();

        LIGHT_LOOP_BEGIN(pixelLightCount)
            // SOLUCIÓN: Usamos la sobrecarga de 2 argumentos para Forward Clásico
            Light addLight = GetAdditionalLight(lightIndex, PositionWS);

            float3 lightColor = addLight.color * addLight.distanceAttenuation * Intensity;
            float  NdotL      = saturate(dot(softenedNormal, addLight.direction));
            DiffuseColor     += lightColor * NdotL;

            float3 H     = normalize(addLight.direction + ViewWS);
            float  NdotH = saturate(dot(softenedNormal, H));
            SpecularColor += lightColor * pow(NdotH, max(SpecularPower, 1.0));

            float weight    = addLight.distanceAttenuation;
            float rawShadow = lerp(1.0, addLight.shadowAttenuation, ShadowStrength);
            shadowAccum    += rawShadow * weight;
            weightAccum    += weight;
        LIGHT_LOOP_END

    #endif // renderer branch

    if (weightAccum > 0.0001)
        AdditionalShadow = shadowAccum / weightAccum;

#endif // !SHADERGRAPH_PREVIEW
}

void WaterEnvironment_float(
    float3 NormalWS,
    float3 ViewWS,
    float3 PositionWS,
    float2 ScreenUV,
    float PerceptualRoughness,
    float Intensity,
    float2 FlowDir,
    float FlowReflectionStrength,
    float ReflectionNormalSoftness,
    float ReflectionMode,
    UnityTextureCube SkyboxCubemap,
    UnitySamplerState sampler_SkyboxCubemap,
    float ReflectionJitter,
    float ReflectionJitterScale,
    out float3 OutReflection)
{
#if defined(SHADERGRAPH_PREVIEW)
    OutReflection = float3(0.5, 0.6, 0.7);
#else
    float3 p = PositionWS * ReflectionJitterScale;
    float3 wave_sum = (
        sin(fmod(p, 62.831853)) + 
        sin(fmod(p.yzx * 1.618034, 62.831853)) + 
        sin(fmod(p.zxy * 2.414214, 62.831853))
    );
    float3 jitter = wave_sum * (ReflectionJitter * 0.33);

    float3 softenedNormal = normalize(lerp(NormalWS, float3(0, 1, 0), ReflectionNormalSoftness) + jitter);
    float3 R_base = reflect(-ViewWS, softenedNormal);
    float3 flowOffset = float3(FlowDir.x, 0.0, FlowDir.y) * FlowReflectionStrength;
    float3 R = normalize(R_base + flowOffset);
    
    half3 refl = half3(0, 0, 0);
    
    if (ReflectionMode > 0.5)
    {
        half mip = PerceptualRoughnessToMipmapLevel((half) PerceptualRoughness);
        half4 sampledColor = SAMPLE_TEXTURECUBE_LOD(SkyboxCubemap.tex, sampler_SkyboxCubemap.samplerstate, R, mip);
        refl = sampledColor.rgb;
    }
    else
    {
        // GlossyEnvironmentReflection: la firma varía según versión de URP.
        // Unity 6 / URP 17+ usa 5 argumentos (añade ScreenUV para probes).
        // Versiones anteriores usan 3 argumentos (sin posWS ni ScreenUV).
        // Usamos UNITY_VERSION para seleccionar la firma correcta y evitar
        // errores de compilación al cambiar de versión de Unity.
        #if UNITY_VERSION >= 600000
            refl = GlossyEnvironmentReflection(R, PositionWS, (half)PerceptualRoughness, 1.0h, ScreenUV);
        #else
            refl = GlossyEnvironmentReflection(R, PositionWS, (half)PerceptualRoughness, 1.0h);
        #endif
    }
    
    OutReflection = (float3) refl * Intensity;
#endif
}

#ifndef SHADERGRAPH_PREVIEW
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#endif

void WaterCausticsUV_float(
    float2 ScreenUV,
    float3 WaterPositionWS,
    float CausticsScale,
    float2 CausticsSpeed,
    float DepthFadeDistance,
    float DepthCausticsPower,
    out float2 CausticsUV1,
    out float2 CausticsUV2,
    out float DepthMask)
{
    CausticsUV1 = float2(0,0);
    CausticsUV2 = float2(0,0);
    DepthMask = 0;

#if !defined(SHADERGRAPH_PREVIEW)
    float2 uv = UnityStereoTransformScreenSpaceTex(ScreenUV);
    float rawDepth = SampleSceneDepth(uv);
    float3 groundPositionWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);

    CausticsUV1 = (groundPositionWS.xz * CausticsScale) + (_Time.y * CausticsSpeed);

    float2 speed2 = float2(-CausticsSpeed.y, CausticsSpeed.x) * 0.8;
    CausticsUV2 = (groundPositionWS.xz * (CausticsScale * 1.15)) + (_Time.y * speed2);

    float depthDiff = WaterPositionWS.y - groundPositionWS.y;
    float baseDepthMask = saturate(depthDiff / max(DepthFadeDistance, 0.001));
    float shallowMask = saturate(1.0 - (depthDiff / max(DepthCausticsPower, 0.001)));

    float3 dPdx = ddx(groundPositionWS);
    float3 dPdy = ddy(groundPositionWS);
    float3 groundNormal = normalize(cross(dPdy, dPdx));
    
    float flatMask = smoothstep(0.4, 0.95, abs(groundNormal.y));

    DepthMask = saturate(baseDepthMask * flatMask * shallowMask);
#endif
}

void WaterNormalShading_float(
    float3 NormalWS,
    float3 ViewWS,
    float3 UpColor,
    float3 SideColor,
    float3 DownColor,
    float  Contrast,
    float  FresnelPower,
    out float3 FakeLight)
{
    float upFactor   = saturate(NormalWS.y);
    float downFactor = saturate(-NormalWS.y);
    float sideFactor = 1.0 - abs(NormalWS.y);

    float3 baseShading = UpColor   * upFactor
                       + SideColor * sideFactor
                       + DownColor * downFactor;

    baseShading = pow(max(baseShading, 0.001), max(Contrast, 0.001));

    float NdotV      = saturate(dot(NormalWS, ViewWS));
    float fresnelRim = pow(saturate(1.0 - NdotV), FresnelPower);
    baseShading     += SideColor * fresnelRim * 0.3;

    FakeLight = baseShading;
}



#endif
