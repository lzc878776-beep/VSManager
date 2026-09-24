using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace VSManager
{
    public sealed class ChatImage
    {
        public const int MaxCount = 4;
        public const int MaxBytes = 10 * 1024 * 1024;
        private readonly byte[] _png;
        public string Name { get; }
        public int Width { get; }
        public int Height { get; }

        private ChatImage(string name, byte[] png, int width, int height)
        {
            Name = name;
            _png = png;
            Width = width;
            Height = height;
        }

        public static ChatImage FromFile(string path)
        {
            if (new FileInfo(path).Length > MaxBytes)
                throw new InvalidDataException("每张图片不能超过 10 MB。");
            using (var stream = File.OpenRead(path))
            using (var image = Image.FromStream(stream, true, true))
                return FromImage(image, Path.GetFileName(path));
        }

        public static ChatImage FromImage(Image image, string name)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (image.Width > 8192 || image.Height > 8192 || (long)image.Width * image.Height > 32000000)
                throw new InvalidDataException("图片尺寸过大：单边不能超过 8192 像素，总像素不能超过 3200 万。");
            using (var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var stream = new MemoryStream())
            {
                graphics.DrawImage(image, 0, 0, bitmap.Width, bitmap.Height);
                bitmap.Save(stream, ImageFormat.Png);
                if (stream.Length > MaxBytes) throw new InvalidDataException("转换后的图片超过 10 MB，请缩小后添加。");
                return new ChatImage(string.IsNullOrWhiteSpace(name) ? "clipboard.png" : name,
                    stream.ToArray(), bitmap.Width, bitmap.Height);
            }
        }

        /// <summary>PNG 数据的副本（用于保存为附件）。/ Copy of the PNG data (used to save it as an attachment).</summary>
        public byte[] PngBytes() => (byte[])_png.Clone();

        public Bitmap OpenBitmap()
        {
            using (var stream = new MemoryStream(_png, false))
            using (var image = Image.FromStream(stream))
                return new Bitmap(image);
        }

        public Bitmap CreateThumbnail(int maxSize)
        {
            if (maxSize < 1) throw new ArgumentOutOfRangeException(nameof(maxSize));
            double scale = Math.Min(1, Math.Min((double)maxSize / Width, (double)maxSize / Height));
            using (var stream = new MemoryStream(_png, false))
            using (var image = Image.FromStream(stream))
                return new Bitmap(image, Math.Max(1, (int)(Width * scale)), Math.Max(1, (int)(Height * scale)));
        }
    }
}
