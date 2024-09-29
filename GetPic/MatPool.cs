using NLog;
using OpenCvSharp;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CTNewGetPic
{
    public static class MatCache
    {
        private static readonly ConcurrentDictionary<Guid, ImgCache> _cache = new ConcurrentDictionary<Guid, ImgCache>();

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
                    if (mat.IsDisposed)
                    {
                        return;
                    }
                    mat.IsDisposed = true;
                    Marshal.FreeHGlobal(mat.Data);
                }

                mat = null;
            }
        }

        public unsafe static bool AddCache(IntPtr mat, int height, int width, int channels, MatType type, out Guid id)
        {
            id = Guid.NewGuid();
            _cacheIds.Enqueue((id, DateTimeOffset.Now.ToUnixTimeSeconds()));
            var size = width * height * channels;
            var data = Marshal.AllocHGlobal(size);
            if (!_cache.TryAdd(id, new ImgCache { Data = data, Length = size, Width = width, Height = height, Channels = channels, Type = type }))
            {
                Marshal.FreeHGlobal(data);
                return false;
            }

            CopyMemory(data, mat, (uint)size);

            return true;
        }

        public static bool GetCache(Guid id, out ImgCache? mat)
        {
            mat = null;
            if (_cache.TryGetValue(id, out var cache))
            {
                lock (cache)
                {
                    if (cache == null || cache.IsDisposed)
                    {
                        return false;
                    }
                    cache.IsDisposed = true;
                    mat = cache;
                    return true;
                }
            }
            return false;
        }

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory(IntPtr destination, IntPtr source, uint length);
    }

    public class ImgCache
    {
        public IntPtr Data { get; init; } = IntPtr.Zero;

        public int Width { get; init; }

        public int Height { get; init; }

        public int Channels { get; init; }

        public MatType Type { get; init; }

        public int Length { get; init; }

        public bool IsDisposed { get; set; } = false;
    }
}