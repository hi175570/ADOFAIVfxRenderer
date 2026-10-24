Shader "ADOFAI/VFX Blend"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _BlendMode ("Blend Mode", Float) = 0
        _Tile ("Tile", Vector) = (1,1,0,0)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
        [HideInInspector] _Stencil ("Stencil ID", Float) = 0
        [HideInInspector] _StencilOp ("Stencil Operation", Float) = 0
        [HideInInspector] _StencilWriteMask ("Stencil Write Mask", Float) = 255
        [HideInInspector] _StencilReadMask ("Stencil Read Mask", Float) = 255
        [HideInInspector] _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }
        Cull Off
        Lighting Off
        ZWrite Off

        GrabPass { }

        Pass
        {
            Stencil
            {
                Ref [_Stencil]
                Comp [_StencilComp]
                Pass [_StencilOp]
                ReadMask [_StencilReadMask]
                WriteMask [_StencilWriteMask]
            }
            ColorMask [_ColorMask]
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 grabPos : TEXCOORD1;
            };

            sampler2D _MainTex;
            sampler2D _GrabTexture;
            fixed4 _Color;
            fixed4 _RendererColor;
            float _BlendMode;
            float4 _Tile;

            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = frac(input.uv * max(_Tile.xy, float2(0.000001, 0.000001)));
                output.color = input.color * _Color;
                output.grabPos = ComputeGrabScreenPos(output.vertex);
                return output;
            }

            fixed3 Overlay(fixed3 background, fixed3 source)
            {
                fixed3 low = 2.0 * background * source;
                fixed3 high = 1.0 - 2.0 * (1.0 - background) * (1.0 - source);
                return lerp(low, high, step(0.5, background));
            }

            fixed3 SoftLight(fixed3 background, fixed3 source)
            {
                fixed3 low = background - (1.0 - 2.0 * source) * background * (1.0 - background);
                fixed3 d = lerp(((16.0 * background - 12.0) * background + 4.0) * background, sqrt(background), step(0.25, background));
                fixed3 high = background + (2.0 * source - 1.0) * (d - background);
                return lerp(low, high, step(0.5, source));
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 source = tex2D(_MainTex, input.uv) * input.color;
                fixed4 background = tex2Dproj(_GrabTexture, UNITY_PROJ_COORD(input.grabPos));
                fixed3 blended;
                if (_BlendMode < 0.5) blended = abs(background.rgb - source.rgb);
                else if (_BlendMode < 1.5) blended = Overlay(background.rgb, source.rgb);
                else if (_BlendMode < 2.5) blended = SoftLight(background.rgb, source.rgb);
                else blended = min(1.0, background.rgb + source.rgb);
                return fixed4(lerp(background.rgb, blended, source.a), background.a + source.a * (1.0 - background.a));
            }
            ENDCG
        }
    }
}
