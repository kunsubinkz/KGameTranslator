namespace GameTranslator.Platform.Windows.Render;

/// <summary>HLSL Shader Model 5, biên dịch lúc chạy bằng D3DCompile (d3dcompiler_47.dll có sẵn trong Windows).
/// Đổi NV12/YUY2 sang RGB (BT.709 cho HD, BT.601 cho SD, dải giới hạn) rồi phóng theo 3 chế độ.</summary>
internal static class Shaders
{
    public const string Source = """
        Texture2D texA : register(t0);   // BGRA | Y (NV12) | YUY2 gói (w/2 x h, RGBA)
        Texture2D texB : register(t1);   // UV (NV12)
        Texture2D texE : register(t2);   // kết quả EASU (pass 2 của FSR)
        SamplerState linearS : register(s0);
        SamplerState pointS : register(s1);

        cbuffer Params : register(b0)
        {
            float2 srcSize;
            float2 dstSize;
            int mode;     // 0 = mặc định (song tuyến), 1 = mịn (Catmull-Rom + làm nét, khi thu nhỏ), 2 = điểm ảnh
            float sharp;  // RCAS: exp2(-stops)
            int fmt;      // 0 = BGRA, 1 = NV12, 2 = YUY2
            int hd;       // 1 = BT.709
            float2 offset; // góc trên trái vùng video trong back buffer (pass RCAS)
            float2 pad;
        };

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VS(uint id : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.uv = uv;
            o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        float3 yuv2rgb(float y, float u, float v)
        {
            y = (y - 16.0 / 255.0) * (255.0 / 219.0);
            u = (u - 0.5) * (255.0 / 224.0);
            v = (v - 0.5) * (255.0 / 224.0);
            if (hd != 0) return float3(y + 1.5748 * v, y - 0.1873 * u - 0.4681 * v, y + 1.8556 * u);
            return float3(y + 1.402 * v, y - 0.3441 * u - 0.7141 * v, y + 1.772 * u);
        }

        float3 Fetch(int2 p)
        {
            p = clamp(p, int2(0, 0), int2(srcSize) - 1);
            if (fmt == 0) return texA.Load(int3(p, 0)).rgb;
            if (fmt == 1)
            {
                float y = texA.Load(int3(p, 0)).r;
                float2 uv = texB.Load(int3(p >> 1, 0)).rg;
                return saturate(yuv2rgb(y, uv.x, uv.y));
            }
            float4 t = texA.Load(int3(int2(p.x >> 1, p.y), 0));
            float yy = ((p.x & 1) == 0) ? t.r : t.b;
            return saturate(yuv2rgb(yy, t.g, t.a));
        }

        float4 weights(float f)
        {
            float f2 = f * f, f3 = f2 * f;
            return float4(-0.5 * f3 + f2 - 0.5 * f,
                           1.5 * f3 - 2.5 * f2 + 1.0,
                          -1.5 * f3 + 2.0 * f2 + 0.5 * f,
                           0.5 * f3 - 0.5 * f2);
        }

        float4 PS(VSOut i) : SV_Target
        {
            float2 pos = i.uv * srcSize - 0.5;
            int2 p = int2(floor(pos));
            float2 fr = frac(pos);

            if (mode == 2) return float4(Fetch(int2(i.uv * srcSize)), 1);

            if (mode == 0)
            {
                float3 a = lerp(Fetch(p), Fetch(p + int2(1, 0)), fr.x);
                float3 b = lerp(Fetch(p + int2(0, 1)), Fetch(p + int2(1, 1)), fr.x);
                return float4(lerp(a, b, fr.y), 1);
            }

            float4 wx = weights(fr.x), wy = weights(fr.y);
            float3 acc = 0;
            [unroll] for (int y = 0; y < 4; y++)
            {
                float3 row = 0;
                [unroll] for (int x = 0; x < 4; x++)
                    row += Fetch(p + int2(x - 1, y - 1)) * wx[x];
                acc += row * wy[y];
            }
            float3 c = saturate(acc);
            float3 blur = (Fetch(p + int2(0, 1)) + Fetch(p + int2(1, 1)) + Fetch(p + int2(0, 0)) + Fetch(p + int2(1, 0))) * 0.25;
            return float4(saturate(c + sharp * (c - blur)), 1);
        }

        // ===================================================================================
        // AMD FidelityFX Super Resolution 1.0 (EASU + RCAS), viết lại cho bản này (fp32, đọc nguồn bằng Fetch).
        // Copyright (c) 2021 Advanced Micro Devices, Inc. All rights reserved. Giấy phép MIT:
        // Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
        // documentation files (the "Software"), to deal in the Software without restriction, including without limitation
        // the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
        // to permit persons to whom the Software is furnished to do so, subject to the following conditions: The above
        // copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
        // THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.
        // ===================================================================================

        float Luma2(float3 c) { return c.b * 0.5 + (c.r * 0.5 + c.g); }

        // Phân tích hướng cạnh và độ dài cạnh trên một góc phần tư của lưới 2x2 (f, g, j, k).
        void EasuSet(inout float2 dir, inout float len, float w, float lA, float lB, float lC, float lD, float lE)
        {
            // lA trên, lB trái, lC giữa, lD phải, lE dưới
            float dc = lD - lC, cb = lC - lB;
            float lenX = max(abs(dc), abs(cb));
            lenX = lenX > 0 ? 1.0 / lenX : 0;
            float dirX = lD - lB;
            dir.x += dirX * w;
            lenX = saturate(abs(dirX) * lenX);
            lenX *= lenX;
            len += lenX * w;
            float ec = lE - lC, ca = lC - lA;
            float lenY = max(abs(ec), abs(ca));
            lenY = lenY > 0 ? 1.0 / lenY : 0;
            float dirY = lE - lA;
            dir.y += dirY * w;
            lenY = saturate(abs(dirY) * lenY);
            lenY *= lenY;
            len += lenY * w;
        }

        // Một tap của bộ lọc Lanczos-2 xấp xỉ, kéo giãn theo hướng cạnh.
        void EasuTap(inout float3 aC, inout float aW, float2 off, float2 dir, float2 len2, float lob, float clp, float3 c)
        {
            float2 v;
            v.x = off.x * dir.x + off.y * dir.y;
            v.y = off.x * -dir.y + off.y * dir.x;
            v *= len2;
            float d2 = min(v.x * v.x + v.y * v.y, clp);
            float wB = (2.0 / 5.0) * d2 - 1.0;
            float wA = lob * d2 - 1.0;
            wB *= wB;
            wA *= wA;
            wB = (25.0 / 16.0) * wB - (25.0 / 16.0 - 1.0);
            float w = wB * wA;
            aC += c * w;
            aW += w;
        }

        // Pass 1: EASU (Edge-Adaptive Spatial Upsampling), ghi vào texture trung gian cỡ vùng video.
        float4 PSEasu(VSOut i) : SV_Target
        {
            float2 pp = i.pos.xy * (srcSize / dstSize) - 0.5;
            float2 fp = floor(pp);
            pp -= fp;
            int2 p = int2(fp);
            //    b c
            //  e f g h
            //  i j k l
            //    n o
            float3 bC = Fetch(p + int2(0, -1)), cC = Fetch(p + int2(1, -1));
            float3 eC = Fetch(p + int2(-1, 0)), fC = Fetch(p), gC = Fetch(p + int2(1, 0)), hC = Fetch(p + int2(2, 0));
            float3 iC = Fetch(p + int2(-1, 1)), jC = Fetch(p + int2(0, 1)), kC = Fetch(p + int2(1, 1)), lC = Fetch(p + int2(2, 1));
            float3 nC = Fetch(p + int2(0, 2)), oC = Fetch(p + int2(1, 2));
            float bL = Luma2(bC), cL = Luma2(cC), eL = Luma2(eC), fL = Luma2(fC), gL = Luma2(gC), hL = Luma2(hC);
            float iL = Luma2(iC), jL = Luma2(jC), kL = Luma2(kC), lL = Luma2(lC), nL = Luma2(nC), oL = Luma2(oC);

            float2 dir = 0;
            float len = 0;
            EasuSet(dir, len, (1.0 - pp.x) * (1.0 - pp.y), bL, eL, fL, gL, jL);
            EasuSet(dir, len, pp.x * (1.0 - pp.y), cL, fL, gL, hL, kL);
            EasuSet(dir, len, (1.0 - pp.x) * pp.y, fL, iL, jL, kL, nL);
            EasuSet(dir, len, pp.x * pp.y, gL, jL, kL, lL, oL);

            float2 dir2 = dir * dir;
            float dirR = dir2.x + dir2.y;
            bool zro = dirR < (1.0 / 32768.0);
            dirR = zro ? 1.0 : rsqrt(dirR);
            dir.x = zro ? 1.0 : dir.x;
            dir *= dirR;
            len = len * 0.5;
            len *= len;
            float stretch = (dir.x * dir.x + dir.y * dir.y) / max(abs(dir.x), abs(dir.y));
            float2 len2 = float2(1.0 + (stretch - 1.0) * len, 1.0 - 0.5 * len);
            float lob = 0.5 + ((1.0 / 4.0 - 0.04) - 0.5) * len;
            float clp = 1.0 / lob;

            float3 min4 = min(min(fC, gC), min(jC, kC));
            float3 max4 = max(max(fC, gC), max(jC, kC));
            float3 aC = 0;
            float aW = 0;
            EasuTap(aC, aW, float2(0, -1) - pp, dir, len2, lob, clp, bC);
            EasuTap(aC, aW, float2(1, -1) - pp, dir, len2, lob, clp, cC);
            EasuTap(aC, aW, float2(-1, 1) - pp, dir, len2, lob, clp, iC);
            EasuTap(aC, aW, float2(0, 1) - pp, dir, len2, lob, clp, jC);
            EasuTap(aC, aW, float2(0, 0) - pp, dir, len2, lob, clp, fC);
            EasuTap(aC, aW, float2(-1, 0) - pp, dir, len2, lob, clp, eC);
            EasuTap(aC, aW, float2(1, 1) - pp, dir, len2, lob, clp, kC);
            EasuTap(aC, aW, float2(2, 1) - pp, dir, len2, lob, clp, lC);
            EasuTap(aC, aW, float2(2, 0) - pp, dir, len2, lob, clp, hC);
            EasuTap(aC, aW, float2(1, 0) - pp, dir, len2, lob, clp, gC);
            EasuTap(aC, aW, float2(1, 2) - pp, dir, len2, lob, clp, oC);
            EasuTap(aC, aW, float2(0, 2) - pp, dir, len2, lob, clp, nC);
            // Chặn trong khoảng min/max của 4 điểm gần nhất để không có viền sáng/tối (ringing).
            return float4(min(max4, max(min4, aC / aW)), 1);
        }

        // ===================================================================================
        // Pass 0 (tùy chọn): FXAA ở độ phân giải gốc, làm mượt cạnh răng cưa có sẵn trong hình game (Switch thường không khử
        // răng cưa) trước khi FSR phóng lên. Thuật toán FXAA của Timothy Lottes (NVIDIA), bản rút gọn kiểu "FXAA PC".
        // ===================================================================================
        float LumaF(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

        float3 FetchBilinear(float2 pos) // pos: tọa độ điểm ảnh gốc, tâm điểm ảnh ở .5
        {
            float2 q = pos - 0.5;
            float2 fq = floor(q);
            float2 t = q - fq;
            int2 p = int2(fq);
            float3 a = lerp(Fetch(p), Fetch(p + int2(1, 0)), t.x);
            float3 b = lerp(Fetch(p + int2(0, 1)), Fetch(p + int2(1, 1)), t.x);
            return lerp(a, b, t.y);
        }

        float4 PSFxaa(VSOut i) : SV_Target
        {
            int2 p = int2(i.pos.xy);
            float2 pos = float2(p) + 0.5;
            float3 rgbM = Fetch(p);
            float lM = LumaF(rgbM);
            float lNW = LumaF(Fetch(p + int2(-1, -1))), lNE = LumaF(Fetch(p + int2(1, -1)));
            float lSW = LumaF(Fetch(p + int2(-1, 1))), lSE = LumaF(Fetch(p + int2(1, 1)));
            float lMin = min(lM, min(min(lNW, lNE), min(lSW, lSE)));
            float lMax = max(lM, max(max(lNW, lNE), max(lSW, lSE)));
            // Vùng phẳng hoặc tương phản thấp: giữ nguyên (không làm mờ chi tiết).
            if (lMax - lMin < max(0.0312, lMax * 0.125)) return float4(rgbM, 1);

            float2 dir = float2(-((lNW + lNE) - (lSW + lSE)), (lNW + lSW) - (lNE + lSE));
            float dirReduce = max((lNW + lNE + lSW + lSE) * (0.25 * (1.0 / 8.0)), 1.0 / 128.0);
            float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
            dir = clamp(dir * rcpDirMin, -8.0, 8.0);

            float3 rgbA = 0.5 * (FetchBilinear(pos + dir * (1.0 / 3.0 - 0.5)) + FetchBilinear(pos + dir * (2.0 / 3.0 - 0.5)));
            float3 rgbB = rgbA * 0.5 + 0.25 * (FetchBilinear(pos + dir * -0.5) + FetchBilinear(pos + dir * 0.5));
            float lB = LumaF(rgbB);
            return float4((lB < lMin || lB > lMax) ? rgbA : rgbB, 1);
        }

        // Pass 2: RCAS (Robust Contrast-Adaptive Sharpening), đọc kết quả EASU, ghi ra back buffer.
        float4 PSRcas(VSOut i) : SV_Target
        {
            int2 p = int2(i.pos.xy - offset);
            int2 mx = int2(dstSize) - 1;
            //   b
            // d e f
            //   h
            float3 b = texE.Load(int3(clamp(p + int2(0, -1), 0, mx), 0)).rgb;
            float3 d = texE.Load(int3(clamp(p + int2(-1, 0), 0, mx), 0)).rgb;
            float3 e = texE.Load(int3(clamp(p, 0, mx), 0)).rgb;
            float3 f = texE.Load(int3(clamp(p + int2(1, 0), 0, mx), 0)).rgb;
            float3 h = texE.Load(int3(clamp(p + int2(0, 1), 0, mx), 0)).rgb;
            float bL = Luma2(b), dL = Luma2(d), eL = Luma2(e), fL = Luma2(f), hL = Luma2(h);
            // Bớt làm nét ở vùng nhiễu.
            float nz = 0.25 * bL + 0.25 * dL + 0.25 * fL + 0.25 * hL - eL;
            float range = max(max(max(bL, dL), max(eL, fL)), hL) - min(min(min(bL, dL), min(eL, fL)), hL);
            nz = range > 0 ? saturate(abs(nz) / range) : 0;
            nz = -0.5 * nz + 1.0;
            float3 mn4 = min(min(b, d), min(f, h));
            float3 mx4 = max(max(b, d), max(f, h));
            float3 hitMin = mn4 / max(4.0 * mx4, 1e-5);
            float3 hitMax = (1.0 - mx4) / min(4.0 * mn4 - 4.0, -1e-5);
            float3 lobeRGB = max(-hitMin, hitMax);
            float lobe = max(-(0.25 - 1.0 / 16.0), min(max(lobeRGB.r, max(lobeRGB.g, lobeRGB.b)), 0.0)) * sharp;
            lobe *= nz;
            float rcpL = 1.0 / (4.0 * lobe + 1.0);
            return float4(saturate((lobe * (b + d + f + h) + e) * rcpL), 1);
        }
        """;
}
