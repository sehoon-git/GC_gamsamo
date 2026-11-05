Shader "UI/Gamsamo/PatientPortrait"
{
    Properties
    {
        [PerRendererData] _MainTex ("Portrait", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _BlackLow ("Black key low", Float) = 0.008
        _BlackHigh ("Black key high", Float) = 0.035
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
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="False" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            struct appdata { float4 vertex:POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 vertex:SV_POSITION; fixed4 color:COLOR; float2 uv:TEXCOORD0; float4 localPosition:TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            sampler2D _MainTex;
            fixed4 _Color;
            float4 _ClipRect;
            float _BlackLow, _BlackHigh;
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.localPosition=v.vertex;
                o.vertex=UnityObjectToClipPos(v.vertex);
                o.uv=v.uv;
                o.color=v.color*_Color;
                return o;
            }
            fixed4 frag(v2f i):SV_Target
            {
                fixed4 source=tex2D(_MainTex,i.uv);
                // The supplied portraits contain an opaque near-black backdrop. Keep originals intact.
                // Key in sRGB even when the project samples textures in linear color space.
                float3 keyColor=source.rgb;
                #ifndef UNITY_COLORSPACE_GAMMA
                    keyColor=LinearToGammaSpace(keyColor);
                #endif
                float key=smoothstep(_BlackLow,_BlackHigh,max(keyColor.r,max(keyColor.g,keyColor.b)));
                fixed4 color=source*i.color;
                color.a*=key;
                #ifdef UNITY_UI_CLIP_RECT
                    color.a*=UnityGet2DClipping(i.localPosition.xy,_ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                    clip(color.a-0.001);
                #endif
                return color;
            }
            ENDCG
        }
    }
}
