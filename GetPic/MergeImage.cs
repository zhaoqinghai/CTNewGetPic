using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace CTNewGetPic
{
    public class MergeImage
    {
        private readonly ILogger<MergeImage> _logger;

        private readonly ImageTransportPump _pump;
        private readonly LocalSettings _settings;
        private int _started = 0;
        private object _lock = new object();

        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<int, byte[]> _lastImageDict = new ConcurrentDictionary<int, byte[]>();
        private readonly Dictionary<int, DalsaConfig> _configs = new Dictionary<int, DalsaConfig>();
        private ManualResetEventSlim manualSet = new ManualResetEventSlim(false);
        private readonly ConcurrentDictionary<int, MergeImgMat> _mergeImages = new ConcurrentDictionary<int, MergeImgMat>();
        private readonly Dictionary<int, int> _effectivePxRangeDict;
        private Channel<string> _channel;
        private readonly Stopwatch _stopwatch;

        public MergeImage(ILogger<MergeImage> logger, ImageTransportPump pump, IOptions<LocalSettings> settings, [FromKeyedServices(MqttServer.MQTT_CHANNEL)] Channel<string> channel)
        {
            _logger = logger;
            _pump = pump;
            _settings = settings.Value;
            _configs = _settings.CamConfigs.ToDictionary(x => x.Id);
            _stopwatch = new Stopwatch();
            _channel = channel;
            var orderCameras = _settings.CamConfigs.OrderBy(x => x.Id).ToList();
            _effectivePxRangeDict = orderCameras.Select((x, i) => (x.Id, orderCameras.Take(i).Sum(_ => _.EndX - _.BeginX))).ToDictionary(x => x.Id, x => x.Item2);
        }

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                lock (_lock)
                {
                    if (_cts == null)
                    {
                        _cts = new CancellationTokenSource();
                    }

                    new Thread(async () =>
                    {
                        await foreach (var imgs in _pump.ImageGroupChannel.Reader.ReadAllAsync(_cts.Token))
                        {
                            _stopwatch.Restart();
                            if (imgs == null || imgs.Count == 0)
                            {
                                continue;
                            }
                            var currentFrameNo = imgs.FirstOrDefault().FrameNo;
                            try
                            {
                                Parallel.ForEach(imgs, img =>
                                {
                                    _logger.LogInformation("C{0}-F{1} 存图", img.CameraId, img.FrameNo);

                                    if (_configs.TryGetValue(img.CameraId, out var config) && img.FrameHeight >= config.YOffset)
                                    {
                                        if (MatCache.GetCache(img.CacheId, out var cache) && cache != null)
                                        {
                                            var mergeMat = new MergeImgMat()
                                            {
                                                Data = cache,
                                                Order = img.CameraId
                                            };
                                            try
                                            {
                                                _logger.LogInformation("DEBUG C{0}|F{1} Vertical Merge Start {2} ms", img.CameraId, currentFrameNo, _stopwatch.ElapsedMilliseconds);
                                                MatCache.Delete(img.CacheId);

                                                if (config.YOffset == 0)
                                                {
                                                    _logger.LogInformation("C{0}-F{1} Array Length {2} YOffset {3}", img.CameraId, img.FrameNo, _mergeImages.Count, config.YOffset);
                                                    _mergeImages.AddOrUpdate(mergeMat.Order, _ => mergeMat, (_, _) => mergeMat);
                                                    return;
                                                }
                                                if (!_lastImageDict.TryGetValue(img.CameraId, out var info))
                                                {
                                                    unsafe
                                                    {
                                                        var size = config.YOffset * cache.Width * cache.Channels;
                                                        var dataArr = _lastImageDict.AddOrUpdate(img.CameraId, _ => new byte[size], (_, old) => old.Length == size ? old : new byte[size]);
                                                        fixed (byte* arr = dataArr)
                                                        {
                                                            Unsafe.CopyBlock(ref Unsafe.AsRef<byte>(arr), ref Unsafe.Add(ref Unsafe.AsRef<byte>((void*)cache.Data), (cache.Height - config.YOffset) * cache.Width * cache.Channels), (uint)size);
                                                        }
                                                    }
                                                }
                                                else if (cache.Width == info.Length / (config.YOffset * cache.Channels) && info.Length % (config.YOffset * cache.Channels) == 0)
                                                {
                                                    var size = config.YOffset * cache.Width * cache.Channels;
                                                    var data = ArrayPool<byte>.Shared.Rent(size);
                                                    try
                                                    {
                                                        Buffer.BlockCopy(info, 0, data, 0, size);
                                                        unsafe
                                                        {
                                                            var dataArr = _lastImageDict.AddOrUpdate(img.CameraId, _ => new byte[size], (_, old) => old.Length == size ? old : new byte[size]);
                                                            fixed (byte* arr = dataArr)
                                                            fixed (byte* lastFrameData = data)
                                                            {
                                                                Unsafe.CopyBlock(ref Unsafe.AsRef<byte>(arr), ref Unsafe.Add(ref Unsafe.AsRef<byte>((void*)cache.Data), (cache.Height - config.YOffset) * cache.Width * cache.Channels), (uint)size);

                                                                Unsafe.CopyBlock(ref Unsafe.Add(ref Unsafe.AsRef<byte>((void*)cache.Data), config.YOffset * cache.Width * cache.Channels), ref Unsafe.AsRef<byte>((void*)cache.Data), (uint)((cache.Height - config.YOffset) * cache.Width * cache.Channels));
                                                                Unsafe.CopyBlock(ref Unsafe.AsRef<byte>((void*)cache.Data), ref Unsafe.AsRef<byte>(lastFrameData), (uint)size);
                                                            }
                                                        }
                                                        _mergeImages.AddOrUpdate(mergeMat.Order, _ => mergeMat, (_, _) => mergeMat);
                                                    }
                                                    finally
                                                    {
                                                        ArrayPool<byte>.Shared.Return(data);
                                                    }
                                                }
                                                else
                                                {
                                                    _logger.LogError("old L{0}, new W{1}|H{2}|C{3}", info.Length, cache.Width, cache.Height, cache.Channels);
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger.LogError("单独进行纵向合图异常:{0}", ex);
                                            }
                                            finally
                                            {
                                                if (!_mergeImages.ContainsKey(mergeMat.Order))
                                                {
                                                    Marshal.FreeHGlobal(cache.Data);
                                                    _mergeImages.TryRemove(mergeMat.Order, out _);
                                                }
                                            }
                                        }
                                    }
                                });
                                _logger.LogInformation("DEBUG F{0} Vertical Merge Elapsed {1} ms", currentFrameNo, _stopwatch.ElapsedMilliseconds);

                                if (_mergeImages.Count == imgs.Count && imgs.Count > 0 && _mergeImages.All(x => x.Value.Data.Height == imgs.First().FrameHeight))
                                {
                                    var height = _mergeImages.First().Value.Data.Height;
                                    var channels = _mergeImages.First().Value.Data.Channels;
                                    var type = _mergeImages.First().Value.Data.Type;
                                    if (_mergeImages.Any(x => x.Value.Data.Height != height) || _mergeImages.Any(x => x.Value.Data.Channels != channels) || _mergeImages.Any(x => x.Value.Data.Type != type))
                                    {
                                        _logger.LogInformation("合图横向拼接存在高度或通道数不一致问题 H({0}), C({0})", string.Join(',', _mergeImages.Select(x => x.Value.Data.Height)), string.Join(',', _mergeImages.Select(x => x.Value.Data.Channels)));
                                        return;
                                    }
                                    var mergeTotalWidth = _mergeImages.Sum(x => x.Value.Data.Width);
                                    var mergeTotalStride = channels * mergeTotalWidth;
                                    var size = height * mergeTotalStride;
                                    using ManualResetEventSlim manualSet = new ManualResetEventSlim(false);
                                    ThreadPool.QueueUserWorkItem(async _ =>
                                    {
                                        var mergeArr = Marshal.AllocHGlobal(size);
                                        try
                                        {
                                            var sw = Stopwatch.StartNew();

                                            Parallel.ForEach(_mergeImages, img =>
                                            {
                                                var stride = img.Value.Data.Channels * img.Value.Data.Width;
                                                for (var i = 0; i < height; i++)
                                                {
                                                    unsafe
                                                    {
                                                        ref byte src = ref Unsafe.Add(ref Unsafe.AsRef<byte>((void*)img.Value.Data.Data), i * stride);
                                                        ref byte dst = ref Unsafe.Add(ref Unsafe.AsRef<byte>((void*)mergeArr), i * mergeTotalStride + _effectivePxRangeDict[img.Value.Order] * channels);
                                                        Unsafe.CopyBlock(ref dst, ref src, (uint)stride);
                                                    }
                                                }
                                            });
                                            manualSet.Set();
                                            _logger.LogInformation("DEBUG F{0} Horizontal Merge Elapsed {1} ms", currentFrameNo, sw.ElapsedMilliseconds);
                                            var path = Path.Combine(_settings.SaveImgDir, $"{DateTime.Today:yyyy-MM-dd}\\{DateTime.Now:MMdd_HHmmss_fff}.jpg");
                                            var directoryPath = Path.GetDirectoryName(path);
                                            if (!string.IsNullOrEmpty(directoryPath))
                                            {
                                                if (!Directory.Exists(directoryPath))
                                                {
                                                    Directory.CreateDirectory(directoryPath);
                                                }
                                                using var mat = Mat.FromPixelData(height, mergeTotalWidth, type, mergeArr);
                                                Cv2.ImWrite(path, mat);
                                                _logger.LogInformation("DEBUG F{0} Save Image Elapsed {1} ms", currentFrameNo, sw.ElapsedMilliseconds);

                                                _logger.LogInformation("完成合图 F{0}", currentFrameNo);

                                                await _channel.Writer.WriteAsync(path);

                                                _logger.LogInformation("DEBUG F{0} Publish channel msg: {1}", currentFrameNo, path);
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError("horizontal merge or save img error: {0}", ex);
                                            if (!manualSet.IsSet)
                                            {
                                                manualSet.Set();
                                            }
                                        }
                                        finally
                                        {
                                            Marshal.FreeHGlobal(mergeArr);
                                        }
                                    }, null);
                                    manualSet.Wait();
                                }
                                else
                                {
                                    _logger.LogInformation("合图存在缺失 F({0})", string.Join(',', imgs.Select(x => x.FrameNo)));
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError("合图失败:{0}", ex);
                            }
                            finally
                            {
                                foreach (var mat in _mergeImages)
                                {
                                    Marshal.FreeHGlobal(mat.Value.Data.Data);
                                }

                                _mergeImages.Clear();
                                _logger.LogInformation("DEBUG F{0} Finished Elapsed {1} ms", currentFrameNo, _stopwatch.ElapsedMilliseconds);
                            }
                        }
                    }).Start();
                }
            }
        }

        public void Stop()
        {
            if (Interlocked.CompareExchange(ref _started, 0, 1) == 1)
            {
                lock (_lock)
                {
                    _cts?.Cancel();
                    _cts?.Dispose();
                    _cts = null;
                }
            }
        }

        public class MergeImgMat
        {
            public int Order { get; set; }

            public required ImgCache Data { get; set; }
        }
    }
}