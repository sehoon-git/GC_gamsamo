Shader "Gamsamo/UI/Consultation Background Blur"
{
    Properties
    {
        [PerRendererData] _MainTex ("Original Background", 2D) = "white" {}
        _BlurredTex ("Blurred Background", 2D) = "white" {}
        _Amount ("Blur Amount", Range(0, 1)) = 0
        _BlurRadius ("Filter Radius", Float) = 2
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "False"
        }

        // Pass 0: uGUI renders the sharp/blurred blend without touching the foreground UI.
        Pass
        {
            Name "DISPLAY"
            Stencil
            {
                Ref [_Stencil]
                Comp [_StencilComp]
                Pass [_StencilOp]
                ReadMask [_StencilReadMask]
                WriteMask [_StencilWriteMask]
            }
            Cull Off
            Lighting Off
            ZWrite Off
            ZTest [unity_GUIZTestMode]
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask [_ColorMask]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 localPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            sampler2D _BlurredTex;
            fixed4 _Color;
            float4 _ClipRect;
            float _Amount;

            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.localPosition = input.vertex;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.color = input.color * _Color;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 original = tex2D(_MainTex, input.uv);
                fixed4 blurred = tex2D(_BlurredTex, input.uv);
                fixed4 color = lerp(original, blurred, saturate(_Amount)) * input.color;
                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(input.localPosition.xy, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif
                return color;
            }
            ENDCG
        }

        // The five bilinear reads below implement a normalized nine-tap Gaussian.
        Pass
        {
            Name "HORIZONTAL"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _BlurRadius;

            fixed4 frag(v2f_img input) : SV_Target
            {
                float2 step = float2(_MainTex_TexelSize.x * _BlurRadius, 0);
                fixed4 color = tex2D(_MainTex, input.uv) * 0.2270270270;
                color += tex2D(_MainTex, input.uv + step * 1.3846153846) * 0.3162162162;
                color += tex2D(_MainTex, input.uv - step * 1.3846153846) * 0.3162162162;
                color += tex2D(_MainTex, input.uv + step * 3.2307692308) * 0.0702702703;
                color += tex2D(_MainTex, input.uv - step * 3.2307692308) * 0.0702702703;
                return color;
            }
            ENDCG
        }

        Pass
        {
            Name "VERTICAL"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _BlurRadius;

            fixed4 frag(v2f_img input) : SV_Target
            {
                float2 step = float2(0, _MainTex_TexelSize.y * _BlurRadius);
                fixed4 color = tex2D(_MainTex, input.uv) * 0.2270270270;
                color += tex2D(_MainTex, input.uv + step * 1.3846153846) * 0.3162162162;
                color += tex2D(_MainTex, input.uv - step * 1.3846153846) * 0.3162162162;
                color += tex2D(_MainTex, input.uv + step * 3.2307692308) * 0.0702702703;
                color += tex2D(_MainTex, input.uv - step * 3.2307692308) * 0.0702702703;
                return color;
            }
            ENDCG
        }
    }
    Fallback Off
}
