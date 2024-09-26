using System.Text.Json.Serialization;

namespace CTNewGetPic
{
    public class LocalSettings
    {
        public DalsaConfig[] CamConfigs { get; set; } = null!;

        public string SpotName { get; set; } = null!;

        public string SaveImgDir { get; set; } = null!;

        public MqttServerSettings MqttServerSettings { get; set; } = null!;

        public int RemainDays { get; set; } = 1;
    }

    public class MqttServerSettings
    {
        public string ServerIp { get; set; } = null!;

        public int ServerPort { get; set; }

        public string Topic { get; set; } = null!;
    }

    public class DalsaConfig
    {
        public int Id { get; set; }

        public string DeviceName { get; set; } = null!;

        public string ServerName { get; set; } = null!;

        public string ConfigFilePath { get; set; } = null!;

        public int YOffset { get; set; }

        public int BeginX { get; set; }

        public int EndX { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(LocalSettings))]
    [JsonSerializable(typeof(MqttServerSettings))]
    [JsonSerializable(typeof(DalsaConfig))]
    internal partial class SourceGenerationContext : JsonSerializerContext
    {
    }
}