// Procedural expanding ring drawn on a quad (UV centred at 0.5).
Shader "OrbitalStrike/Shockwave"
{
    Properties
    {
        [HDR] _Color ("Color", Color) = (1, 0.6, 0.25, 1)
        _Radius ("Radius (0-1)", Range(0, 1)) = 0.3
        _Width ("Width", Range(0.001, 0.5)) = 0.06
        _Inner ("Inner Glow", Range(0, 1)) = 0.25
        _Intensity ("Intensity", Float) = 3
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend One One
        ZWrite Off
        Cull Off

        Pass
        {
            Name "Ring"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Radius;
                half _Width;
                half _Inner;
                half _Intensity;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert (Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                half r = length(i.uv - 0.5) * 2.0;
                half ring = smoothstep(_Radius - _Width, _Radius, r) * (1.0 - smoothstep(_Radius, _Radius + _Width * 0.35, r));
                half inner = saturate(1.0 - r / max(_Radius, 0.001)) * _Inner * step(r, _Radius);
                half fade = 1.0 - smoothstep(0.85, 1.0, r);
                return half4(_Color.rgb * (ring + inner) * fade * _Intensity, 1);
            }
            ENDHLSL
        }
    }
}
