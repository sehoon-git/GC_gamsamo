Shader "Hidden/Gamsamo/Room Layer Composite"
{
    Properties
    {
        _LayerTex ("Room Layer", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            // Straight-alpha source artwork over an opaque room base keeps the final alpha at one.
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            sampler2D _LayerTex;
            float4 _LayerRect;
            float4x4 _CanvasProjection;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata input)
            {
                v2f output;
                float2 pixelPosition = _LayerRect.xy + input.vertex.xy * _LayerRect.zw;
                output.vertex = mul(_CanvasProjection, float4(pixelPosition, 0, 1));
                output.uv = input.uv;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                return tex2D(_LayerTex, input.uv);
            }
            ENDCG
        }
    }
    Fallback Off
}
