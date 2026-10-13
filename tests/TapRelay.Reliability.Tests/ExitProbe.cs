using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using TapRelay;

/// <summary>
/// 托盘退出路径探针：在进程内构造 MainForm，然后调用真实的
/// <c>MainForm.ExitFromTrayAsync</c>（托盘菜单「退出」的处理函数），
/// 用进程是否自行结束、以及结束得多快来验收这条路径。
///
/// 存在的理由：三条退出路径里，只有这条无法用外部手段触发 ——
/// 托盘菜单需要鼠标点击，而本沙箱没有可交互桌面。
/// 它又恰好是最特殊的一条：先等后台停止任务，再 <c>Close()</c>，
/// 而 <c>Close()</c> -> <c>OnFormClosing</c> 会**再调一次** <c>StopAsync</c>。
/// 这个"二次停止"一旦没被上限覆盖，总耗时会翻倍并撞上看门狗，
/// 所以必须有一条能真正跑它的回归入口。
///
/// 用法：TapRelay.Reliability.Tests.exe --exit-probe
/// 退出码：0 = 自行退出（正常）；4 = 到点仍未退出（说明退出链路卡住）。
///
/// 注意：
/// - 跑之前必须先退出正在运行的 TapRelay，否则 OnLoad 会和真实实例抢 1122。
/// - 这里必须自己泵消息：<c>ExitFromTrayAsync</c> 是 async，它的续体要靠
///   WindowsFormsSynchronizationContext 投递回本线程，不泵就永远不执行，
///   会把"探针没泵消息"误判成"退出路径坏了"。
/// </summary>
internal static class ExitProbe
{
    private const int ProbeDeadlineMilliseconds = 12000;

    internal static int Run()
    {
        var thread = new Thread(RunOnSta);
        // WinForms 的控件树必须在 STA 线程上创建。
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        // 正常路径下 ExitFromTrayAsync 已经 Environment.Exit(0) 了，走不到这里。
        Console.WriteLine("EXIT_PROBE_TIMEOUT: 退出链路在 " + ProbeDeadlineMilliseconds + "ms 内没有结束进程");
        Console.Out.Flush();
        Environment.Exit(4);
        return 4;
    }

    private static void RunOnSta()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // MainForm.OnLoad 是 async void，未捕获异常会直接干掉进程；探针只关心退出链路。
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, __) => { };
        AppDomain.CurrentDomain.UnhandledException += (_, __) => { };

        // 独立的信号名：绝不能碰到真实实例在等的 TapRelay.ExitSignal。
        using var signal = new EventWaitHandle(
            false, EventResetMode.AutoReset, "TapRelay.ExitProbe.ExitSignal");
        var form = new MainForm(signal);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(0, 0);

        form.Show();
        Pump(1500);
        Console.WriteLine("EXIT_PROBE_READY: form shown, invoking ExitFromTrayAsync");
        Console.Out.Flush();

        var exitFromTray = typeof(MainForm).GetMethod(
            "ExitFromTrayAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (exitFromTray == null)
        {
            Console.WriteLine("EXIT_PROBE_FAILED: ExitFromTrayAsync not found");
            Console.Out.Flush();
            Environment.Exit(2);
            return;
        }

        // 不 await：正常路径下它自己会 Environment.Exit(0)，这里只负责把消息泵转起来。
        exitFromTray.Invoke(form, null);

        var deadline = Environment.TickCount64 + ProbeDeadlineMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Pump(50);
        }
    }

    private static void Pump(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }
}
