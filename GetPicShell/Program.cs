using System.Diagnostics;

var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
do
{
    Parallel.ForEach(Process.GetProcessesByName("CTNewGetPic"), p =>
    {
        try
        {
            p?.WaitForExit();
            Thread.Sleep(10000);
        }
        catch { }
    });

    var id = Guid.NewGuid().ToString();
    var mutex = new Mutex(true, id);
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
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}--已启动");
        if (process != null)
        {
            Console.WriteLine("等待结束");
            await process.WaitForExitAsync();
        }
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}--已停止");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"运行异常:{ex.Message}");
        if (process != null && !process.HasExited)
        {
            process.Close();
        }
    }
    finally
    {
        mutex.Dispose();
    }

    await Task.Delay(TimeSpan.FromSeconds(30));
}
while (await timer.WaitForNextTickAsync());