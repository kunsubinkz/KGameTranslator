using GameTranslator.Core.Pipeline;
using GameTranslator.Platform.Windows.Capture;

namespace GameTranslator.Platform.Windows.Render;

/// <summary>Đổi một vùng của khung NV12/YUY2/BGRA sang BGRA trên CPU (chỉ dùng cho vùng OCR nhỏ và ảnh chọn vùng).</summary>
public static class FrameConvert
{
    public static BgraImage CropToBgra(FrameFormat fmt, byte[] data, int width, int height, int stride, int x, int y, int w, int h)
    {
        var img = BgraImage.Create(w, h);
        bool hd = height >= 720;
        for (int row = 0; row < h; row++)
        {
            int sy = y + row;
            for (int col = 0; col < w; col++)
            {
                int sx = x + col;
                int o = (row * w + col) * 4;
                switch (fmt)
                {
                    case FrameFormat.Bgra:
                    {
                        int s = sy * stride + sx * 4;
                        img.Pixels[o] = data[s]; img.Pixels[o + 1] = data[s + 1]; img.Pixels[o + 2] = data[s + 2];
                        break;
                    }
                    case FrameFormat.Nv12:
                    {
                        int yv = data[sy * stride + sx];
                        int uvOff = stride * height + (sy >> 1) * stride + (sx & ~1);
                        Yuv(yv, data[uvOff], data[uvOff + 1], hd, img.Pixels, o);
                        break;
                    }
                    default:
                    {
                        int s = sy * stride + (sx & ~1) * 2;
                        int yv = (sx & 1) == 0 ? data[s] : data[s + 2];
                        Yuv(yv, data[s + 1], data[s + 3], hd, img.Pixels, o);
                        break;
                    }
                }
                img.Pixels[o + 3] = 255;
            }
        }
        return img;
    }

    private static void Yuv(int y, int u, int v, bool hd, byte[] dst, int o)
    {
        double yy = (y - 16) * (255.0 / 219.0), uu = (u - 128) * (255.0 / 224.0), vv = (v - 128) * (255.0 / 224.0);
        double r, g, b;
        if (hd) { r = yy + 1.5748 * vv; g = yy - 0.1873 * uu - 0.4681 * vv; b = yy + 1.8556 * uu; }
        else { r = yy + 1.402 * vv; g = yy - 0.3441 * uu - 0.7141 * vv; b = yy + 1.772 * uu; }
        dst[o] = (byte)Math.Clamp(b + 0.5, 0, 255);
        dst[o + 1] = (byte)Math.Clamp(g + 0.5, 0, 255);
        dst[o + 2] = (byte)Math.Clamp(r + 0.5, 0, 255);
    }
}
