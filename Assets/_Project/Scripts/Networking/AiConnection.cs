using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Gamsamo.Networking
{
    /// <summary>Connection secrets are local settings, never scene fields or conversation state.</summary>
    public sealed class AiConnection
    {
        public string BaseUrl { get; private set; }
        public string ApiKey { get; private set; }
        public static AiConnection Load(string defaultFile)
        {
            string file = Environment.GetEnvironmentVariable("GAMSAMO_AI_CONNECTION");
            if (string.IsNullOrWhiteSpace(file)) file = defaultFile;
            string url, key;
            if (File.Exists(file))
            {
                JObject value = AiProtocol.Parse(File.ReadAllText(file));
                url = AiProtocol.Text(value, "api_url"); key = (string)value["api_key"] ?? "";
            }
            else
            {
                url = Environment.GetEnvironmentVariable("SEOYUN_API_URL");
                key = Environment.GetEnvironmentVariable("SEOYUN_API_KEY") ?? "";
            }
            return FromSettings(url, key);
        }
        public static AiConnection FromSettings(string url, string key)
        {
            key = key ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
                throw new InvalidDataException("AI 연결 설정을 확인해 주세요. 외부 서버는 HTTPS 주소를 사용합니다.");
            if (!uri.IsLoopback && string.IsNullOrWhiteSpace(key))
                throw new InvalidDataException("외부 AI 연결에 접근 키가 필요합니다.");
            foreach (char character in key)
                if (character < 33 || character > 126) throw new InvalidDataException("접근 키 형식이 올바르지 않습니다.");
            return new AiConnection { BaseUrl = uri.AbsoluteUri.TrimEnd('/'), ApiKey = key };
        }
    }
}
