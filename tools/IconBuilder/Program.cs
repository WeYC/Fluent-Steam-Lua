// 图标生成器：从高分辨率 PNG 生成多尺寸 ICO 与关于页用的 256px PNG
//
// 用法：dotnet run -c Release --project tools/IconBuilder -- <源PNG> <输出目录>
// 例：  dotnet run -c Release --project tools/IconBuilder -- steam-icon-logo.png Assets
//
// 质量要点：
//  - 每个尺寸都从原始高分辨率图重采样，绝不从小图放大
//  - HighQualityBicubic + HighQuality 像素偏移，保持圆盘边缘平滑
//  - 自动探测图形实际包围盒，按等比缩放并居中：源图常带透明边距且非严格正方形，
//    直接拉伸会把圆盘压成椭圆
//  - ICO 使用 Vista+ 的 PNG 压缩条目，完整保留 32bpp alpha
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length < 2)
{
    Console.Error.WriteLine("用法: IconBuilder <源PNG> <输出目录>");
    return 1;
}

var srcPath = args[0];
var outDir = args[1];
if (!File.Exists(srcPath))
{
    Console.Error.WriteLine($"源文件不存在: {srcPath}");
    return 1;
}
Directory.CreateDirectory(outDir);

// 先读入字节再解码，避免 Image.FromFile 长期占用源文件句柄
Bitmap srcBmp;
using (var stream = new MemoryStream(File.ReadAllBytes(srcPath)))
using (var loaded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true))
{
    srcBmp = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(srcBmp);
    g.CompositingMode = CompositingMode.SourceCopy;
    g.InterpolationMode = InterpolationMode.NearestNeighbor;
    g.DrawImage(loaded, new Rectangle(0, 0, loaded.Width, loaded.Height),
        0, 0, loaded.Width, loaded.Height, GraphicsUnit.Pixel);
}

Console.WriteLine($"源: {srcBmp.Width}x{srcBmp.Height} {srcBmp.PixelFormat}");

var bounds = FindOpaqueBounds(srcBmp);
Console.WriteLine($"图形包围盒: {bounds.Width}x{bounds.Height} @ ({bounds.X},{bounds.Y})");

// 覆盖任务栏、资源管理器各视图与 Alt+Tab 的常用档位；
// 20/40 用于避免 125%/150% 缩放下的非整数插值
var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var bitmaps = new List<Bitmap>();

foreach (var size in sizes)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // 等比缩放到正方形画布内并居中
        double scale = size / (double)Math.Max(bounds.Width, bounds.Height);
        float dw = (float)(bounds.Width * scale);
        float dh = (float)(bounds.Height * scale);
        float dx = (size - dw) / 2f;
        float dy = (size - dh) / 2f;
        g.DrawImage(srcBmp, new RectangleF(dx, dy, dw, dh), bounds, GraphicsUnit.Pixel);
    }
    bitmaps.Add(bmp);
}

bitmaps[^1].Save(Path.Combine(outDir, "app.png"), ImageFormat.Png);

var icoPath = Path.Combine(outDir, "app.ico");
var blobs = new List<byte[]>();
foreach (var bmp in bitmaps)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    blobs.Add(ms.ToArray());
}

using (var fs = File.Create(icoPath))
using (var bw = new BinaryWriter(fs))
{
    bw.Write((ushort)0);              // reserved
    bw.Write((ushort)1);              // type: icon
    bw.Write((ushort)bitmaps.Count);

    int offset = 6 + 16 * bitmaps.Count;
    for (int i = 0; i < bitmaps.Count; i++)
    {
        int dim = bitmaps[i].Width >= 256 ? 0 : bitmaps[i].Width;
        bw.Write((byte)dim);          // width（0 表示 256）
        bw.Write((byte)dim);          // height
        bw.Write((byte)0);            // 调色板数
        bw.Write((byte)0);            // 保留
        bw.Write((ushort)1);          // 色彩平面
        bw.Write((ushort)32);         // 位深
        bw.Write((uint)blobs[i].Length);
        bw.Write((uint)offset);
        offset += blobs[i].Length;
    }
    foreach (var blob in blobs) bw.Write(blob);
}

foreach (var bmp in bitmaps) bmp.Dispose();
srcBmp.Dispose();

Console.WriteLine($"已生成: {icoPath} ({new FileInfo(icoPath).Length} 字节, {bitmaps.Count} 档尺寸)");
return 0;

// 扫描不透明像素最小外接矩形；用锁位图批量取字节，避免逐像素 GetPixel 的开销
static Rectangle FindOpaqueBounds(Bitmap bmp)
{
    int w = bmp.Width, h = bmp.Height;
    var full = new Rectangle(0, 0, w, h);
    var data = bmp.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        int stride = data.Stride;
        var buf = new byte[stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                if (buf[row + x * 4 + 3] >= 128)   // BGRA，取 A
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        return maxX < 0 ? full : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
    finally
    {
        bmp.UnlockBits(data);
    }
}
