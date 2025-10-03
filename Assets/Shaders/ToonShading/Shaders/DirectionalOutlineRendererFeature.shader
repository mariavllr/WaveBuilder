Shader "Toon/Directional Rim Outline Renderer" {
	Properties{		
		_Outline("Outline width", Range(.002, 0.5)) = .005
		_OutlineZ("Outline Z", Range(-.016, 0)) = -.001// outline z offset
		_Brightness("Outline Brightness", Range(0.5, 10)) = .005// noise offset	
		_Lerp("Lerp between normals and vertex based", Range(0, 1)) = 0// outline z offset		
	}
	
	HLSLINCLUDE
	#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
	#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
	struct appdata {
		float4 vertex : POSITION;
		float4 texcoord : TEXCOORD0;// texture coordinates
		float4 normal : NORMAL;
	};
	
	struct v2f {
		float4 pos : SV_POSITION;
		float3 lightDir : TEXCOORD1;
		float4 screenPos : TEXCOORD2;
	};
	
	float _Lerp; // noise offset
	float _Outline;
	float _OutlineZ;
	
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

		// Get the light direction
		Light light = GetMainLight();
		o.lightDir = normalize(light.direction);//
		
		// rotate normals to eye space
		float3 norm = normalize(mul((float3x3)UNITY_MATRIX_IT_MV, v.normal)) * scale;
		// attempt to do this to vertex for hard normals
		float3 vert = normalize(mul((float4x4)UNITY_MATRIX_IT_MV, v.vertex)) * scale;
		// create an offset and add in the light direction
		float2 offset = mul((float2x2)UNITY_MATRIX_P, float2(lerp(norm.x, vert.x, _Lerp), lerp(norm.y, vert.y, _Lerp)))+ (float2(o.lightDir.x, -o.lightDir.y) * 2);
		// screenpos for grabpass
		o.screenPos = ComputeScreenPos(o.pos);	
		// move vertices with offset			
		o.pos.xy += offset * _Outline;	
		// push away from camera
		o.pos.z += _OutlineZ;
		return o;
	}
	ENDHLSL
	
	SubShader{
		


		Pass{
			Name "OUTLINE"
			Tags { "LightMode"="UniversalForward" }
			Cull Off// we dont want to cull
			
			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			sampler2D _CameraOpaqueTexture;
			float _Brightness;
			float4 frag(v2f i) : SV_Target
			{
				// grab the camera view to colorize the outline
				half4 col = tex2D(_CameraOpaqueTexture , i.screenPos.xy / i.screenPos.w) * _Brightness;
				return saturate(col);
			}
			ENDHLSL
		}

		
	}
	
}