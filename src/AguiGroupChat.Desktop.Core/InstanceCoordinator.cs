using System.Security.Cryptography;

namespace AguiGroupChat.Desktop;

/// <summary>
/// 桌面版多实例协调器：共享同一个后端进程（固定 5200），每个 UI 实例一份引用计数；
/// 第一个实例启动 <c>--backend</c> 子进程，最后一个实例关闭时通过
/// <c>POST /ag-ui/shutdown?secret=…</c> 让后端优雅停机（计数文件 / secret 存 %LocalAppData%\AguiGroupChat）。
/// </summary>
public sealed class InstanceCoordinator
{
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AguiGroupChat");
    private static readonly string CountFile = Path.Combine(BaseDir, "instance-count");
    private static readonly string LockFile = Path.Combine(BaseDir, "instance.lock");
    private static readonly string SecretFile = Path.Combine(BaseDir, "backend-secret");

    /// <summary>探测后端是否已在运行（GET / 返回成功即就绪）。</summary>
    public bool Probe(string baseUrl, int timeoutMs = 1500)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using var resp = http.GetAsync(baseUrl + "/").GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启动后端子进程（当前可执行文件的 --backend 模式，无窗口）。</summary>
    public void StartBackend()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "AguiGroupChat.Desktop.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--backend");
        System.Diagnostics.Process.Start(psi);
    }

    /// <summary>轮询等待后端就绪（启动包含建库 / 恢复状态 / 可能的模型下载，默认 60s 超时）。</summary>
    public bool WaitReady(string baseUrl, int timeoutSec = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            if (Probe(baseUrl, 1000)) return true;
            Thread.Sleep(300);
        }
        return false;
    }

    /// <summary>
    /// 实例计数 +1（文件独占锁保证跨进程原子）。
    /// <paramref name="resetIfStale"/>：本实例刚启动了后端（探测失败过）→ 计数从 1 重算，
    /// 避免上次异常退出残留的计数导致后端永不关闭。
    /// </summary>
    public void AddInstance(bool resetIfStale = false)
    {
        Directory.CreateDirectory(BaseDir);
        using var lk = File.Open(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (resetIfStale)
        {
            File.WriteAllText(CountFile, "1");
        }
        else
        {
            var count = ReadCount();
            File.WriteAllText(CountFile, (count + 1).ToString());
        }
    }

    /// <summary>实例计数 -1；归零时删除计数文件并通知后端优雅停机。返回是否关闭了后端。</summary>
    public bool RemoveInstanceAndShutdownIfLast(string baseUrl)
    {
        var last = false;
        try
        {
            using var lk = File.Open(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var count = ReadCount() - 1;
            if (count <= 0)
            {
                if (File.Exists(CountFile)) File.Delete(CountFile);
                last = true;
            }
            else
            {
                File.WriteAllText(CountFile, count.ToString());
            }
        }
        catch
        {
            last = true; // 计数文件不可用（异常残留）：直接关后端，避免留下孤儿进程
        }
        if (last) ShutdownBackend(baseUrl);
        return last;
    }

    /// <summary>通知后端优雅停机（携带共享 secret；后端已退出时静默失败）。</summary>
    public void ShutdownBackend(string baseUrl)
    {
        try
        {
            var secret = File.Exists(SecretFile) ? File.ReadAllText(SecretFile).Trim() : "";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = baseUrl + "/ag-ui/shutdown?secret=" + Uri.EscapeDataString(secret);
            _ = http.PostAsync(url, content: null).GetAwaiter().GetResult();
        }
        catch
        {
            // 后端已不在：忽略
        }
    }

    /// <summary>读取后端停机 secret；不存在则生成并落盘（后端进程启动时调用）。</summary>
    public static string ReadOrCreateBackendSecret()
    {
        Directory.CreateDirectory(BaseDir);
        if (File.Exists(SecretFile)) return File.ReadAllText(SecretFile).Trim();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(SecretFile, secret);
        return secret;
    }

    private static int ReadCount()
    {
        if (!File.Exists(CountFile)) return 0;
        return int.TryParse(File.ReadAllText(CountFile).Trim(), out var c) ? c : 0;
    }
}
