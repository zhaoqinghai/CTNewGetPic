using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using MQTTnet.Server;
using R3;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CTNewGetPic
{
    public enum ServerCmd
    {
        None = 0,
        Restart
    }

    public interface IRunServerCmd
    {
        ReactiveProperty<ServerCmd> ServerCmd { get; }
    }

    public class DefaultRunServerCmd : IRunServerCmd
    {
        public ReactiveProperty<ServerCmd> ServerCmd { get; } = new ReactiveProperty<ServerCmd>();
    }

    public interface IRunServer
    {
        public void Start();

        public void Stop();
    }

    public class DefaultRunServer : IRunServer
    {
        private const int StopTimesCount = 10;

        private readonly LocalSettings _settings;

        private readonly ILogger<DefaultRunServer> _logger;
        private readonly MergeImage _mergeImage;
        private readonly List<IGrabImage> _grabImages = new List<IGrabImage>();
        private readonly ImageTransportPump _pump;
        private int _started = 0;

        private readonly PeriodicTimer _timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        private object _lock = new object();

        public DefaultRunServer(IOptions<LocalSettings> settings, ImageTransportPump pump, ILogger<DefaultRunServer> logger, MergeImage mergeImage)
        {
            _settings = settings.Value;
            _pump = pump;
            _logger = logger;
            _mergeImage = mergeImage;
        }

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                lock (_lock)
                {
                    _mergeImage.Start();
                    _pump.Start();
                    _grabImages.AddRange(_settings.CamConfigs.Select(x => new DalsaGrabImage(DefaultContainer.IOC.GetRequiredService<ILogger<DalsaGrabImage>>(), x, DefaultContainer.IOC.GetRequiredService<ImageTransportPump>())));
                    Parallel.ForEach(_grabImages, DalsaGrabImage =>
                    {
                        if (!DalsaGrabImage.OpenAsync().GetAwaiter().GetResult())
                        {
                            throw new Exception("采图服务启动失败");
                        }
                    });
                    _logger.LogInformation("采图启动成功");
                }
            }
        }

        public void Stop()
        {
            if (Interlocked.CompareExchange(ref _started, 0, 1) == 1)
            {
                lock (_lock)
                {
                    foreach (var DalsaGrabImage in _grabImages)
                    {
                        DalsaGrabImage.CloseAsync().Wait();
                    }
                    _grabImages.Clear();

                    _pump.Stop();
                    _mergeImage.Stop();
                }
            }
        }
    }

    public class CleanDirServer : IRunServer
    {
        private readonly int _remainDays;
        private readonly string _saveDir;
        private int _started = 0;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public CleanDirServer(IOptions<LocalSettings> settings)
        {
            _remainDays = settings.Value!.RemainDays;
            _saveDir = settings.Value!.SaveImgDir;
        }

        public void Start()
        {
            new Thread(async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                do
                {
                    if (Directory.Exists(_saveDir))
                    {
                        foreach (var dir in Directory.GetDirectories(_saveDir).Select(Path.GetFileName))
                        {
                            if (DateTime.TryParse(dir, out var d))
                            {
                                if ((DateTime.Today - d.Date).TotalDays >= _remainDays)
                                {
                                    Directory.Delete(Path.Combine(_saveDir, dir), true);
                                }
                            }
                        }
                    }
                }
                while (await timer.WaitForNextTickAsync(_cts.Token));
            }).Start();
        }

        public void Stop()
        {
            _cts.Cancel();
        }
    }

    public class MqttServer : IRunServer
    {
        public const string MQTT_CHANNEL = "MqttChannel";
        private readonly MqttServerSettings _settings;
        private readonly Channel<string> _channel;
        private readonly MqttService _mqttService;
        private readonly ILogger<MqttServer> _logger;
        private int _started = 0;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly MqttFactory _mqttFactory = new MqttFactory();

        public MqttServer(IOptions<LocalSettings> settings, ILogger<MqttServer> logger, MqttService service, [FromKeyedServices(MQTT_CHANNEL)] Channel<string> channel)
        {
            _settings = settings.Value!.MqttServerSettings;
            _channel = channel;
            _logger = logger;
            _mqttService = service;
        }

        public void Start()
        {
            new Thread(async () =>
            {
                await foreach (var msg in _channel.Reader.ReadAllAsync(_cts.Token))
                {
                    try
                    {
                        await _mqttService.Publish(_settings.Topic, msg, _cts.Token);
                        _logger.LogInformation("Published mqtt server success msg: {0}", msg);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "mqttservice 发布消息异常");
                    }
                }
            }).Start();
        }

        public void Stop()
        {
            _cts.Cancel();
        }
    }

    public class MqttService : IDisposable
    {
        private IMqttClient? _mqttClient;
        private readonly MqttServerSettings _settings;
        private readonly ILogger<MqttService> _logger;

        public MqttService(IOptions<LocalSettings> settings, ILogger<MqttService> logger)
        {
            _logger = logger;
            _settings = settings.Value!.MqttServerSettings;
        }

        private async Task<bool> InitClient()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (_mqttClient == null)
            {
                _mqttClient = new MqttFactory().CreateMqttClient();
                var mqttClientOptions = new MqttClientOptionsBuilder()
                  .WithTcpServer(_settings.ServerIp, _settings.ServerPort)
                  .Build();
                await _mqttClient.ConnectAsync(mqttClientOptions, cts.Token);
                return true;
            }

            if (_mqttClient != null && !_mqttClient.IsConnected)
            {
                try
                {
                    var ret = await _mqttClient.ConnectAsync(_mqttClient.Options, cts.Token);
                    if (ret != null && ret.ResultCode == MqttClientConnectResultCode.Success)
                    {
                        return true;
                    }
                    await _mqttClient.DisconnectAsync();
                    _mqttClient.Dispose();
                    _mqttClient = null;
                    return false;
                }
                catch (Exception ex)
                {
                    _mqttClient = null;
                    _logger.LogError("创建mqtt连接失败:{0}", ex);
                }
                return false;
            }
            return true;
        }

        public async Task Publish(string topic, string msg, CancellationToken token)
        {
            try
            {
                if (await InitClient())
                {
                    var applicationMessage = new MqttApplicationMessageBuilder()
                       .WithTopic(topic)
                       .WithPayload(msg)
                       .Build();
                    await _mqttClient!.PublishAsync(applicationMessage, token);
                }
                else
                {
                    _logger.LogInformation("topic:{0},msg:{1}", topic, msg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "mqttservice 发布消息异常");
                _logger.LogInformation("topic:{0},msg:{1}", topic, msg);
            }
        }

        public void Dispose()
        {
            _mqttClient?.DisconnectAsync();
            _mqttClient?.Dispose();
        }
    }

    public interface IOptions<T> where T : class
    {
        T Value { get; }
    }

    public class DefaultSettings : IOptions<LocalSettings>
    {
        public LocalSettings Value { get; init; } = null!;
    }
}