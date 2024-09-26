using NLog;
using OpenCvSharp;
using System.Collections.Concurrent;

namespace CTNewGetPic
{
    public static class MatCache
    {
        private static readonly ConcurrentDictionary<Guid, Mat> _cache = new ConcurrentDictionary<Guid, Mat>();

        private static readonly ConcurrentQueue<(Guid, long)> _cacheIds = new ConcurrentQueue<(Guid, long)>();

        static MatCache()
        {
            new Thread(async () =>
            {
                var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync())
                {
                    while (_cacheIds.TryPeek(out var cache))
                    {
                        if (!_cache.ContainsKey(cache.Item1))
                        {
                            _cacheIds.TryDequeue(out var _);
                            continue;
                        }
                        if (DateTimeOffset.Now.ToUnixTimeSeconds() - cache.Item2 > 3)
                        {
                            LogManager.GetCurrentClassLogger().Error("********************************************");
                            Delete(cache.Item1);
                            _cacheIds.TryDequeue(out var _);
                        }
                        else
                        {
                            break;
                        }
                    }
                    GC.Collect();
                }
            })
            {
                IsBackground = false,
                Priority = ThreadPriority.BelowNormal
            }.Start();
        }

        public static void Delete(Guid id)
        {
            if (_cache.TryRemove(id, out var mat))
            {
                lock (mat)
                {
                    mat.Dispose();
                }
                mat = null;
            }
        }

        public static bool AddCache(Mat mat, out Guid id)
        {
            id = Guid.NewGuid();
            _cacheIds.Enqueue((id, DateTimeOffset.Now.ToUnixTimeSeconds()));
            return _cache.TryAdd(id, mat);
        }

        public static bool GetCache(Guid id, out Mat? mat)
        {
            mat = null;
            if (_cache.TryGetValue(id, out var cache))
            {
                lock (cache)
                {
                    if (cache.IsDisposed)
                    {
                        return false;
                    }
                    mat = cache.Clone();
                    return true;
                }
            }
            return false;
        }
    }
}