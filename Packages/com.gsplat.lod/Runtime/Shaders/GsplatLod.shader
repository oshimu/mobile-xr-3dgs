// Gaussian splat rendering for the LoD streaming pool.
// Projection math adapted from gsplat-unity (Copyright (c) 2025 Yize Wu, MIT)
// which in turn derives from the PlayCanvas engine gsplat chunks
// (Copyright (c) 2011-2024 PlayCanvas Ltd, MIT).
Shader "GsplatLod/Splat"
{
    Properties {}
    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
        }

        Pass
        {
            ZWrite Off
            Blend One OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma require compute
            // Required for XR Single Pass Instanced: without it the
            // STEREO_INSTANCING_ON variant is never compiled, so
            // unity_StereoEyeIndex and SV_RenderTargetArrayIndex stay unset
            // and nothing reaches the stereo texture array.
            #pragma multi_compile_instancing
            // Declares only the splat pool buffers the pool actually uses;
            // see GsplatLodRecord.hlsl for why GLES needs this.
            #pragma multi_compile GSPLAT_POOLS_1 GSPLAT_POOLS_2 GSPLAT_POOLS_4

            #include "UnityCG.cginc"
            #include "GsplatLodRecord.hlsl"

            bool _GammaToLinear;
            int _SplatCount;
            int _SplatInstanceSize;
            float _KernelAlphaCutoff;   // min contributing alpha; larger = smaller quads (fill-rate)
            float _MinPixelRadius;      // min on-screen splat radius (px); smaller splats culled (fill-rate)
            float4x4 _MATRIX_M;
            StructuredBuffer<uint> _OrderBuffer;   // sorted globalIndex list

            static const float4 discardVec = float4(0.0, 0.0, 2.0, 1.0);

            struct appdata
            {
                float4 vertex : POSITION;
                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                uint instanceID : SV_InstanceID;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
                // Render extent in std devs; constant per splat, so the
                // fragment's Gaussian falloff must be scaled to match it.
                nointerpolation float stdDev : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3x3 QuatToMat3(float4 R) // R layout: (w, x, y, z)
            {
                float4 R2 = R + R;
                float X = R2.x * R.w;
                float4 Y = R2.y * R;
                float4 Z = R2.z * R;
                float W = R2.w * R.w;
                return float3x3(
                    1.0 - Z.z - W, Y.z + X, Y.w - Z.x,
                    Y.z - X, 1.0 - Y.y - W, Z.w + Y.x,
                    Y.w + Z.x, Z.w - Y.x, 1.0 - Y.y - Z.z);
            }

            void CalcCovariance(float4 quatWxyz, float3 scale, out float3 covA, out float3 covB)
            {
                float3x3 rot = QuatToMat3(quatWxyz);
                float3x3 M = transpose(float3x3(
                    scale.x * rot[0],
                    scale.y * rot[1],
                    scale.z * rot[2]));
                covA = float3(dot(M[0], M[0]), dot(M[0], M[1]), dot(M[0], M[2]));
                covB = float3(dot(M[1], M[1]), dot(M[1], M[2]), dot(M[2], M[2]));
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = discardVec;

                #if !defined(UNITY_INSTANCING_ENABLED) && !defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && !defined(UNITY_STEREO_INSTANCING_ENABLED)
                uint order = v.instanceID * _SplatInstanceSize + asuint(v.vertex.z);
                #else
                uint order = unity_InstanceID * _SplatInstanceSize + asuint(v.vertex.z);
                #endif
                if (order >= (uint)_SplatCount)
                    return o;

                uint poolIndex;
                if (!TrySplatPoolIndex(_OrderBuffer[order], /*out*/ poolIndex))
                    return o;
                SplatRecord rec = LoadSplat(poolIndex);
                float2 cornerUV = float2(v.vertex.x, v.vertex.y);

                // center
                float4x4 modelView = mul(UNITY_MATRIX_V, _MATRIX_M);
                float4 centerView = mul(modelView, float4(rec.position, 1.0));
                if (centerView.z > 0.0)
                    return o;
                float4 centerProj = mul(UNITY_MATRIX_P, centerView);
                centerProj.z = clamp(centerProj.z, -abs(centerProj.w), abs(centerProj.w));
                float3 view = centerView.xyz / centerView.w;

                // covariance -> screen-space ellipse (EWA splatting)
                float4 q = DecodeRotation(rec);           // (x, y, z, w)
                float3 scale = DecodeScale(rec);
                float3 covA, covB;
                CalcCovariance(float4(q.w, q.x, q.y, q.z), scale, covA, covB);
                float3x3 Vrk = float3x3(
                    covA.x, covA.y, covA.z,
                    covA.y, covB.x, covB.y,
                    covA.z, covB.y, covB.z);

                float focal = _ScreenParams.x * UNITY_MATRIX_P[0][0];
                float3 vdir = unity_OrthoParams.w == 1.0 ? float3(0.0, 0.0, 1.0) : view;
                float J1 = focal / vdir.z;
                float2 J2 = -J1 / vdir.z * vdir.xy;
                float3x3 J = float3x3(
                    J1, 0.0, J2.x,
                    0.0, J1, J2.y,
                    0.0, 0.0, 0.0);
                float3x3 T = mul(J, (float3x3)modelView);
                float3x3 cov = mul(mul(T, Vrk), transpose(T));

                float diagonal1 = cov[0][0] + 0.3;
                float offDiagonal = cov[0][1];
                float diagonal2 = cov[1][1] + 0.3;
                float mid = 0.5 * (diagonal1 + diagonal2);
                float radius = length(float2((diagonal1 - diagonal2) / 2.0, offDiagonal));
                float lambda1 = mid + radius;
                float lambda2 = max(mid - radius, 0.1);
                // LoD representatives are drawn over a wider extent so they
                // cover the cell they stand in for; leaves keep sqrt(8) sigma.
                float lodOpacity = DecodeLodOpacity(rec);
                float stdDev = LodStdDev(lodOpacity);

                float vmin = min(1024.0, min(_ScreenParams.x, _ScreenParams.y));
                float l1 = min(stdDev * sqrt(lambda1), 2.0 * vmin);
                float l2 = min(stdDev * sqrt(lambda2), 2.0 * vmin);

                float minDiameter = 2.0 * _MinPixelRadius;
                if (l1 < minDiameter && l2 < minDiameter)
                    return o;
                float2 c = centerProj.ww / _ScreenParams.xy;
                float maxL = max(l1, l2);
                if (any(abs(centerProj.xy) - float2(maxL, maxL) * c > centerProj.ww))
                    return o;

                float2 diagonalVector = normalize(float2(offDiagonal, lambda1 - diagonal1));
                float2 v1 = l1 * diagonalVector;
                float2 v2 = l2 * float2(diagonalVector.y, -diagonalVector.x);

                float4 color = DecodeColor(rec);
                // For a representative the LoD opacity is the alpha (spark
                // keeps it above 1 and lets the blend saturate).
                if (lodOpacity > 0.0)
                    color.a = lodOpacity;

                // clip corner to the alpha-significant extent
                float cutoff = max(_KernelAlphaCutoff, 1.0 / 255.0);
                if (color.a <= cutoff)
                    return o;
                float clip = LodClip(color.a, stdDev, cutoff);
                if (clip <= 0.0)
                    return o;
                float2 offset = (cornerUV.x * v1 + cornerUV.y * v2) * c * clip;

                o.vertex = centerProj + float4(offset.x, _ProjectionParams.x * offset.y, 0, 0);
                o.color = color;
                o.uv = cornerUV * clip;
                o.stdDev = stdDev;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float A = dot(i.uv, i.uv);
                if (A > 1.0) discard;
                // A is normalized to the quad, whose half-extent is stdDev
                // sigma, so z2 = stdDev^2 * A. For a leaf LodAlpha is a plain
                // Gaussian falloff.
                float alpha = LodAlpha(i.color.a, i.stdDev * i.stdDev * A);
                if (alpha < max(_KernelAlphaCutoff, 1.0 / 255.0)) discard;
                float3 rgb = _GammaToLinear ? GammaToLinearSpace(i.color.rgb) : i.color.rgb;
                return float4(rgb * alpha, alpha);
            }
            ENDHLSL
        }
    }
}
