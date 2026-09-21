void GerstnerWave_float(
    float4 DirFreqPhase,
    float4 AmpSteep,
    float3 posWS,
    float2 meshUV,
    int    useUV,
    float  horizontalStrength,
    float  riverLength,
    float  riverWidth,
    inout float3 displacement,
    inout float3 tangent,
    inout float3 binormal,
    inout float  jacobian)
{
    float2 d     = DirFreqPhase.xy;
    float  freq  = DirFreqPhase.z;
    float  phase = DirFreqPhase.w;
    float  amp   = AmpSteep.x;
    float  steep = AmpSteep.y;

    float dLength = length(d);
    float2 finalD = (dLength > 1e-6) ? d / dLength : float2(1, 0);

    if (useUV == 1)
    {
        float2 uvPhysical = float2(meshUV.x * riverLength, meshUV.y * riverWidth);
        
        float xz = dot(finalD, uvPhysical);
        float theta = freq * xz + phase * _Time.y;
        
        float s = sin(theta);
        float c = cos(theta);
        
        float qi = steep;
        float hs = horizontalStrength;

        displacement.x += qi * amp * finalD.x * c * hs;
        displacement.z += qi * amp * finalD.y * c * hs;
        displacement.y += amp * s;

        float wa = freq * amp;
        tangent.x  += -qi * finalD.x * finalD.x * wa * s * hs;
        tangent.y  +=        finalD.x       * wa * c;
        tangent.z  += -qi * finalD.x * finalD.y * wa * s * hs;

        binormal.x += -qi * finalD.x * finalD.y * wa * s * hs;
        binormal.y +=        finalD.y       * wa * c;
        binormal.z += -qi * finalD.y * finalD.y * wa * s * hs;
        
        jacobian += -(qi * freq * amp * 4.0) * c;
    }
    else
    {
        float xz = dot(finalD, posWS.xz);
        float spatialOffset = sin(posWS.x * 0.1) * cos(posWS.z * 0.15) * 1.5; 
        
        float theta = freq * xz + phase * _Time.y + spatialOffset;

        float s = sin(theta);
        float c = cos(theta);
        
        float qi = steep;
        float hs = horizontalStrength;

        displacement.x += qi * amp * finalD.x * c * hs;
        displacement.z += qi * amp * finalD.y * c * hs;
        displacement.y += amp * s;

        float wa = freq * amp;
        tangent.x  += -qi * finalD.x * finalD.x * wa * s * hs;
        tangent.y  +=        finalD.x       * wa * c;
        tangent.z  += -qi * finalD.x * finalD.y * wa * s * hs;

        binormal.x += -qi * finalD.x * finalD.y * wa * s * hs;
        binormal.y +=        finalD.y       * wa * c;
        binormal.z += -qi * finalD.y * finalD.y * wa * s * hs;

        jacobian += -(qi * freq * amp * 4.0) * c;
    }
}

void WaterDisplace_float(
    float3 PositionOS,
    float2 MeshUV,
    float  UseUV,
    float4 Wave0_DirFreqPhase, float4 Wave0_AmpSteep,
    float4 Wave1_DirFreqPhase, float4 Wave1_AmpSteep,
    float4 Wave2_DirFreqPhase, float4 Wave2_AmpSteep,
    float4 Wave3_DirFreqPhase, float4 Wave3_AmpSteep,
    float  HorizontalStrength,
    float  RiverLength,
    float  RiverWidth,
    float  FoamThreshold,
    float  OrganicFoamWidth,
    float  FoamTipThreshold,
    float  FoamNoise,
    out float3 PositionOS_Out,
    out float3 NormalOS_Out,
    out float  FoamFactor,
    out float  Jacobian)
{
    float3 posWS    = mul(unity_ObjectToWorld, float4(PositionOS, 1.0)).xyz;




    posWS = round(posWS * 1000.0) * 0.001;

    float3 disp     = float3(0, 0, 0);
    float3 tangent  = float3(1, 0, 0);
    float3 binormal = float3(0, 0, 1);
    float  jac      = 0.0;

    GerstnerWave_float(Wave0_DirFreqPhase, Wave0_AmpSteep, posWS, MeshUV, (UseUV > 0.5) ? 1 : 0, HorizontalStrength, RiverLength, RiverWidth, disp, tangent, binormal, jac);
    GerstnerWave_float(Wave1_DirFreqPhase, Wave1_AmpSteep, posWS, MeshUV, (UseUV > 0.5) ? 1 : 0, HorizontalStrength, RiverLength, RiverWidth, disp, tangent, binormal, jac);
    GerstnerWave_float(Wave2_DirFreqPhase, Wave2_AmpSteep, posWS, MeshUV, (UseUV > 0.5) ? 1 : 0, HorizontalStrength, RiverLength, RiverWidth, disp, tangent, binormal, jac);
    GerstnerWave_float(Wave3_DirFreqPhase, Wave3_AmpSteep, posWS, MeshUV, (UseUV > 0.5) ? 1 : 0, HorizontalStrength, RiverLength, RiverWidth, disp, tangent, binormal, jac);

    float dropoffFactor = 1.0;
    float exaggeratedDropoff = 1.0;
    
    disp *= exaggeratedDropoff;
    tangent = lerp(float3(1, 0, 0), tangent, exaggeratedDropoff);
    binormal = lerp(float3(0, 0, 1), binormal, exaggeratedDropoff);

    float totalAmp  = Wave0_AmpSteep.x + Wave1_AmpSteep.x + Wave2_AmpSteep.x + Wave3_AmpSteep.x;
    float heightRatio = (totalAmp > 0.0001) ? saturate(disp.y / totalAmp) : 0.0;
    heightRatio = max(heightRatio, 1.0 - dropoffFactor);
    
    Jacobian = saturate(-jac); 

    float organicHeight = saturate(heightRatio + (FoamNoise - 0.5) * max(OrganicFoamWidth, 0.1) * 2.0);
    float foamEdge = FoamThreshold + max(OrganicFoamWidth, 0.01);
    FoamFactor = smoothstep(FoamThreshold, foamEdge, organicHeight);

    float3 normalWS  = normalize(cross(binormal, tangent));
    float3 dispOS    = mul((float3x3)unity_WorldToObject, disp);
    PositionOS_Out   = PositionOS + dispOS;
    NormalOS_Out     = normalize(mul((float3x3)unity_WorldToObject, normalWS));
}

void WaterFoam_float(
    float3 PositionWS,
    float2 MeshUV,
    float  UseUV,
    float4 Wave0_DirFreqPhase, float4 Wave0_AmpSteep,
    float4 Wave1_DirFreqPhase, float4 Wave1_AmpSteep,
    float4 Wave2_DirFreqPhase, float4 Wave2_AmpSteep,
    float4 Wave3_DirFreqPhase, float4 Wave3_AmpSteep,
    float  HorizontalStrength,
    float  RiverLength,
    float  RiverWidth,
    float  FoamThreshold,
    float  OrganicFoamWidth,
    float  FoamTipThreshold,
    out float FoamFactor)
{
    float3 disp     = float3(0, 0, 0);
    float3 tangent  = float3(1, 0, 0);
    float3 binormal = float3(0, 0, 1);
    float  jac      = 0.0;
    int    uv       = (UseUV > 0.5) ? 1 : 0;

    GerstnerWave_float(Wave0_DirFreqPhase, Wave0_AmpSteep, PositionWS, MeshUV, uv, HorizontalStrength, RiverLength, RiverWidth, disp, tangent, binormal, jac);
    GerstnerWave_float(Wave1_DirFreqPhase, Wave1_AmpSteep, PositionWS, MeshUV, uv, HorizontalStrength, RiverLength, RiverWidth, disp, tangent, binormal, jac);
    GerstnerWave_float(Wave2_DirFreqPhase, Wave2_AmpSteep, PositionWS, MeshUV, uv, HorizontalStrength, RiverLength, RiverWidth, disp, tangent, binormal, jac);
    GerstnerWave_float(Wave3_DirFreqPhase, Wave3_AmpSteep, PositionWS, MeshUV, uv, HorizontalStrength, RiverLength, RiverWidth, disp, tangent, binormal, jac);

    float dropoffFactor = 1.0;
    float exaggeratedDropoff = 1.0;
    disp *= exaggeratedDropoff;

    float totalAmp  = Wave0_AmpSteep.x + Wave1_AmpSteep.x + Wave2_AmpSteep.x + Wave3_AmpSteep.x;
    float heightRatio = (totalAmp > 0.0001) ? saturate(disp.y / totalAmp) : 0.0;
    heightRatio = max(heightRatio, 1.0 - dropoffFactor);

    float foamEdge = FoamThreshold + max(OrganicFoamWidth, 0.01);
    FoamFactor = smoothstep(FoamThreshold, foamEdge, heightRatio);
}
