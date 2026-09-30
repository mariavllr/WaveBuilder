Shader "Hidden/TiltShift"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float4 _TS_TexelSize; // xy = 1/tamaño de la textura de blur
        float4 _TS_Params;    // x = focusCenter, y = focusWidth, z = falloff, w = blurRadius
        float4 _TS_Params2;   // x = intensity, y = debugMask
        TEXTURE2D_X(_TS_BlurTex);

        // Gaussiano de 9 taps aprovechando el filtrado bilineal (5 lecturas)
        half4 GaussianBlur(float2 uv, float2 direction)
        {
            float2 stepUV = direction * _TS_TexelSize.xy * _TS_Params.w;
            float2 off1 = stepUV * 1.3846153846;
            float2 off2 = stepUV * 3.2307692308;

            half4 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv) * 0.2270270270;
            color += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + off1) * 0.3162162162;
            color += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - off1) * 0.3162162162;
            color += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + off2) * 0.0702702703;
            color += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - off2) * 0.0702702703;
            return color;
        }

        float FocusMask(float2 uv)
        {
            float dist = abs(uv.y - _TS_Params.x);
            return smoothstep(_TS_Params.y, _TS_Params.y + _TS_Params.z, dist);
        }

        half4 FragBlurH(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return GaussianBlur(input.texcoord, float2(1, 0));
        }

        half4 FragBlurV(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return GaussianBlur(input.texcoord, float2(0, 1));
        }

        half4 FragComposite(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;

            half4 sharp = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);
            half4 blurred = SAMPLE_TEXTURE2D_X(_TS_BlurTex, sampler_LinearClamp, uv);

            float mask = FocusMask(uv) * _TS_Params2.x;

            if (_TS_Params2.y > 0.5)
                return half4(mask.xxx, 1);

            return half4(lerp(sharp.rgb, blurred.rgb, mask), sharp.a);
        }
        ENDHLSL

        Pass
        {
            Name "TiltShift Blur Horizontal"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlurH
            ENDHLSL
        }

        Pass
        {
            Name "TiltShift Blur Vertical"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlurV
            ENDHLSL
        }

        Pass
        {
            Name "TiltShift Composite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            ENDHLSL
        }
    }
}
