using DALSA.SaperaLT.SapClassBasic;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CTNewGetPic
{
    public partial class CameraManager(IOptions<LocalSettings> _settings, ILogger<CameraManager> _logger)
    {
        [GeneratedRegex("^[\\w\\-]+$", RegexOptions.IgnoreCase, "en-US")]
        public static partial Regex ServerNameMatch();

        private readonly Dictionary<string, string> _deviceServerMap = new Dictionary<string, string>();

        public async Task<bool> Init()
        {
            if (_deviceServerMap.Count == 0)
            {
                SapManager.DetectAllServers(SapManagerBase.DetectServerType.All);
                var deviceSet = _settings.Value.CamConfigs.Select(x => x.DeviceName).ToHashSet();
                int i = 0;
                while (i++ < 100)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                    var serverCount = SapManager.GetServerCount(SapManagerBase.ResourceType.AcqDevice);
                    if (serverCount < deviceSet.Count)
                    {
                        _logger.LogInformation("未能查找全部相机设备,当前搜索设备数量：{0}", serverCount);
                        continue;
                    }
                    for (int serverIndex = 0; serverIndex < serverCount; serverIndex++)
                    {
                        var serverName = SapManager.GetServerName(serverIndex, SapManagerBase.ResourceType.AcqDevice);
                        if (!ServerNameMatch().IsMatch(serverName))
                        {
                            _logger.LogInformation($"无法匹配server{serverName},index:{serverIndex}");
                            continue;
                        }
                        string resourceName = SapManager.GetResourceName(serverName, SapManager.ResourceType.AcqDevice, 0);

                        _logger.LogInformation($"已搜索到设备:{serverName}|{resourceName},index:{serverIndex}");
                        if (deviceSet.Contains(resourceName) && !_deviceServerMap.ContainsKey(resourceName))
                        {
                            _deviceServerMap.Add(resourceName, serverName);
                        }
                    }

                    if (_deviceServerMap.Count == deviceSet.Count)
                    {
                        _logger.LogInformation("设备搜索成功");
                        return true;
                    }
                }
                _logger.LogInformation("设备搜索超时失败");
                return false;
            }
            return true;
        }

        public string? GetServerName(string deviceName)
        {
            _deviceServerMap.TryGetValue(deviceName, out var serverName);
            return serverName;
        }
    }
}