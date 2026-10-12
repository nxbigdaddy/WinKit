using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WinKit.Core.Serialization
{
    /// <summary>
    /// 零依赖 JSON 序列化。使用 .NET Framework 自带的 DataContractJsonSerializer，
    /// 避免引入 System.Text.Json / Newtonsoft（会向输出目录带入 DLL，破坏"单文件零依赖"）。
    /// </summary>
    public static class Json
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string Serialize<T>(T value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, value);
                return Utf8NoBom.GetString(ms.ToArray());
            }
        }

        /// <summary>反序列化；JSON 为字面量 null 或空内容时返回 null。</summary>
        public static T? Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(Utf8NoBom.GetBytes(json)))
            {
                return serializer.ReadObject(ms) as T;
            }
        }

        /// <summary>尝试反序列化，失败返回 null 而不抛异常（用于容忍日志/配置中的坏行）。</summary>
        public static T? TryDeserialize<T>(string json) where T : class
        {
            try
            {
                return Deserialize<T>(json);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
