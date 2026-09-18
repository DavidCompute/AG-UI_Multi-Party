namespace AguiGroupChat.Agents;

/// <summary>
/// 图片尺寸探测：只读文件头取宽高，<b>不引图像库</b>。
///
/// <para>
/// 为什么不用 ImageSharp：平台工程（AguiGroupChat.Agents）不引它 —— 内置技能里的
/// ImageSharp 是<b>技能运行时</b>用 NuGet 还原的，与平台工程无关。
/// 而这里只需要宽高（给前端显示、给技能判断会不会被拉糊），读 20~30 字节的文件头就够，
/// 引一整个图像库只为这件事不划算。
/// </para>
///
/// <para>读不出来（如 WebP、损坏文件）返回 false，宽高为 0 —— 调用方按“未知”处理，不阻断上传。</para>
/// </summary>
public static class ImageSizeProbe
{
    /// <summary>从字节流读出宽高；不支持的格式返回 false。</summary>
    public static bool TryRead(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (bytes.Length < 16) return false;
        if (IsPng(bytes)) return TryPng(bytes, out width, out height);
        if (IsJpeg(bytes)) return TryJpeg(bytes, out width, out height);
        if (IsGif(bytes)) return TryGif(bytes, out width, out height);
        if (IsBmp(bytes)) return TryBmp(bytes, out width, out height);
        return false;
    }

    private static bool IsPng(byte[] b) => b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
    private static bool IsJpeg(byte[] b) => b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
    private static bool IsGif(byte[] b) => b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F';
    private static bool IsBmp(byte[] b) => b[0] == (byte)'B' && b[1] == (byte)'M';

    /// <summary>PNG：IHDR 固定在偏移 16，宽高各 4 字节大端。</summary>
    private static bool TryPng(byte[] b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length < 24) return false;
        w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
        h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
        return w > 0 && h > 0;
    }

    /// <summary>GIF：逻辑屏幕宽高在偏移 6，各 2 字节小端。</summary>
    private static bool TryGif(byte[] b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length < 10) return false;
        w = b[6] | (b[7] << 8);
        h = b[8] | (b[9] << 8);
        return w > 0 && h > 0;
    }

    /// <summary>BMP：BITMAPINFOHEADER 宽高在偏移 18，各 4 字节小端（高度可能为负 = 自上而下）。</summary>
    private static bool TryBmp(byte[] b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length < 26) return false;
        w = b[18] | (b[19] << 8) | (b[20] << 16) | (b[21] << 24);
        h = Math.Abs(b[22] | (b[23] << 8) | (b[24] << 16) | (b[25] << 24));
        return w > 0 && h > 0;
    }

    /// <summary>
    /// JPEG：逐段扫到 SOFn（帧起始）取宽高。
    /// 必须按段长跳（不能逐字节找 <c>FFC0</c>）：段内的熵编码数据里到处都是 0xFF，
    /// 逐字节扫会把随机数据当成帧头，读出天文数字般的宽高。
    /// </summary>
    private static bool TryJpeg(byte[] b, out int w, out int h)
    {
        w = h = 0;
        var i = 2; // 跳过 SOI
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            // 填充字节（0xFF 0xFF）与无长度字段的标记：继续往后走
            if (marker == 0xFF || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9)) { i += 2; continue; }
            var len = (b[i + 2] << 8) | b[i + 3];
            if (len < 2) return false;
            // SOF0..SOF3 / SOF5..SOF7 / SOF9..SOF11 / SOF13..SOF15（排除 DHT/JPG/DAC）
            var isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                if (i + 9 >= b.Length) return false;
                h = (b[i + 5] << 8) | b[i + 6];
                w = (b[i + 7] << 8) | b[i + 8];
                return w > 0 && h > 0;
            }
            // 到了扫描数据（SOS）就说明前面没有 SOF，放弃
            if (marker == 0xDA) return false;
            i += 2 + len;
        }
        return false;
    }
}
