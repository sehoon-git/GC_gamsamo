using System;
using System.IO;
using System.Text;
using Gamsamo.Progression;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace Gamsamo.Save
{
    public enum SaveKind { Missing, Current, LegacySixPatients, LegacyEconomy, UnsupportedVersion, Corrupt, Unreadable }
    public sealed class SaveInspection
    {
        public string Path, Message;
        public SaveKind Kind;
        public GameProgress State;
    }
    /// <summary>Read-only preflight. Never migrates, deletes or activates a save.</summary>
    public static class ProgressSaveInspection
    {
        public static bool HasLegacyEconomy(EconomyState e) => e.Ledger == null && (e.Balance != 0 || e.HighestRewards.Count != 0
            || e.FirstClearBonuses.Count != 0 || e.OwnedItems.Count != 0 || e.EquippedItems.Count != 0);
        public static SaveInspection Inspect(string path)
        {
            var result = new SaveInspection { Path = System.IO.Path.GetFullPath(path), Kind = SaveKind.Missing };
            if (!File.Exists(path)) return result;
            try
            {
                var json = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                if ((int?)json["Version"] != 1) result.Kind = SaveKind.UnsupportedVersion;
                else if (json["Patients"] is JArray patients && patients.Count == 6) result.Kind = SaveKind.LegacySixPatients;
                else
                {
                    var state = json.ToObject<GameProgress>(); state.Validate();
                    var e = state.Economy;
                    if (HasLegacyEconomy(e)) result.Kind = SaveKind.LegacyEconomy;
                    else { result.Kind = SaveKind.Current; result.State = state; }
                }
                result.Message = result.Kind == SaveKind.Current ? "" : "이전 저장은 원본 보존 중입니다. 자동 이관하지 않습니다.";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            { result.Kind = SaveKind.Unreadable; result.Message = e.Message; }
            catch (Exception e) when (e is JsonException || e is InvalidOperationException || e is ArgumentException || e is FormatException || e is OverflowException)
            { result.Kind = SaveKind.Corrupt; result.Message = "손상된 저장입니다. 원본과 .bak를 보존해 주세요."; }
            return result;
        }
        public static IReadOnlyList<SaveInspection> Scan(string root) => !Directory.Exists(root) ? new SaveInspection[0]
            : Directory.GetFiles(root, "*.json").Where(p => Guid.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(p), "N", out _))
                .OrderBy(p => p, StringComparer.Ordinal).Select(Inspect).ToArray();
        // Explicit export only: caller chooses a new destination, never the original or active pointer.
        public static SaveInspection CopyForRecovery(string source, string destination)
        {
            var inspection = Inspect(source);
            if (inspection.Kind == SaveKind.Missing || inspection.Kind == SaveKind.Unreadable) throw new IOException("백업할 원본을 읽을 수 없습니다.");
            File.Copy(source, destination, false);
            return Inspect(destination);
        }
    }
    /// <summary>One atomic file commits progression, settlement, inventory and unlocks together.</summary>
    public sealed class GameProgressStore : IProgressStore
    {
        private readonly string path;
        public Action BeforeReplace;
        public GameProgressStore(string path) { this.path = Path.GetFullPath(path); }
        public GameProgress Load()
        {
            var inspection = ProgressSaveInspection.Inspect(path);
            if (inspection.Kind == SaveKind.Missing) return null;
            if (inspection.Kind != SaveKind.Current) throw new InvalidDataException(inspection.Kind + ": " + inspection.Message);
            return inspection.State;
        }
        public void Save(GameProgress candidate, int expectedRevision)
        {
            candidate.Validate();
            if (ProgressSaveInspection.HasLegacyEconomy(candidate.Economy)) throw new InvalidDataException("구 경제 형식으로 저장할 수 없습니다. 원본은 보존됩니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
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
