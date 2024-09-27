using DALSA.SaperaLT.SapClassBasic;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System.Diagnostics.CodeAnalysis;

namespace CTNewGetPic
{
    public interface IGrabImage
    {
        public Task<bool> OpenAsync();

        public Task CloseAsync();
    }

    public class DalsaGrabImage : IGrabImage
    {
        private readonly ILogger<DalsaGrabImage> _logger;
        private readonly DalsaConfig _dalsaConfig;
        private readonly ImageTransportPump _pump;
        private long _frameNo = 0;
        private SapBuffer? _buffer;

        public DalsaGrabImage(ILogger<DalsaGrabImage> logger, [NotNull] DalsaConfig dalsaConfig, [NotNull] ImageTransportPump pump) => (_dalsaConfig, _logger, _pump) = (dalsaConfig, logger, pump);

        private CancellationTokenSource? _cts;
        private int _started = 0;

        public Task CloseAsync()
        {
            if (Interlocked.CompareExchange(ref _started, 0, 1) == 1)
            {
                _logger.LogInformation($"DALSA相机({_dalsaConfig.ServerName}-{_dalsaConfig.DeviceName})开始关闭采图");
                if (_cts != null && !_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                    _logger.LogInformation($"DALSA相机({_dalsaConfig.ServerName}-{_dalsaConfig.DeviceName})执行关闭采图");
                    _cts.Dispose();
                    _cts = null;
                }
            }
            return Task.CompletedTask;
        }

        public Task<bool> OpenAsync()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                if (_dalsaConfig == null)
                {
                    return Task.FromResult(false);
                }

                _frameNo = 0;
                _logger.LogInformation($"Open DALSA相机({_dalsaConfig.ServerName}-{_dalsaConfig.DeviceName})");
                var tcs = new TaskCompletionSource<bool>();
                if (_dalsaConfig.DeviceName != SapManager.GetResourceName(_dalsaConfig.ServerName, SapManager.ResourceType.AcqDevice, 0))
                {
                    _logger.LogInformation($"Open DALSA相机({_dalsaConfig.ServerName}-{_dalsaConfig.DeviceName})设备名和服务名不匹配");
                    return Task.FromResult(false);
                }
                new Thread(async () =>
                {
                    Action? finallyCallback = default;
                    try
                    {
                        using var location = new SapLocation(_dalsaConfig.ServerName, 0);
                        using var device = new SapAcqDevice(location, _dalsaConfig.ConfigFilePath);
                        using var buffer = new SapBuffer(2, device, SapBuffer.IsBufferTypeSupported(location, SapBuffer.MemoryType.ScatterGather) ? SapBuffer.MemoryType.ScatterGather : SapBuffer.MemoryType.ScatterGatherPhysical);
                        using var deviceToBuf = new SapAcqDeviceToBuf(device, buffer);
                        _logger.LogInformation($"idx:{_dalsaConfig.Id},deviceName:{_dalsaConfig.DeviceName},serverName:{_dalsaConfig.ServerName},configName:{_dalsaConfig.ConfigFilePath}");
                        finallyCallback = () => DestroyObjects(device, buffer, deviceToBuf);
                        deviceToBuf.XferNotify += new SapXferNotifyHandler(_deviceToBuf_XferNotify);
                        deviceToBuf.XferNotifyContext = this;
                        deviceToBuf.Pairs[0].EventType = SapXferPair.XferEventType.EndOfFrame;
                        if (!device.Create())
                        {
                            _logger.LogWarning($"创建相机设备({_dalsaConfig.ServerName}-{_dalsaConfig.DeviceName})失败");
                            tcs.SetResult(false);
                            return;
                        }
                        if (!buffer.Create())
                        {
                            _logger.LogWarning($"创建相机缓冲区({_dalsaConfig.ServerName}-{_dalsaConfig.DeviceName})失败");
                            tcs.SetResult(false);
                            return;
                        }
                        if (!deviceToBuf.Create())
                        {
                            _logger.LogWarning($"创建相机SapAcqDeviceToBuf({_dalsaConfig.ServerName}-{_dalsaConfig.DeviceName})失败");

                            tcs.SetResult(false);
                            return;
                        }
                        if (_cts != null && !_cts.IsCancellationRequested)
                        {
                            try
                            {
                                _cts.Dispose();
                            }
                            catch { }
                        }

                        deviceToBuf.Init(true);
                        _cts = new CancellationTokenSource();
                        if (deviceToBuf.Grab())
                        {
                            _buffer = buffer;
                            tcs.SetResult(true);

                            await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("连接DALSA设备异常:{0}", ex);
                        tcs.TrySetResult(false);
                    }
                    finally
                    {
                        finallyCallback?.Invoke();
                    }
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                }.Start();

                return tcs.Task;
            }
            return Task.FromResult(false);
        }

        private void DestroyObjects(SapAcqDevice device, SapBuffer buffer, SapAcqDeviceToBuf deviceToBuf)
        {
            _logger.LogError("DALSA相机({0}-{1})DestroyObjects Begin", _dalsaConfig.ServerName, _dalsaConfig.DeviceName);

            if (deviceToBuf != null && deviceToBuf.Initialized)
            {
                try
                {
                    deviceToBuf.XferNotify -= _deviceToBuf_XferNotify;
                    deviceToBuf.Destroy();
                }
                catch (Exception ex)
                {
                    _logger.LogError("DALSA相机({0}-{1})DestroyObjects SapAcqDeviceToBuf Excepiton:{2}", _dalsaConfig.ServerName, _dalsaConfig.DeviceName, ex);
                }
            }
            if (buffer != null && buffer.Initialized)
            {
                try
                {
                    buffer.Destroy();
                }
                catch (Exception ex)
                {
                    _logger.LogError("DALSA相机({0}-{1})DestroyObjects SapBuffer Excepiton:{2}", _dalsaConfig.ServerName, _dalsaConfig.DeviceName, ex);
                }
            }
            if (device != null && device.Initialized)
            {
                try
                {
                    device.Destroy();
                }
                catch (Exception ex)
                {
                    _logger.LogError("DALSA相机({0}-{1})DestroyObjects SapAcqDevice Excepiton:{2}", _dalsaConfig.ServerName, _dalsaConfig.DeviceName, ex);
                }
            }
        }

        private void _deviceToBuf_XferNotify(object sender, SapXferNotifyEventArgs e)
        {
            var frameNo = Interlocked.Increment(ref _frameNo);
            if (e.Trash)
            {
                _logger.LogWarning($"---C{0}-F{1} Trash 丢图---", _dalsaConfig.Id, frameNo);
                return;
            }
            if (_buffer != null)
            {
                _buffer.GetAddress(out var addr);
                _logger.LogInformation("C{0}-F{1}-W{2}-H{3} 接收采图数据", _dalsaConfig.Id, frameNo, _buffer.Width, _buffer.Height);
                if (MatCache.AddCache(addr, _buffer.Height, _buffer.Width, _buffer.BytesPerPixel, MatType.CV_8UC1, _dalsaConfig.BeginX, _dalsaConfig.EndX, out var id))
                {
                    _pump.Enqueue(new ImageInfo()
                    {
                        FrameNo = _frameNo,
                        CameraId = _dalsaConfig.Id,
                        CacheId = id,
                        FrameHeight = _buffer.Height
                    });
                }
            }
        }
    }

    public record struct ImageInfo
    {
        public long FrameNo { get; set; }

        public int CameraId { get; set; }

        public Guid CacheId { get; set; }

        public int FrameHeight { get; set; }
    }
}