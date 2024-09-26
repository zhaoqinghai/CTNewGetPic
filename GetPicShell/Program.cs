using System.Diagnostics;

var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
do
{
    var id = Guid.NewGuid().ToString();
    using var mutex = new Mutex(true, id);
    using var process = Process.Start(new ProcessStartInfo()
    {
        Arguments = id,
        CreateNoWindow = true,
        UseShellExecute = true,
        FileName = "CTNewGetPic.exe",
        WindowStyle = ProcessWindowStyle.Hidden,
    });
    Console.WriteLine("已启动");
    if (process != null)
    {
        Console.WriteLine("等待结束");
        await process.WaitForExitAsync();
    }
    Console.WriteLine("已停止");
}
while (await timer.WaitForNextTickAsync());