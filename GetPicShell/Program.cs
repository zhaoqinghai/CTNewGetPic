using System.Diagnostics;

var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
do
{
    Parallel.ForEach(Process.GetProcessesByName("CTNewGetPic"), p =>
    {
        Console.WriteLine("当前存在采图进程未关闭");
        p?.WaitForExit();
    });

    var id = Guid.NewGuid().ToString();
    using var mutex = new Mutex(true, id);
    using var process = Process.Start(new ProcessStartInfo()
    {
        Arguments = id,
        CreateNoWindow = true,
        UseShellExecute = true,
        FileName = "CTNewGetPic.exe",
        WindowStyle = ProcessWindowStyle.Hidden
    });
    try
    {
        Console.WriteLine("已启动");
        if (process != null)
        {
            Console.WriteLine("等待结束");
            await process.WaitForExitAsync();
        }
        Console.WriteLine("已停止");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"运行异常:{ex.Message}");
        if (process != null && !process.HasExited)
        {
            process.Close();
        }
    }
}
while (await timer.WaitForNextTickAsync());