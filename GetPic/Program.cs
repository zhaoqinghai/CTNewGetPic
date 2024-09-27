using CTNewGetPic;
using Microsoft.Extensions.DependencyInjection;
using NLog;
using NLog.Extensions.Logging;
using R3;
using System.Text.Json;
using System.Threading.Channels;

LogManager.GetCurrentClassLogger().Info("**********************app start {0}**********************", args);

try
{
    if (args.Length != 1)
    {
        return;
    }
    using var cts = new CancellationTokenSource();
    using var mutex = new Mutex(false, args[0], out var isNotRunning);
    if (isNotRunning)
    {
        return;
    }
    new Thread(() =>
    {
        mutex.WaitOne();
        cts.Cancel();
    }).Start();

    var svcs = new ServiceCollection();
    svcs.AddLogging(loggingBuilder =>
    {
        loggingBuilder.AddNLog();
    });
    svcs.AddSingleton<MqttService>();
    svcs.AddSingleton<ImageTransportPump>();
    svcs.AddSingleton<IRunServerCmd, DefaultRunServerCmd>();
    svcs.AddSingleton<IRunServer, DefaultRunServer>();
    svcs.AddSingleton<IRunServer, CleanDirServer>();
    svcs.AddSingleton<IRunServer, MqttServer>();
    svcs.AddSingleton<MergeImage>();
    svcs.AddSingleton<IOptions<LocalSettings>, DefaultSettings>(_ =>
    {
        using var fs = File.OpenRead("appsettings.json");
        return new DefaultSettings()
        {
            Value = JsonSerializer.Deserialize(fs, SourceGenerationContext.Default.LocalSettings)!
        };
    });
    svcs.AddKeyedSingleton(MqttServer.MQTT_CHANNEL, (_, _) => Channel.CreateBounded<string>(new BoundedChannelOptions(50)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    }));

    DefaultContainer.IOC = svcs.BuildServiceProvider();

    foreach (var server in DefaultContainer.IOC.GetServices<IRunServer>())
    {
        server.Start();
    }

    using var disposable = DefaultContainer.IOC.GetRequiredService<IRunServerCmd>().ServerCmd.Subscribe(x =>
    {
        if (x == ServerCmd.Restart)
        {
            cts.Cancel();
        }
    });
    await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
    foreach (var server in DefaultContainer.IOC.GetServices<IRunServer>())
    {
        server.Stop();
    }
}
catch (Exception ex)
{
    LogManager.GetCurrentClassLogger().Error("异常崩溃:{0}", ex);
    Environment.Exit(-1);
}

public static class DefaultContainer
{
    public static IServiceProvider IOC { get; set; } = null!;
}