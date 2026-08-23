using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// AG-UI 群聊图标生成器：把矢量设计栅格化输出为多尺寸 PNG 与 Windows ICO。
// 图形：圆角渐变底板 + 外圈六个节点通过连线汇聚到中心"协作中枢"（圆桌讨论隐喻）。
// 用法: dotnet run --project tools/icon-gen -- <输出目录>
// 不传 <输出目录> 时默认输出到仓库根的 assets/。
var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "assets"));

Directory.CreateDirectory(outDir);

// 品牌主色（与 Web 默认 #4f8cff 一致）
var top = ColorFrom("#6fa8ff");
var mid = ColorFrom("#4f8cff");
var bottom = ColorFrom("#2b5fd9");

// ---------------- 主图标（64 网格，可缩放） ----------------
var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
foreach (var size in sizes)
{
    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawIcon(g, size, top, mid, bottom, padRatio: 2f / 64f);
    }
    var pngPath = Path.Combine(outDir, size == 256 ? "agui-icon-256.png" : $"agui-icon-{size}.png");
    bmp.Save(pngPath, ImageFormat.Png);
    Console.WriteLine($"已生成 {Path.GetFileName(pngPath)} ({size}x{size})");
}

// ---------------- Web favicon（32 与 16） ----------------
using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
{
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    DrawIcon(g, 32, top, mid, bottom, 3f / 32f);
    bmp.Save(Path.Combine(outDir, "favicon-32.png"), ImageFormat.Png);
}
using (var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
{
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    DrawIcon(g, 16, top, mid, bottom, 4f / 32f);
    bmp.Save(Path.Combine(outDir, "favicon-16.png"), ImageFormat.Png);
}

// ---------------- Windows ICO（多尺寸内嵌 PNG，max 256） ----------------
BuildIco(Path.Combine(outDir, "agui-icon.ico"), top, mid, bottom);

Console.WriteLine("完成。");

// ---------------- 绘制 ----------------
static void DrawIcon(Graphics g, int size, Color top, Color mid, Color bottom, float padRatio)
{
    var pad = size * padRatio;                 // 底板内边距（居中留白）
    var rect = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
    var corners = (int)(size * 0.24f);

    using var path = RoundedRect(rect, corners);
    using (var bg = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.ForwardDiagonal))
        g.FillPath(bg, path);

    // 连线控制点（按 64 网格的比例缩放），中心在 (0.5*L, 0.5*L + 2)
    var L = size;
    var s = L / 64f;                          // 64 网格 → 像素缩放
    (float X, float Y)[] dots =
    {
        (16*s, 22*s), (13*s, 38*s), (24*s, 50*s),
        (40*s, 50*s), (51*s, 38*s), (48*s, 22*s),
    };
    var cx = 32 * s;
    var cy = 33 * s;

    using var pen = new Pen(Color.White, Math.Max(1f, 2.4f * s));
    var gs = g.Save();
    g.SmoothingMode = SmoothingMode.AntiAlias;
    pen.StartCap = LineCap.Round;
    pen.EndCap = LineCap.Round;
    pen.LineJoin = LineJoin.Round;
    var lines = new (float X0, float Y0, float X1, float Y1)[]
    {
        (16,22,30,30),(13,38,29,35),(24,50,31,39),
        (40,50,34,39),(51,38,36,35),(48,22,34,30),
    };
    foreach (var ln in lines)
        g.DrawLine(pen, ln.X0*s, ln.Y0*s, ln.X1*s, ln.Y1*s);

    foreach (var d in dots)
        FillCircle(g, d.X, d.Y, 4 * s);

    using var ringPen = new Pen(Color.White, Math.Max(1f, 2.4f * s));
    DrawRing(g, cx, cy, 11 * s, ringPen);
    using (var core = new SolidBrush(Color.White))
        FillCircle(g, cx, cy, 4 * s);
    g.Restore(gs);
}

static void FillCircle(Graphics g, float x, float y, float r)
{
    g.FillEllipse(Brushes.White, x - r, y - r, 2 * r, 2 * r);
}

static void DrawRing(Graphics g, float cx, float cy, float r, Pen pen)
{
    g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
}

static GraphicsPath RoundedRect(RectangleF rect, float radius)
{
    var path = new GraphicsPath();
    float d = radius * 2;
    if (d > rect.Width) d = rect.Width;
    if (d > rect.Height) d = rect.Height;
    path.StartFigure();
    path.AddArc(rect.X, rect.Y, d, d, 180, 90);
    path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
    path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
    path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

static void BuildIco(string icoPath, Color top, Color mid, Color bottom)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    var icoSizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
    writer.Write((short)0);
    writer.Write((short)1);
    writer.Write((short)icoSizes.Length);

    var entries = new List<byte[]>();
    foreach (var size in icoSizes)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawIcon(g, size, top, mid, bottom, size == 16 ? 4f / 32f : 2f / 64f);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        entries.Add(ms.ToArray());
    }

    var offset = 6 + 16 * icoSizes.Length;
    for (var i = 0; i < icoSizes.Length; i++)
    {
        var size = icoSizes[i];
        var data = entries[i];
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32); // 32bpp
        writer.Write((uint)data.Length);
        writer.Write((uint)offset);
        offset += data.Length;
    }
    foreach (var data in entries)
        writer.Write(data);

    File.WriteAllBytes(icoPath, stream.ToArray());
    Console.WriteLine($"已生成 {Path.GetFileName(icoPath)}（{string.Join(" / ", icoSizes)} px）");
}

static Color ColorFrom(string hex)
{
    var v = int.Parse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber);
    return Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
}
