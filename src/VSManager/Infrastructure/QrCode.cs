using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace VSManager
{
    /// <summary>精简的二维码编码器（字节模式，纠错等级 L，版本 1–10，最多约 270 字节）。</summary>
    public static class QrCode
    {
        // 每个版本（L 级）：第一组块数、每块数据码字，第二组块数、每块数据码字，每块纠错码字
        private static readonly int[][] Blocks =
        {
            null,
            new[] { 1, 19, 0, 0, 7 }, new[] { 1, 34, 0, 0, 10 }, new[] { 1, 55, 0, 0, 15 }, new[] { 1, 80, 0, 0, 20 },
            new[] { 1, 108, 0, 0, 26 }, new[] { 2, 68, 0, 0, 18 }, new[] { 2, 78, 0, 0, 20 }, new[] { 2, 97, 0, 0, 24 },
            new[] { 2, 116, 0, 0, 30 }, new[] { 2, 68, 2, 69, 18 },
        };

        private static readonly int[][] Align =
        {
            null, new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
            new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
        };

        /// <summary>返回模块矩阵 [行, 列]，true 为深色。</summary>
        public static bool[,] Encode(string text, int forceMask = -1)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? "");
            int ver = 0;
            for (int v = 1; v <= 10; v++)
            {
                int cc = v < 10 ? 8 : 16;
                if (4 + cc + data.Length * 8 <= DataCodewords(v) * 8) { ver = v; break; }
            }
            if (ver == 0) throw new ArgumentException("内容过长，无法生成二维码");

            // ---- 数据位流 ----
            var bits = new List<bool>();
            void Add(int val, int n) { for (int i = n - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); }
            Add(4, 4);
            Add(data.Length, ver < 10 ? 8 : 16);
            foreach (var b in data) Add(b, 8);
            int capBits = DataCodewords(ver) * 8;
            Add(0, Math.Min(4, capBits - bits.Count));
            while (bits.Count % 8 != 0) bits.Add(false);
            for (int pad = 0xEC; bits.Count < capBits; pad ^= 0xEC ^ 0x11) Add(pad, 8);
            var dataCw = new byte[bits.Count / 8];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) dataCw[i >> 3] |= (byte)(0x80 >> (i & 7));

            // ---- 分块与纠错 ----
            var spec = Blocks[ver];
            var dBlocks = new List<byte[]>();
            var eBlocks = new List<byte[]>();
            var divisor = RsDivisor(spec[4]);
            int k = 0;
            for (int g = 0; g < 2; g++)
                for (int n = 0; n < spec[g * 2]; n++)
                {
                    var blk = new byte[spec[g * 2 + 1]];
                    Array.Copy(dataCw, k, blk, 0, blk.Length);
                    k += blk.Length;
                    dBlocks.Add(blk);
                    eBlocks.Add(RsRemainder(blk, divisor));
                }
            var all = new List<byte>();
            int maxD = Math.Max(spec[1], spec[3]);
            for (int i = 0; i < maxD; i++) foreach (var b in dBlocks) if (i < b.Length) all.Add(b[i]);
            for (int i = 0; i < spec[4]; i++) foreach (var b in eBlocks) all.Add(b[i]);

            // ---- 矩阵 ----
            int size = 17 + ver * 4;
            var m = new bool[size, size];
            var fn = new bool[size, size];
            void Set(int x, int y, bool dark) { m[y, x] = dark; fn[y, x] = true; }

            for (int i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
            foreach (var (fx, fy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int x = fx + dx, y = fy + dy;
                        if (x < 0 || y < 0 || x >= size || y >= size) continue;
                        int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        Set(x, y, d != 2 && d != 4);
                    }
            var ap = Align[ver];
            for (int i = 0; i < ap.Length; i++)
                for (int j = 0; j < ap.Length; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == ap.Length - 1) || (i == ap.Length - 1 && j == 0)) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            Set(ap[i] + dx, ap[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            DrawFormat(m, fn, size, 0);
            if (ver >= 7) DrawVersion(m, fn, size, ver);

            // 数据按之字形放置
            int bi = 0, total = all.Count * 8;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool up = ((right + 1) & 2) == 0;
                        int y = up ? size - 1 - vert : vert;
                        if (fn[y, x] || bi >= total) continue;
                        m[y, x] = ((all[bi >> 3] >> (7 - (bi & 7))) & 1) != 0;
                        bi++;
                    }
            }

            // ---- 掩码 ----
            int best = forceMask;
            if (best < 0)
            {
                int bestScore = int.MaxValue;
                for (int mask = 0; mask < 8; mask++)
                {
                    ApplyMask(m, fn, size, mask);
                    DrawFormat(m, fn, size, mask);
                    int score = Penalty(m, size);
                    if (score < bestScore) { bestScore = score; best = mask; }
                    ApplyMask(m, fn, size, mask);
                }
            }
            ApplyMask(m, fn, size, best);
            DrawFormat(m, fn, size, best);
            return m;
        }

        public static Bitmap ToBitmap(bool[,] m, int scale, Color dark, Color light, int quiet = 4)
        {
            int n = m.GetLength(0), px = (n + quiet * 2) * scale;
            var bmp = new Bitmap(px, px);
            using (var g = Graphics.FromImage(bmp))
            using (var b = new SolidBrush(dark))
            {
                g.Clear(light);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        if (m[y, x]) g.FillRectangle(b, (x + quiet) * scale, (y + quiet) * scale, scale, scale);
            }
            return bmp;
        }

        private static int DataCodewords(int v)
        {
            var s = Blocks[v];
            return s[0] * s[1] + s[2] * s[3];
        }

        private static void DrawFormat(bool[,] m, bool[,] fn, int size, int mask)
        {
            int data = 1 << 3 | mask; // 纠错等级 L = 01
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;
            void Set(int x, int y, int i) { m[y, x] = ((bits >> i) & 1) != 0; fn[y, x] = true; }
            for (int i = 0; i <= 5; i++) Set(8, i, i);
            Set(8, 7, 6);
            Set(8, 8, 7);
            Set(7, 8, 8);
            for (int i = 9; i < 15; i++) Set(14 - i, 8, i);
            for (int i = 0; i < 8; i++) Set(size - 1 - i, 8, i);
            for (int i = 8; i < 15; i++) Set(8, size - 15 + i, i);
            m[size - 8, 8] = true;
            fn[size - 8, 8] = true;
        }

        private static void DrawVersion(bool[,] m, bool[,] fn, int size, int ver)
        {
            int rem = ver;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = ver << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                m[b, a] = bit; fn[b, a] = true;
                m[a, b] = bit; fn[a, b] = true;
            }
        }

        private static void ApplyMask(bool[,] m, bool[,] fn, int size, int mask)
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (fn[y, x]) continue;
                    bool inv;
                    switch (mask)
                    {
                        case 0: inv = (x + y) % 2 == 0; break;
                        case 1: inv = y % 2 == 0; break;
                        case 2: inv = x % 3 == 0; break;
                        case 3: inv = (x + y) % 3 == 0; break;
                        case 4: inv = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: inv = x * y % 2 + x * y % 3 == 0; break;
                        case 6: inv = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: inv = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (inv) m[y, x] = !m[y, x];
                }
        }

        private static int Penalty(bool[,] m, int size)
        {
            int score = 0, dark = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < size; a++)
                {
                    int run = 0;
                    bool prev = false;
                    int pattern = 0;
                    for (int b = 0; b < size; b++)
                    {
                        bool c = pass == 0 ? m[a, b] : m[b, a];
                        if (b > 0 && c == prev) run++;
                        else { if (run >= 5) score += run - 2; run = 1; prev = c; }
                        pattern = ((pattern << 1) | (c ? 1 : 0)) & 0x7FF;
                        if (b >= 10 && (pattern == 0x05D || pattern == 0x5D0)) score += 40;
                    }
                    if (run >= 5) score += run - 2;
                }
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (m[y, x]) dark++;
                    if (x < size - 1 && y < size - 1 && m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) score += 3;
                }
            int total = size * size;
            score += Math.Abs(dark * 20 - total * 10) / total * 10;
            return score;
        }

        private static byte[] RsDivisor(int degree)
        {
            var r = new byte[degree];
            r[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    r[j] = Mul(r[j], root);
                    if (j + 1 < degree) r[j] ^= r[j + 1];
                }
                root = Mul(root, 2);
            }
            return r;
        }

        private static byte[] RsRemainder(byte[] data, byte[] divisor)
        {
            var r = new byte[divisor.Length];
            foreach (var b in data)
            {
                int factor = b ^ r[0];
                Array.Copy(r, 1, r, 0, r.Length - 1);
                r[r.Length - 1] = 0;
                for (int i = 0; i < r.Length; i++) r[i] ^= Mul(divisor[i], factor);
            }
            return r;
        }

        private static byte Mul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }
    }
}
