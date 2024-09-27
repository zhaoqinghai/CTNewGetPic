using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System.Diagnostics.CodeAnalysis;

namespace CTNewGetPic
{
    internal class VirtualGrabImage : IGrabImage

    {
        private readonly ILogger<VirtualGrabImage> _logger;
        private readonly DalsaConfig _dalsaConfig;
        private readonly ImageTransportPump _pump;

        private static long _frameNo = 0;

        private static int _hasReset = 1;

        public VirtualGrabImage(ILogger<VirtualGrabImage> logger, [NotNull] DalsaConfig dalsaConfig, [NotNull] ImageTransportPump pump) => (_dalsaConfig, _logger, _pump) = (dalsaConfig, logger, pump);

        private CancellationTokenSource? _cts;
        private int _started = 0;

        public Task CloseAsync()
        {
            if (Interlocked.CompareExchange(ref _started, 0, 1) == 1)
            {
                _logger.LogInformation($"Virtual相机({_dalsaConfig.ServerName}-{_dalsaConfig.DeviceName})开始关闭采图");
                if (_cts != null && !_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                    _logger.LogInformation($"Virtual相机({_dalsaConfig.ServerName}-{_dalsaConfig.DeviceName})执行关闭采图");
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

                _logger.LogInformation($"Open Virtual相机({_dalsaConfig.ServerName}-{_dalsaConfig.DeviceName})");
                new Thread(() =>
                {
                    try
                    {
                        if (_cts != null && !_cts.IsCancellationRequested)
                        {
                            try
                            {
                                _cts.Dispose();
                            }
                            catch { }
                        }

                        _cts = new CancellationTokenSource();
                        while (!_cts.IsCancellationRequested)
                        {
                            using var src = Cv2.ImRead($@"C:\Users\cayav\Desktop\Image\{_dalsaConfig.Id}\{(_frameNo % 302) + 1}.jpg", ImreadModes.Grayscale);
                            var mat = new Mat(src, new Rect(_dalsaConfig.BeginX, 0, _dalsaConfig.EndX - _dalsaConfig.BeginX, src.Height));
                            if (MatCache.AddCache(mat, out var id))
                            {
                                _pump.Enqueue(new ImageInfo()
                                {
                                    FrameNo = _frameNo + 1,
                                    CameraId = _dalsaConfig.Id,
                                    CacheId = id,
                                    FrameHeight = mat.Height,
                                    FrameWidth = mat.Width
                                });
                            }
                            else
                            {
                                mat.Dispose();
                            }

                            if (!(Interlocked.CompareExchange(ref _hasReset, 1, 4) == 4))
                            {
                                Interlocked.Increment(ref _hasReset);

                                while (_hasReset != 1)
                                {
                                    Thread.Yield();
                                }
                            }
                            else
                            {
                                _frameNo++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("连接Virtual设备异常:{0}", ex);
                    }
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                }.Start();

                return Task.FromResult(true);
            }
            return Task.FromResult(true);
        }
    }
}