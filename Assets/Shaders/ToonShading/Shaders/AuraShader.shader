Shader "FX/Aura Outline" {
    Properties{
        
        _Color("Aura Inner Color", Color) = (0,0,1,1)
        _ColorR("Rim Color", Color) = (0,1,1,1)
        _Outline("Outline width", Range(.002, 0.8)) = .3
        _OutlineZ("Outline Z", Range(-.06, 0)) = -.05
        _NoiseTex("Noise Texture", 2D) = "white" { }
        _Scale("Noise Scale", Range(0.0, 0.2)) = 0.01
        _SpeedX("Speed X", Range(-10, 10)) = 0
        _SpeedY("Speed Y", Range(-10, 10)) = 3.0
        _Opacity("Noise Opacity", Range(0.01, 10.0)) = 10
        _Brightness("Brightness", Range(0.5, 3)) = 2
        _Edge("Rim Edge", Range(0.0, 1)) = 0.1
        _RimPower("Rim Power", Range(0.01, 10.0)) = 1
        
    }
    
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
    
    
    struct appdata {
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        
    };
    
    struct v2f {
        float4 pos : SV_POSITION;
        float3 viewDir : TEXCOORD1;
        float3 normalDir : TEXCOORD2;
    };
    
    
    float _Outline;
    float _OutlineZ;
    float _RimPower;
    
    v2f vert(appdata v) {
        v2f o;
        // clipspace
        o.pos = TransformObjectToHClip(v.vertex.xyz);
        
        // scale of object
        float3 scale = float3(
        length(unity_ObjectToWorld._m00_m10_m20),
        length(unity_ObjectToWorld._m01_m11_m21),
        length(unity_ObjectToWorld._m02_m12_m22)
        );
        // normals for fresnel
        o.normalDir = normalize(mul(float4(v.normal, 1), unity_WorldToObject).xyz); // normal direction
        // view direction for fresnel
        float3 worldPos =  mul(unity_ObjectToWorld, v.vertex).xyz;
        o.viewDir = normalize(GetWorldSpaceViewDir(worldPos)); // view direction
        
        // rotate normals to eye space
        float3 norm = normalize(mul((float3x3)UNITY_MATRIX_IT_MV, v.normal)) * scale;
        float2 offset = (mul((float2x2)UNITY_MATRIX_P, norm.xy));
        // add the offset
        o.pos.xy += offset * _Outline; 
        // flatten
        o.pos.z *= 0.01;
        // push away from camera
        o.pos.z -= (_OutlineZ);
        
        return o;
    }
    ENDHLSL
    
    SubShader{
        
        Pass{        
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha // Transparency Blending
            ZWrite Off
            Cull Back
            
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            sampler2D _NoiseTex;
            float _Scale, _Opacity, _Edge;
            float4 _Color, _ColorR;
            float _Brightness, _SpeedX, _SpeedY;
            
            float4 frag(v2f i) : SV_Target
            {
                float2 uv = float2(i.pos.x* _Scale - (_Time.x * _SpeedX), i.pos.y * _Scale - (_Time.x * _SpeedY)); // float2 based on speed, position and, scale
                float text = tex2D(_NoiseTex, uv).r;
                float rim = pow(saturate(dot(i.viewDir, i.normalDir)), _RimPower).r; // calculate inverted rim based on view and normal
                rim -= text; // subtract noise texture
                float4 texturedAura = saturate(rim * _Opacity); // make a harder edge
                float4 extraRim = (saturate((_Edge + rim) * _Opacity) - texturedAura);// extra edge, subtracting the textured rim
                float4 result = ((_Color * texturedAura) + (_ColorR * extraRim));// combine both with colors
                return saturate(result) * _Brightness;
            }
            ENDHLSL
        }
    }
    
}