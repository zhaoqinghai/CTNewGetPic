using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

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
        private readonly ConcurrentDictionary<int, Mat> _lastImageDict = new ConcurrentDictionary<int, Mat>();
        private readonly Dictionary<int, DalsaConfig> _configs = new Dictionary<int, DalsaConfig>();
        private ManualResetEventSlim manualSet = new ManualResetEventSlim(false);
        private readonly List<MergeImgMat> _mergeImages = new List<MergeImgMat>();
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
                                        if (MatCache.GetCache(img.CacheId, out var mat) && mat != null)
                                        {
                                            MatCache.Delete(img.CacheId);
                                            if (config.YOffset == 0)
                                            {
                                                lock (_mergeImages)
                                                {
                                                    _logger.LogInformation("C{0}-F{1} Array Length {2} YOffset {3}", img.CameraId, img.FrameNo, _mergeImages.Count, config.YOffset);
                                                    _mergeImages.Add(new MergeImgMat
                                                    {
                                                        Data = mat,
                                                        Order = img.CameraId,
                                                    });
                                                }
                                                return;
                                            }
                                            try
                                            {
                                                if (!_lastImageDict.TryGetValue(img.CameraId, out var info))
                                                {
                                                    _lastImageDict.TryAdd(img.CameraId, new Mat(mat, new Rect(0, mat.Height - config.YOffset, mat.Width, config.YOffset)));
                                                }
                                                else if (mat.Width == info.Width && info != null)
                                                {
                                                    _lastImageDict.TryUpdate(img.CameraId, new Mat(mat, new Rect(0, mat.Height - config.YOffset, mat.Width, config.YOffset)), info);
                                                    Mat src = new Mat();
                                                    Cv2.VConcat(new List<Mat> { info, new Mat(mat, new Rect(0, 0, mat.Width, mat.Height - config.YOffset)) }, src);
                                                    lock (_mergeImages)
                                                    {
                                                        _logger.LogInformation("C{0}-F{1} Array Length {2} YOffset {3}", img.CameraId, img.FrameNo, _mergeImages.Count, config.YOffset);

                                                        _mergeImages.Add(new MergeImgMat
                                                        {
                                                            Data = src,
                                                            Order = img.CameraId,
                                                        });
                                                    }
                                                    info.Dispose();
                                                    info = null;
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger.LogError("单独进行纵向合图异常:{0}", ex);
                                            }
                                            finally
                                            {
                                                mat.Dispose();
                                                mat = null;
                                            }
                                        }
                                    }
                                });
                                _logger.LogInformation("DEBUG F{0} Vertical Merge Elapsed {1} ms", currentFrameNo, _stopwatch.ElapsedMilliseconds);
                                if (_mergeImages.Count == imgs.Count && imgs.Count > 0)
                                {
                                    manualSet.Reset();
                                    ThreadPool.QueueUserWorkItem(async _ =>
                                    {
                                        var sw = Stopwatch.StartNew();
                                        using var mat = new Mat();
                                        Cv2.HConcat(_mergeImages.OrderBy(x => x.Order).Select(x => x.Data), mat);
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

                                            var encodeParam = new ImageEncodingParam(ImwriteFlags.JpegQuality, 95);

                                            Cv2.ImWrite(path, mat, new[] { encodeParam });
                                            _logger.LogInformation("DEBUG F{0} Save Image Elapsed {1} ms", currentFrameNo, sw.ElapsedMilliseconds);

                                            _logger.LogInformation("完成合图 F{0}", currentFrameNo);

                                            await _channel.Writer.WriteAsync(path);

                                            _logger.LogInformation("DEBUG F{0} Publish channel msg: {1}", currentFrameNo, path);
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
                                    mat.Data?.Dispose();
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

            public Mat Data { get; set; } = null!;
        }
    }
}