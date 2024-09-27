using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace CTNewGetPic
{
    internal class VirtualGrabImage : IGrabImage

    {
        private readonly ILogger<VirtualGrabImage> _logger;
        private readonly DalsaConfig _dalsaConfig;
        private readonly ImageTransportPump _pump;

        private static long _frameNo = 0;

        private static ConcurrentQueue<(Action, TaskCompletionSource)> _tasks = new ConcurrentQueue<(Action, TaskCompletionSource)>();

        public VirtualGrabImage(ILogger<VirtualGrabImage> logger, [NotNull] DalsaConfig dalsaConfig, [NotNull] ImageTransportPump pump) => (_dalsaConfig, _logger, _pump) = (dalsaConfig, logger, pump);

        private CancellationTokenSource? _cts;
        private int _started = 0;

        static VirtualGrabImage()
        {
            new Thread(async () =>
            {
                while (true)
                {
                    while (_tasks.Count != 4)
                    {
                        Thread.Yield();
                    }

                    await Task.WhenAll(_tasks.Select(_ => Task.Run(() =>
                    {
                        _.Item1.Invoke();
                        return _.Item2;
                    }))).ContinueWith(t =>
                    {
                        _tasks.Clear();
                        _frameNo++;
                        Parallel.ForEach(t.Result, t => t.TrySetResult());
                    });
                }
            }).Start();
        }

        static async Task SyncGrab(Action action)
        {
            var tsc = new TaskCompletionSource();

            _tasks.Enqueue((action, tsc));

            await tsc.Task;
        }

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
                new Thread(async () =>
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
                            await SyncGrab(() =>
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
                            });
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