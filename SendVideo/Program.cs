using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SendVideo;

// 添加退出标志（用户主动退出时不重启）
bool isUserInitiatedExit = false;

// 设置 UTF-8 编码（保留）
Console.OutputEncoding = Encoding.UTF8;

// 注册程序退出时重启逻辑（仅在非用户主动退出时执行）
AppDomain.CurrentDomain.ProcessExit += (s, e) =>
{
    if (!isUserInitiatedExit)
    {
        RestartProcess();
    }
};

// 拦截 Ctrl+C 信号（保留原有逻辑）
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true; // 阻止默认退出，如需允许 Ctrl+C 退出可移除
};

// 启动文件监控（保留）
var fileMonitor = new FileMonitor();
_ = Task.Run(() => fileMonitor.StartMonitoring()); // 使用异步防止阻塞按键监听

// 启动热键监听线程（核心新增部分）
var hotkeyHandler = new QuitKeyHandler();
_ = Task.Run(() => hotkeyHandler.StartListening(() =>
{
    isUserInitiatedExit = true; // 标记用户主动退出
    Environment.Exit(0); // 立即退出程序
}));

// 隐藏控制台窗口（保留）
HideConsoleWindow();

// 保持程序运行（通过任务调度维持）
Task.WaitAny(new[] { Task.Delay(-1) });

return;

static void RestartProcess()
{
    Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath,
        UseShellExecute = true
    });
}

[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();

[DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

static void HideConsoleWindow()
{
    var hWnd = GetConsoleWindow();
    if (hWnd != IntPtr.Zero)
    {
        ShowWindow(hWnd, 0); // SW_HIDE = 0
    }
}
