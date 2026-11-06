Shader "Hidden/Gamsamo/Room Layer Composite"
{
    Properties
    {
        _LayerTex ("Room Layer", 2D) = "white" {}
        _PreserveWindowView ("Preserve Waiting Room", Float) = 0
        _WindowViewTex ("Waiting Room", 2D) = "white" {}
        _WindowClearTex ("Window Clear Reference", 2D) = "white" {}
        _WindowCloudyTex ("Window Cloudy Reference", 2D) = "white" {}
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
            sampler2D _WindowViewTex;
            sampler2D _WindowClearTex;
            sampler2D _WindowCloudyTex;
            float _PreserveWindowView;
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
                fixed4 layer = tex2D(_LayerTex, input.uv);
                if (_PreserveWindowView > 0.5)
                {
                    // Shipped weather pairs differ only inside the glass. Matching frames,
                    // mullions and blind fabric stay opaque, including fully closed blinds.
                    float3 difference = abs(tex2D(_WindowClearTex, input.uv).rgb
                        - tex2D(_WindowCloudyTex, input.uv).rgb);
                    float glass = smoothstep(0.003, 0.03, max(difference.r, max(difference.g, difference.b)));
                    layer.rgb = lerp(layer.rgb, tex2D(_WindowViewTex, input.uv).rgb, glass);
                }
                return layer;
            }
            ENDCG
        }
    }
    Fallback Off
}
