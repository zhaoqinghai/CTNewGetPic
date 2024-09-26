using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CTNewGetPic
{
    public class ImageTransportPump
    {
        const int CHANNEL_CAPACITY = 10;

        private readonly ILogger<ImageTransportPump> _logger;
        private readonly IRunServerCmd _runServer;
        private readonly FrameImageInfoCache _cache = new();

        private int _started = 0;

        private CancellationTokenSource? _cancellationTokenSource;

        private readonly object _lock = new object();

        private readonly ConcurrentQueue<ManualResetEventSlim> _slims = new();

        private Channel<List<ImageInfo>> _channel = Channel.CreateBounded<List<ImageInfo>>(new BoundedChannelOptions(CHANNEL_CAPACITY)
        {
            SingleWriter = true,
            AllowSynchronousContinuations = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        public Channel<List<ImageInfo>> ImageGroupChannel => _channel;

        public ImageTransportPump(ILogger<ImageTransportPump> logger, IOptions<LocalSettings> settings, IRunServerCmd server)
        {
            _logger = logger;
            _runServer = server;
            _dict = settings.Value.CamConfigs.Select(x => x.Id).Distinct().ToDictionary(x => x, _ => Channel.CreateBounded<ImageInfo>(new BoundedChannelOptions(CHANNEL_CAPACITY)
            {
                SingleWriter = true,
                AllowSynchronousContinuations = true,
                FullMode = BoundedChannelFullMode.DropOldest
            }));
        }

        private readonly Dictionary<int, Channel<ImageInfo>> _dict;

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        _cancellationTokenSource.Dispose();
                    }
                    catch { }
                }
                _cancellationTokenSource = new CancellationTokenSource();
                foreach (var item in _dict)
                {
                    new Thread(async () =>
                    {
                        var channel = item.Value;
                        _logger.LogInformation("C{0}-启动生成相机图片组线程", item.Key);
                        await foreach (var imgInfo in channel.Reader.ReadAllAsync(_cancellationTokenSource.Token))
                        {
                            _logger.LogInformation("C{0}-F{1} 开始消费", imgInfo.CameraId, imgInfo.FrameNo);
                            if (imgInfo.FrameNo < _cache.FrameId)
                            {
                                _logger.LogInformation("C{0}-F{1} 漏帧 当前帧 {2}", imgInfo.CameraId, imgInfo.FrameNo, _cache.FrameId);
                                MatCache.Delete(imgInfo.CacheId);
                                continue;
                            }
                            lock (_lock)
                            {
                                if (imgInfo.FrameNo < _cache.FrameId)
                                {
                                    MatCache.Delete(imgInfo.CacheId);
                                    continue;
                                }
                                else if (imgInfo.FrameNo == _cache.FrameId)
                                {
                                    _logger.LogInformation("C{0}-F{1} 写入合图数据", imgInfo.CameraId, imgInfo.FrameNo);
                                    _cache.ImgList.Add(imgInfo);
                                    if (_cache.ImgList.Count == _dict.Count)
                                    {
                                        _logger.LogInformation("F{0} 相机组所有图片获取完成", imgInfo.FrameNo);
                                        if (_channel.Reader.Count == CHANNEL_CAPACITY)
                                        {
                                            if (_channel.Reader.TryRead(out var imgs))
                                            {
                                                foreach (var img in imgs)
                                                {
                                                    _logger.LogInformation("C{0}-F{1} drop oldest item", img.CameraId, img.FrameNo);
                                                    MatCache.Delete(img.CacheId);
                                                }
                                            }
                                        }
                                        _channel.Writer.TryWrite(_cache.ImgList.Select(x => x with { }).ToList());
                                        _cache.ImgList.Clear();
                                        Thread.Yield();
                                        continue;
                                    }
                                }
                                else
                                {
                                    _logger.LogInformation("C{0}-F{1} 新建合图缓存", imgInfo.CameraId, imgInfo.FrameNo);
                                    _cache.FrameId = imgInfo.FrameNo;
                                    foreach (var img in _cache.ImgList)
                                    {
                                        MatCache.Delete(img.CacheId);
                                    }
                                    _cache.ImgList.Clear();
                                    _cache.ImgList.Add(imgInfo);
                                    Thread.Yield();
                                    CancelWaiHandle();
                                }
                            }
                            EnqueueWaitHandle(_cancellationTokenSource.Token);
                        }
                    })
                    {
                        IsBackground = false,
                        Priority = ThreadPriority.Lowest,
                    }.Start();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnqueueWaitHandle(CancellationToken token)
        {
            using var manualset = new ManualResetEventSlim(false);
            _slims.Enqueue(manualset);
            if (!manualset.Wait(TimeSpan.FromSeconds(3), token))
            {
                _logger.LogInformation("---------EnqueueWaitHandle 合成图等待取消或超时");
            }
        }

        private void CancelWaiHandle()
        {
            var snapshot = _slims.ToList();
            _slims.Clear();
            foreach (var snapshotItem in snapshot)
            {
                try
                {
                    snapshotItem.Set();
                }
                catch { }
            }
        }

        public void Stop()
        {
            if (Interlocked.CompareExchange(ref _started, 0, 1) == 1)
            {
                foreach (var snapshotItem in _slims)
                {
                    snapshotItem.Set();
                }
                _slims.Clear();
                _cache.ImgList.Clear();
                _cache.FrameId = 0;
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Cancel();
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }

                foreach (var item in _dict)
                {
                    for (; item.Value.Reader.TryRead(out _);) ;
                }
            }
        }

        public void Enqueue(ImageInfo info)
        {
            if (_dict.TryGetValue(info.CameraId, out var channel))
            {
                _logger.LogInformation("C{0}-F{1} Write To Channel", info.CameraId, info.FrameNo);
                if (channel.Reader.Count == CHANNEL_CAPACITY)
                {
                    if (channel.Reader.TryRead(out var img))
                    {
                        _logger.LogInformation("C{0}-F{1} drop oldest item", img.CameraId, img.FrameNo);
                        MatCache.Delete(img.CacheId);
                    }
                }
                channel.Writer.TryWrite(info);
            }
        }

        public class FrameImageInfoCache
        {
            public long FrameId { get; set; }

            public List<ImageInfo> ImgList { get; set; } = new List<ImageInfo>();
        }
    }
}