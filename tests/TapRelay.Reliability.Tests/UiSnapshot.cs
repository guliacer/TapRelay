using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using TapRelay;

/// <summary>
/// 无头 UI 快照：把 MainForm 在指定尺寸下渲染成 PNG，并打印关键布局数值，用来核对页面。
///
/// 存在的理由：这个沙箱里既不能用 PowerShell 的 Add-Type 编译 P/Invoke，
/// 也没有可供截图的交互桌面（从后台任务启动的 TapRelay 窗口不在当前窗口站里，
/// PrintWindow 只能拿到一张全白图）。而进程内的 PrintWindow 不依赖窗口站，
/// 是这里唯一可靠的视觉核对手段。
///
/// 用法：TapRelay.Reliability.Tests.exe --ui-snapshot &lt;输出目录&gt; [用例名]
/// 用例：default-collapsed / minimum-collapsed / default-expanded。
/// 一次只渲染一个用例：MainForm 的构造与 OnLoad 很重（自动检测应用、起监视器），
/// 而且最小宽度那一档会与 MinimumSize 协商来回调整尺寸，连跑多档会卡住。
/// 只做渲染、不跑测试；正常测试运行不受影响。
/// 注意：跑之前必须先退出正在运行的 TapRelay —— MainForm.OnLoad 会尝试监听 1122，
/// 与真实实例并存会互相干扰（这也正是 Program.cs 里单实例接管要解决的问题）。
/// </summary>
internal static class UiSnapshot
{
    private static readonly (string Name, int Width, int Height, bool LogExpanded)[] Cases =
    {
        ("default-collapsed", 880, 780, false),
        ("minimum-collapsed", 784, 741, false),
        ("default-expanded", 880, 780, true),
    };

    internal static int Run(string outputDirectory, string? caseName)
    {
        Directory.CreateDirectory(outputDirectory);

        var selected = string.IsNullOrWhiteSpace(caseName)
            ? Cases[0]
            : Array.Find(Cases, c => c.Name == caseName);
        if (selected.Name == null)
        {
            Console.WriteLine(
                "UI_SNAPSHOT_FAILED: unknown case '" + caseName + "'; known: " +
                string.Join(", ", Array.ConvertAll(Cases, c => c.Name)));
            Environment.Exit(1);
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Render(outputDirectory, selected);
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        // WinForms 的控件树必须在 STA 线程上创建。
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null)
        {
            Console.WriteLine("UI_SNAPSHOT_FAILED: " + failure);
            // 必须强制退出：MainForm 会起监视器/托盘图标等前台资源，
            // 正常 return 之后进程不会自己结束，命令行会一直挂着。
            Environment.Exit(1);
        }

        Console.WriteLine("UI_SNAPSHOT_OK: " + outputDirectory);
        Environment.Exit(0);
        return 0;
    }

    private static void Render(
        string outputDirectory,
        (string Name, int Width, int Height, bool LogExpanded) selected)
    {
        var (name, width, height, logExpanded) = selected;

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // MainForm.OnLoad 是 async void：里面任何未捕获异常都会直接干掉进程。
        // 快照只关心画面，运行期的后台异常一律吞掉。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, __) => { };
        AppDomain.CurrentDomain.UnhandledException += (_, __) => { };

        Console.WriteLine("case " + name + " client=" + width + "x" + height);
        Console.Out.Flush();

        // 用独立的信号名：绝不能碰到真实实例在等的 TapRelay.ExitSignal。
        using var signal = new EventWaitHandle(
            false, EventResetMode.AutoReset, "TapRelay.UiSnapshot.ExitSignal");
        using var form = new MainForm(signal);

        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(0, 0);
        form.ClientSize = new Size(width, height);
        if (logExpanded)
        {
            // 走真实入口而不是直接改字段：展开态的高度与滚动条都由此路径设置，
            // 直接改 _logExpanded 拍到的会是一个布局没跟上展开态的半成品。
            Invoke(form, "ToggleLogExpanded");
        }

        form.Show();
        // 布局里有多处 BeginInvoke / Idle 回调，必须把消息泵跑几轮再拍。
        Settle();

        var path = Path.Combine(outputDirectory, name + ".png");
        CaptureToPng(form, path, out var note);
        Console.WriteLine("  capture=" + note + " -> " + path);
        try
        {
            DumpLayout(form);
        }
        catch (Exception error)
        {
            // 诊断输出只是辅助，绝不能因为它失败而丢掉已经拍好的图。
            Console.WriteLine("  dump failed: " + error.Message);
        }

        Console.Out.Flush();
    }

    /// <summary>把决定页面纵向分配的数值全部打印出来，避免靠肉眼估像素。</summary>
    private static void DumpLayout(MainForm form)
    {
        Console.WriteLine("  client=" + form.ClientSize.Width + "x" + form.ClientSize.Height);
        Console.WriteLine("  pageBottom=" + (form.ClientSize.Height - 18));
        foreach (var field in new[]
                 {
                     "_stripCard", "_serviceCard", "_notificationCard", "_logCard",
                     "_notificationAppsPanel", "_logLabel", "_logBox",
                     "_sendButton", "_messageBox", "_addressBox",
                     "_iconQr", "_iconTheme", "_iconMore"
                 })
        {
            var control = GetField<Control>(form, field);
            if (control == null)
            {
                Console.WriteLine("  " + field + " = <null>");
                continue;
            }

            Console.WriteLine(
                "  " + field + " top=" + control.Top + " bottom=" + control.Bottom +
                " h=" + control.Height + " left=" + control.Left + " right=" + control.Right +
                " w=" + control.Width + " visible=" + control.Visible);
        }

        // _hintLabel / _installedGroup 等是面板自己的私有字段，不在 MainForm 上。
        var panel = GetField<Control>(form, "_notificationAppsPanel")!;
        foreach (var field in new[] { "_hintLabel", "_installedGroup", "_uninstalledGroup" })
        {
            var control = GetPrivate<Control>(panel, field);
            Console.WriteLine(
                control == null
                    ? "  panel." + field + " = <null>"
                    : "  panel." + field + " top=" + control.Top + " bottom=" + control.Bottom +
                      " h=" + control.Height + " left=" + control.Left + " w=" + control.Width);
        }

        Console.WriteLine(
            "  panel FillHeight=" + GetPrivateProperty(panel, "FillHeight") +
            " Max=" + GetPrivateProperty(panel, "MaxContentHeight") +
            " Min=" + GetPrivateProperty(panel, "MinContentHeight"));
        Console.WriteLine("  _logExpanded=" + GetField<object>(form, "_logExpanded"));
    }

    private static object? GetPrivateProperty(object target, string name)
    {
        return target.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(target);
    }

    private static T? GetPrivate<T>(object target, string name) where T : class
    {
        return target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target) as T;
    }

    private static T? GetField<T>(MainForm form, string name) where T : class
    {
        var value = typeof(MainForm)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(form);
        return value as T;
    }

    private static void Invoke(MainForm form, string method)
    {
        typeof(MainForm)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, null);
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    /// <summary>PW_RENDERFULLCONTENT：让 DWM 合成的窗口也能被完整渲染出来。</summary>
    private const uint RenderFullContent = 0x00000002;

    /// <summary>
    /// 把整个窗口（含所有子控件）渲染到 PNG。
    /// <para>
    /// 不能用 <c>Control.DrawToBitmap</c>：它靠 WM_PRINT 逐层下发，只对直接子控件有效，
    /// 而本窗口是「窗体 -> 卡片面板 -> 标签/输入框」三层结构，实测只能画出卡片底色，
    /// 里面的标签、输入框、图标按钮全是空白。PrintWindow 走系统合成路径，一次拿到完整画面。
    /// </para>
    /// </summary>
    private static void CaptureToPng(Form form, string path, out string note)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            var ok = PrintWindow(form.Handle, hdc, RenderFullContent);
            graphics.ReleaseHdc(hdc);
            note = ok ? "printwindow" : "printwindow-failed";
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    private static void Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            Application.DoEvents();
            Thread.Sleep(30);
        }
    }
}
