Shader "Hidden/ADOFAI/OfflineEffects"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _BloomTex ("Bloom", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"
        sampler2D _MainTex;
        sampler2D _BloomTex;
        float4 _MainTex_TexelSize;
        float4 _Resolution;
        float4 _ScreenTile;
        float4 _ScreenScroll;
        float4 _Yuv;
        float _TimeValue;
        float _Grayscale;
        float _Sepia;
        float _Invert;
        float _Contrast;
        float _Aberration;
        float _Waves;
        float _Fisheye;
        float _LightWater;
        float _Glitch;
        float _Pixelate;
        float _Led;
        float _Posterize;
        float _Grain;
        float _Neon;
        float _OilPaint;
        float _Drawing;
        float _Vhs;
        float _Rain;
        float _Sharpen;
        float _MotionBlur;
        float _Earthquake;
        float _RadialBlur;
        float _BlurHole;
        float _WideHorizontal;
        float _WideVertical;
        float _Dark;
        float _Hue;
        float _Ascii;
        float _Matrix;
        float _Tiles;
        float _Tunnel;
        float _HallOfMirrors;
        float _BlurSize;
        float _BloomIntensity;
        float _BloomThreshold;

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
            output.vertex = UnityObjectToClipPos(input.vertex);
            output.uv = input.uv;
            return output;
        }

        float hash21(float2 value)
        {
            value = frac(value * float2(123.34, 456.21));
            value += dot(value, value + 45.32);
            return frac(value.x * value.y);
        }

        float3 rgbToYuv(float3 c)
        {
            return float3(dot(c, float3(0.299, 0.587, 0.114)), dot(c, float3(-0.14713, -0.28886, 0.436)), dot(c, float3(0.615, -0.51499, -0.10001)));
        }

        float3 yuvToRgb(float3 c)
        {
            return float3(c.x + 1.13983 * c.z, c.x - 0.39465 * c.y - 0.58060 * c.z, c.x + 2.03211 * c.y);
        }

        float3 hueRotate(float3 color, float angle)
        {
            float s = sin(angle);
            float c = cos(angle);
            float3x3 hueMatrix = float3x3(
                0.299 + 0.701*c + 0.168*s, 0.587 - 0.587*c + 0.330*s, 0.114 - 0.114*c - 0.497*s,
                0.299 - 0.299*c - 0.328*s, 0.587 + 0.413*c + 0.035*s, 0.114 - 0.114*c + 0.292*s,
                0.299 - 0.300*c + 1.250*s, 0.587 - 0.588*c - 1.050*s, 0.114 + 0.886*c - 0.203*s);
            return mul(hueMatrix, color);
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag(v2f input) : SV_Target
            {
                float2 uv = input.uv;
                float2 centered = uv - 0.5;
                float aspect = _Resolution.x / max(1.0, _Resolution.y);
                centered.x *= aspect;

                if (abs(_ScreenTile.x) > 0.0001 && abs(_ScreenTile.y) > 0.0001)
                    uv = frac((uv + _ScreenScroll.xy * 0.01) * _ScreenTile.xy);

                if (_HallOfMirrors > 0.5)
                    uv = abs(frac(uv * 2.0) * 2.0 - 1.0);

                float radius = length(centered);
                if (abs(_Fisheye) > 0.0001)
                {
                    float power = 1.0 + _Fisheye * 0.75;
                    float adjusted = pow(max(radius, 0.00001), power);
                    centered *= adjusted / max(radius, 0.00001);
                    centered.x /= aspect;
                    uv = centered + 0.5;
                    centered = uv - 0.5;
                    centered.x *= aspect;
                }

                if (abs(_Tunnel) > 0.0001)
                {
                    float angle = atan2(centered.y, centered.x);
                    float tunnelRadius = pow(max(radius, 0.0001), 1.0 + _Tunnel * 0.2);
                    centered = float2(cos(angle), sin(angle)) * tunnelRadius;
                    centered.x /= aspect;
                    uv = centered + 0.5;
                }

                uv.x += sin((uv.y + _TimeValue * 0.35) * 18.0) * _Waves * 0.015;
                uv += float2(
                    sin(_TimeValue * 37.0 + uv.y * 13.0),
                    cos(_TimeValue * 31.0 + uv.x * 11.0)) * _LightWater * 0.004;
                uv += float2(sin(_TimeValue * 93.0), cos(_TimeValue * 71.0)) * _Earthquake * 0.002;

                if (abs(_Glitch) > 0.0001)
                {
                    float row = floor(uv.y * 48.0 + _TimeValue * 12.0);
                    float gate = step(0.78, hash21(float2(row, floor(_TimeValue * 15.0))));
                    uv.x += (hash21(float2(row, _TimeValue)) - 0.5) * gate * _Glitch * 0.08;
                }

                float block = max(_Pixelate * 24.0, max(_Led * 6.0, _Tiles * 0.2));
                if (block > 1.0)
                {
                    float2 grid = float2(block * aspect, block);
                    uv = (floor(uv * grid) + 0.5) / grid;
                }

                float aberration = _Aberration * 0.02;
                float2 radial = normalize((uv - 0.5) + 0.00001) * aberration;
                float4 baseColor = tex2D(_MainTex, uv);
                if (abs(aberration) > 0.00001)
                {
                    baseColor.r = tex2D(_MainTex, uv + radial).r;
                    baseColor.b = tex2D(_MainTex, uv - radial).b;
                }

                if (abs(_RadialBlur) > 0.0001)
                {
                    float2 direction = (0.5 - uv) * _RadialBlur * 0.015;
                    float4 sum = baseColor;
                    sum += tex2D(_MainTex, uv + direction);
                    sum += tex2D(_MainTex, uv + direction * 2.0);
                    sum += tex2D(_MainTex, uv + direction * 3.0);
                    baseColor = sum * 0.25;
                }

                if (abs(_MotionBlur) > 0.0001)
                {
                    float2 direction = float2(_MotionBlur * 0.003, 0.0);
                    baseColor = (baseColor + tex2D(_MainTex, uv - direction) + tex2D(_MainTex, uv + direction)) / 3.0;
                }

                float3 color = baseColor.rgb;
                if (abs(_Sharpen) > 0.0001)
                {
                    float3 neighbours = tex2D(_MainTex, uv + float2(_MainTex_TexelSize.x, 0)).rgb +
                                        tex2D(_MainTex, uv - float2(_MainTex_TexelSize.x, 0)).rgb +
                                        tex2D(_MainTex, uv + float2(0, _MainTex_TexelSize.y)).rgb +
                                        tex2D(_MainTex, uv - float2(0, _MainTex_TexelSize.y)).rgb;
                    color += (color * 4.0 - neighbours) * _Sharpen;
                }

                float luminance = dot(color, float3(0.299, 0.587, 0.114));
                color = lerp(color, luminance.xxx, saturate(_Grayscale));
                float3 sepia = float3(dot(color, float3(0.393, 0.769, 0.189)), dot(color, float3(0.349, 0.686, 0.168)), dot(color, float3(0.272, 0.534, 0.131)));
                color = lerp(color, sepia, saturate(_Sepia));
                color = lerp(color, 1.0 - color, saturate(_Invert));
                color = (color - 0.5) * (1.0 + _Contrast) + 0.5;
                color *= 1.0 - saturate(_Dark);

                float3 yuv = rgbToYuv(color);
                yuv += _Yuv.xyz;
                color = yuvToRgb(yuv);
                color = hueRotate(color, _Hue * 0.1);

                float levels = max(2.0, 20.0 * max(_Posterize, _OilPaint));
                if (max(_Posterize, _OilPaint) > 0.0001) color = floor(color * levels + 0.5) / levels;

                if (abs(_Neon) > 0.0001 || abs(_Drawing) > 0.0001)
                {
                    float3 dx = tex2D(_MainTex, uv + float2(_MainTex_TexelSize.x, 0)).rgb - tex2D(_MainTex, uv - float2(_MainTex_TexelSize.x, 0)).rgb;
                    float3 dy = tex2D(_MainTex, uv + float2(0, _MainTex_TexelSize.y)).rgb - tex2D(_MainTex, uv - float2(0, _MainTex_TexelSize.y)).rgb;
                    float edge = length(dx) + length(dy);
                    color = lerp(color, edge.xxx * 2.0, saturate(max(_Neon, _Drawing)));
                }

                float noise = hash21(floor(uv * _Resolution.xy) + floor(_TimeValue * 60.0));
                color += (noise - 0.5) * _Grain * 0.35;

                if (abs(_Vhs) > 0.0001)
                {
                    color *= 0.92 + 0.08 * sin(uv.y * _Resolution.y * 1.5);
                    color += (hash21(float2(floor(uv.y * 120.0), floor(_TimeValue * 25.0))) - 0.5) * _Vhs * 0.1;
                }

                if (abs(_Rain) > 0.0001)
                {
                    float streak = step(0.985, hash21(float2(floor(uv.x * 180.0), floor((uv.y + _TimeValue) * 90.0))));
                    color += streak * _Rain * 0.4;
                }

                if (abs(_Ascii) > 0.0001 || abs(_Matrix) > 0.0001)
                {
                    float2 cell = frac(uv * float2(120.0, 68.0));
                    float glyph = step(0.25, cell.x) * step(cell.x, 0.75) * step(0.15, cell.y) * step(cell.y, 0.85);
                    float3 glyphColor = lerp(float3(0.8, 0.8, 0.8), float3(0.1, 1.0, 0.25), saturate(_Matrix));
                    color = lerp(color, glyphColor * luminance * glyph, saturate(max(_Ascii, _Matrix)));
                }

                float wideH = saturate(_WideHorizontal);
                float wideV = saturate(_WideVertical);
                if (wideH > 0.0001 && abs(uv.y - 0.5) > 0.5 * (1.0 - wideH)) color = 0;
                if (wideV > 0.0001 && abs(uv.x - 0.5) > 0.5 * (1.0 - wideV)) color = 0;

                if (_BlurHole > 0.0001)
                {
                    float hole = smoothstep(0.1, 0.45, length((input.uv - 0.5) * float2(aspect, 1.0)));
                    color = lerp(color, color * (1.0 - hole * 0.5), saturate(_BlurHole));
                }

                if (_Led > 0.0001)
                {
                    float2 ledCell = frac(input.uv * float2(160.0, 90.0)) - 0.5;
                    color *= lerp(1.0, smoothstep(0.75, 0.1, length(ledCell)), saturate(_Led));
                }

                return float4(saturate(color), baseColor.a);
            }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBlurH
            fixed4 fragBlurH(v2f input) : SV_Target
            {
                float2 offset = float2(_MainTex_TexelSize.x * max(0.5, _BlurSize), 0);
                return tex2D(_MainTex, input.uv) * 0.4 +
                       (tex2D(_MainTex, input.uv + offset) + tex2D(_MainTex, input.uv - offset)) * 0.24 +
                       (tex2D(_MainTex, input.uv + offset * 2.0) + tex2D(_MainTex, input.uv - offset * 2.0)) * 0.06;
            }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBlurV
            fixed4 fragBlurV(v2f input) : SV_Target
            {
                float2 offset = float2(0, _MainTex_TexelSize.y * max(0.5, _BlurSize));
                return tex2D(_MainTex, input.uv) * 0.4 +
                       (tex2D(_MainTex, input.uv + offset) + tex2D(_MainTex, input.uv - offset)) * 0.24 +
                       (tex2D(_MainTex, input.uv + offset * 2.0) + tex2D(_MainTex, input.uv - offset * 2.0)) * 0.06;
            }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragThreshold
            fixed4 fragThreshold(v2f input) : SV_Target
            {
                float4 color = tex2D(_MainTex, input.uv);
                float luminance = max(color.r, max(color.g, color.b));
                return color * saturate((luminance - _BloomThreshold) * 4.0);
            }
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBloom
            fixed4 fragBloom(v2f input) : SV_Target
            {
                float4 source = tex2D(_MainTex, input.uv);
                float3 bloom = tex2D(_BloomTex, input.uv).rgb * _BloomIntensity;
                return float4(source.rgb + bloom, source.a);
            }
            ENDCG
        }
    }
}
