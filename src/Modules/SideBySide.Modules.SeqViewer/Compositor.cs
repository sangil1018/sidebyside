namespace SideBySide.Modules.SeqViewer;

/// <summary>seq-viewer와 같은 블렌드 모드 (Canvas globalCompositeOperation 이름 순서).</summary>
public enum BlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Hue,
    Saturation,
    Color,
    Luminosity,
    Add,
    Behind,
    AlphaIn,
    AlphaOut,
}

/// <summary>합성할 레이어 한 장 (모두 같은 크기의 Pbgra32 픽셀).</summary>
public readonly record struct CompositeLayer(byte[] Pixels, BlendMode Mode, double Opacity);

/// <summary>
/// W3C Compositing and Blending 규칙의 레이어 합성 (Canvas 2D와 같은 결과). 미리 곱한 알파 BGRA 바이트를 행 단위로 나눠
/// 여러 코어에서 동시에 계산한다. 모든 레이어가 Normal이면 이 경로를 쓰지 않고 WPF(GPU) 합성에 맡긴다.
/// </summary>
public static class Compositor
{
    private static readonly float[] ToUnit = Enumerable.Range(0, 256).Select(i => i / 255f).ToArray();

    /// <summary>아래 레이어부터 순서대로 합성한 결과 (투명 바탕).</summary>
    public static byte[] Compose(IReadOnlyList<CompositeLayer> bottomToTop, int width, int height)
    {
        var stride = width * 4;
        var output = new byte[stride * height];
        if (bottomToTop.Count == 0) return output;

        Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, y =>
        {
            var row = y * stride;
            foreach (var layer in bottomToTop)
            {
                var opacity = (float)Math.Clamp(layer.Opacity, 0, 1);
                if (opacity <= 0) continue;
                BlendRow(output.AsSpan(row, stride), layer.Pixels.AsSpan(row, stride), layer.Mode, opacity);
            }
        });

        return output;
    }

    private static void BlendRow(Span<byte> dst, ReadOnlySpan<byte> src, BlendMode mode, float opacity)
    {
        for (var i = 0; i < dst.Length; i += 4)
        {
            // 원본(위) 미리 곱한 값 × 레이어 불투명도
            var sa = ToUnit[src[i + 3]] * opacity;
            if (sa <= 0 && mode is not (BlendMode.AlphaIn)) continue;
            var sb = ToUnit[src[i]] * opacity;
            var sg = ToUnit[src[i + 1]] * opacity;
            var sr = ToUnit[src[i + 2]] * opacity;

            var da = ToUnit[dst[i + 3]];
            var db = ToUnit[dst[i]];
            var dg = ToUnit[dst[i + 1]];
            var dr = ToUnit[dst[i + 2]];

            float ob, og, or, oa;
            switch (mode)
            {
                case BlendMode.Add:
                    ob = sb + db; og = sg + dg; or = sr + dr; oa = sa + da;
                    break;
                case BlendMode.Behind:
                    ob = db + (sb * (1 - da)); og = dg + (sg * (1 - da)); or = dr + (sr * (1 - da)); oa = da + (sa * (1 - da));
                    break;
                case BlendMode.AlphaIn:
                    ob = db * sa; og = dg * sa; or = dr * sa; oa = da * sa;
                    break;
                case BlendMode.AlphaOut:
                    ob = db * (1 - sa); og = dg * (1 - sa); or = dr * (1 - sa); oa = da * (1 - sa);
                    break;
                default:
                {
                    // 직선 색으로 혼합 함수 B(Cb, Cs) 계산 → co = cs(1-ab) + cb(1-as) + as·ab·B
                    float cr = Un(sr, sa), cg = Un(sg, sa), cb = Un(sb, sa);
                    float br = Un(dr, da), bg = Un(dg, da), bb = Un(db, da);
                    float mr, mg, mb;
                    if (mode is BlendMode.Hue or BlendMode.Saturation or BlendMode.Color or BlendMode.Luminosity)
                    {
                        (mr, mg, mb) = NonSeparable(mode, br, bg, bb, cr, cg, cb);
                    }
                    else
                    {
                        mr = Separable(mode, br, cr);
                        mg = Separable(mode, bg, cg);
                        mb = Separable(mode, bb, cb);
                    }

                    var both = sa * da;
                    or = (sr * (1 - da)) + (dr * (1 - sa)) + (both * mr);
                    og = (sg * (1 - da)) + (dg * (1 - sa)) + (both * mg);
                    ob = (sb * (1 - da)) + (db * (1 - sa)) + (both * mb);
                    oa = sa + da - both;
                    break;
                }
            }

            oa = Math.Clamp(oa, 0, 1);
            dst[i] = ToByte(Math.Min(ob, oa));
            dst[i + 1] = ToByte(Math.Min(og, oa));
            dst[i + 2] = ToByte(Math.Min(or, oa));
            dst[i + 3] = ToByte(oa);
        }
    }

    private static float Un(float premultiplied, float alpha) => alpha > 0 ? Math.Min(1, premultiplied / alpha) : 0;

    private static byte ToByte(float v) => (byte)Math.Clamp((int)((v * 255f) + 0.5f), 0, 255);

    internal static float Separable(BlendMode mode, float b, float s) => mode switch
    {
        BlendMode.Multiply => b * s,
        BlendMode.Screen => b + s - (b * s),
        BlendMode.Overlay => HardLight(s, b),
        BlendMode.Darken => Math.Min(b, s),
        BlendMode.Lighten => Math.Max(b, s),
        BlendMode.ColorDodge => b <= 0 ? 0 : s >= 1 ? 1 : Math.Min(1, b / (1 - s)),
        BlendMode.ColorBurn => b >= 1 ? 1 : s <= 0 ? 0 : 1 - Math.Min(1, (1 - b) / s),
        BlendMode.HardLight => HardLight(b, s),
        BlendMode.SoftLight => SoftLight(b, s),
        BlendMode.Difference => Math.Abs(b - s),
        BlendMode.Exclusion => b + s - (2 * b * s),
        _ => s, // Normal
    };

    private static float HardLight(float b, float s) => s <= 0.5f ? b * 2 * s : Screen(b, (2 * s) - 1);

    private static float Screen(float b, float s) => b + s - (b * s);

    private static float SoftLight(float b, float s)
    {
        if (s <= 0.5f) return b - ((1 - (2 * s)) * b * (1 - b));
        var d = b <= 0.25f ? ((((16 * b) - 12) * b) + 4) * b : MathF.Sqrt(b);
        return b + (((2 * s) - 1) * (d - b));
    }

    // ── 비분리 모드 (W3C 정의) ──────────────────────────────────

    private static (float R, float G, float B) NonSeparable(BlendMode mode, float br, float bg, float bb, float sr, float sg, float sb) => mode switch
    {
        BlendMode.Hue => SetLum(SetSat((sr, sg, sb), Sat(br, bg, bb)), Lum(br, bg, bb)),
        BlendMode.Saturation => SetLum(SetSat((br, bg, bb), Sat(sr, sg, sb)), Lum(br, bg, bb)),
        BlendMode.Color => SetLum((sr, sg, sb), Lum(br, bg, bb)),
        _ => SetLum((br, bg, bb), Lum(sr, sg, sb)), // Luminosity
    };

    private static float Lum(float r, float g, float b) => (0.3f * r) + (0.59f * g) + (0.11f * b);

    private static float Sat(float r, float g, float b) => Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));

    private static (float, float, float) SetLum((float R, float G, float B) c, float l)
    {
        var d = l - Lum(c.R, c.G, c.B);
        return Clip((c.R + d, c.G + d, c.B + d));
    }

    private static (float, float, float) Clip((float R, float G, float B) c)
    {
        var l = Lum(c.R, c.G, c.B);
        var n = Math.Min(c.R, Math.Min(c.G, c.B));
        var x = Math.Max(c.R, Math.Max(c.G, c.B));
        if (n < 0)
        {
            c = (l + ((c.R - l) * l / (l - n)), l + ((c.G - l) * l / (l - n)), l + ((c.B - l) * l / (l - n)));
        }

        if (x > 1)
        {
            c = (l + ((c.R - l) * (1 - l) / (x - l)), l + ((c.G - l) * (1 - l) / (x - l)), l + ((c.B - l) * (1 - l) / (x - l)));
        }

        return c;
    }

    private static (float, float, float) SetSat((float R, float G, float B) c, float s)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max <= min) return (0, 0, 0);
        return (Scale(c.R), Scale(c.G), Scale(c.B));

        float Scale(float v) => (v - min) * s / (max - min);
    }
}
