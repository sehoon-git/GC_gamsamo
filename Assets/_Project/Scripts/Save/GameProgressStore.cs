using System;
using System.IO;
using System.Text;
using Gamsamo.Progression;
using Newtonsoft.Json;

namespace Gamsamo.Save
{
    /// <summary>One atomic file commits progression, settlement, inventory and unlocks together.</summary>
    public sealed class GameProgressStore : IProgressStore
    {
        private readonly string path;
        public Action BeforeReplace;
        public GameProgressStore(string path) { this.path = Path.GetFullPath(path); }
        public GameProgress Load()
        {
            if (!File.Exists(path)) return null;
            GameProgress state;
            try { state = JsonConvert.DeserializeObject<GameProgress>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (JsonException e) { throw new InvalidDataException("진행 저장이 손상되었습니다. 원본을 보존하고 백업을 확인하세요: " + path + ".bak", e); }
            if (state == null) throw new InvalidDataException("진행 저장을 읽지 못했습니다. 백업을 확인해 주세요.");
            state.Validate(); return state;
        }
        public void Save(GameProgress candidate, int expectedRevision)
        {
            candidate.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var guard = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                var old = Load();
                if ((old?.Revision ?? -1) != expectedRevision || candidate.Revision != expectedRevision + 1
                    || (old != null && old.ProgressId != candidate.ProgressId)) throw new IOException("다른 실행이 저장을 변경했습니다. 다시 불러오세요.");
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(candidate, Formatting.Indented));
                    using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                    BeforeReplace?.Invoke();
                    if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
    }
}
