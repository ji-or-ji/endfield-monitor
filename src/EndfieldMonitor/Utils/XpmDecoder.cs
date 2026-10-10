using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EndfieldMonitor.Utils;

/// <summary>
/// XPM 解码器。XPM 是纯文本的像素图（本质是 C 源码里的字符串数组），
/// Linux 图标主题里不少老图标就是它。客户端只会画位图，而采集端是把
/// 图标文件原样转发的，所以这里得自己把它啃下来——不引第三方库。
///
/// 只认常见写法：#RGB / #RRGGBB / #RRRRGGGGBBBB、None，加一小撮颜色名。
/// 认不出就返回 null，交给上层退回本地字形，绝不抛。
/// </summary>
public static class XpmDecoder
{
    public static Bitmap? TryDecode(byte[] data)
    {
        try
        {
            return Decode(data);
        }
        catch
        {
            return null;   // 畸形文件当没有，不影响别的行
        }
    }

    private static Bitmap? Decode(byte[] data)
    {
        var parts = ExtractQuoted(Encoding.Latin1.GetString(data));
        if (parts.Count < 2) return null;

        var header = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (header.Length < 4) return null;
        int w = ParseInt(header[0]);
        int h = ParseInt(header[1]);
        int ncolors = ParseInt(header[2]);
        int cpp = ParseInt(header[3]);
        if (w <= 0 || h <= 0 || ncolors <= 0 || cpp <= 0) return null;
        if (parts.Count < 1 + ncolors + h) return null;

        // 调色板：键（cpp 个字符）-> 颜色。颜色统一打包成 0xAARRGGBB。
        var palette = new Dictionary<string, uint>(ncolors, StringComparer.Ordinal);
        for (int i = 0; i < ncolors; i++)
        {
            var line = parts[1 + i];
            if (line.Length < cpp) return null;
            string key = line[..cpp];
            var rest = line[cpp..].Trim();
            // rest 形如 "c #AABBCC" / "c None" / "m white"：跳过类型记号，剩下的是颜色
            int sp = rest.IndexOf(' ');
            palette[key] = ParseColor(sp >= 0 ? rest[(sp + 1)..].Trim() : "");
        }

        int stride = w * 4;
        var buf = new byte[stride * h];   // BGRA8888，非预乘
        for (int y = 0; y < h; y++)
        {
            var row = parts[1 + ncolors + y];
            for (int x = 0; x < w; x++)
            {
                if ((x + 1) * cpp > row.Length) break;
                string key = row.Substring(x * cpp, cpp);
                uint c = palette.TryGetValue(key, out var pc) ? pc : 0xFF000000u;
                int o = y * stride + x * 4;
                buf[o + 0] = (byte)(c & 0xFF);          // B
                buf[o + 1] = (byte)((c >> 8) & 0xFF);   // G
                buf[o + 2] = (byte)((c >> 16) & 0xFF);  // R
                buf[o + 3] = (byte)((c >> 24) & 0xFF);  // A
            }
        }

        return ToBitmap(buf, w, h);
    }

    /// <summary>把非预乘的 BGRA 缓冲写进 WriteableBitmap。Skia 要预乘，这里顺手乘上。</summary>
    private static WriteableBitmap ToBitmap(byte[] bgra, int w, int h)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                      PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            int rowBytes = w * 4;
            var line = new byte[rowBytes];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * rowBytes + x * 4;
                    int a = bgra[i + 3];
                    line[x * 4 + 0] = (byte)(bgra[i + 0] * a / 255);
                    line[x * 4 + 1] = (byte)(bgra[i + 1] * a / 255);
                    line[x * 4 + 2] = (byte)(bgra[i + 2] * a / 255);
                    line[x * 4 + 3] = (byte)a;
                }
                Marshal.Copy(line, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), rowBytes);
            }
        }
        return bmp;
    }

    /// <summary>把 XPM 里的所有双引号字符串抠出来（处理 \ 转义）。</summary>
    private static List<string> ExtractQuoted(string text)
    {
        var list = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '"') { i++; continue; }
            i++;
            var sb = new StringBuilder();
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    char n = text[i + 1];
                    sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n });
                    i += 2;
                }
                else
                {
                    sb.Append(text[i++]);
                }
            }
            i++;   // 跳过收尾的引号
            list.Add(sb.ToString());
        }
        return list;
    }

    private static int ParseInt(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>颜色值 -> 0xAARRGGBB。None / 认不出都是「透明」。</summary>
    private static uint ParseColor(string v)
    {
        if (v.Length == 0 || v.Equals("None", StringComparison.OrdinalIgnoreCase)) return 0u;
        if (v[0] == '#')
        {
            var hex = v[1..].Trim();
            if (hex.Length == 6 && TryHex(hex, out var v6))
                return 0xFF000000u | v6;   // #RRGGBB
            if (hex.Length == 3)
                return ParseColor("#" + new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] }));
            if (hex.Length == 12
                && TryHex(hex[0..4], out var r16)
                && TryHex(hex[4..8], out var g16)
                && TryHex(hex[8..12], out var b16))
                return 0xFF000000u | ((r16 >> 8) << 16) | ((g16 >> 8) << 8) | (b16 >> 8);   // 每个分量 16 位
            return 0xFF000000u;
        }
        return Named.TryGetValue(v.ToLowerInvariant(), out var named) ? named : 0xFF000000u;
    }

    private static bool TryHex(string hex, out uint value) =>
        uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    /// <summary>少量常见颜色名，值按 0xAARRGGBB。</summary>
    private static readonly Dictionary<string, uint> Named = new(StringComparer.Ordinal)
    {
        ["black"] = 0xFF000000u,
        ["white"] = 0xFFFFFFFFu,
        ["red"] = 0xFFFF0000u,
        ["green"] = 0xFF00C000u,
        ["blue"] = 0xFF0000FFu,
        ["yellow"] = 0xFFFFFF00u,
        ["cyan"] = 0xFF00FFFFu,
        ["magenta"] = 0xFFFF00FFu,
        ["gray"] = 0xFF808080u,
        ["grey"] = 0xFF808080u,
        ["orange"] = 0xFFFFA500u,
    };
}
