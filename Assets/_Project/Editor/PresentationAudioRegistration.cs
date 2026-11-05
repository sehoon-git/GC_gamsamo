using System;
using System.IO;
using System.Collections.Generic;
using Gamsamo.UI.Presentation;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Explicit catalog registration after delivery; never downloads or approves media.</summary>
public static class PresentationAudioRegistration
{
    public static string[] Validate(string manifestPath)
    {
        var errors = new List<string>();
        Read(manifestPath, errors);
        return errors.ToArray();
    }
    private static Dictionary<string, AudioClip> Read(string manifestPath, List<string> errors)
    {
        var clips = new Dictionary<string, AudioClip>();
        if (!File.Exists(manifestPath)) { errors.Add("음원 manifest 누락"); return clips; }
        JObject manifest;
        try { manifest = JObject.Parse(File.ReadAllText(manifestPath)); }
        catch (Exception e) when (e is IOException || e is Newtonsoft.Json.JsonException) { errors.Add("manifest 읽기 실패: " + e.Message); return clips; }
        var items = manifest["tracks"] as JArray;
        if (items == null) { errors.Add("tracks 배열 누락"); return clips; }
        foreach (var token in items)
        {
            if (!(token is JObject track)) { errors.Add("잘못된 트랙 항목"); continue; }
            string slot = (string)track["slot"], path = (string)track["path"], guid = (string)track["guid"];
            if (slot != "roomMusic" && slot != "endingMusic" && slot != "pageSound" && slot != "clearSound") { errors.Add("알 수 없는 슬롯: " + slot); continue; }
            if (clips.ContainsKey(slot)) { errors.Add(slot + ": 중복 슬롯"); continue; }
            clips[slot] = null;
            if (track["approved"]?.Type != JTokenType.Boolean || !(bool)track["approved"]) errors.Add(slot + ": 승인 대기");
            if (string.IsNullOrWhiteSpace((string)track["license"]) || string.IsNullOrWhiteSpace((string)track["source"])) errors.Add(slot + ": 라이선스·출처 누락");
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) || path.Contains("..") || path.Contains("\\")) { errors.Add(slot + ": Assets 상대 경로 필요"); continue; }
            if (string.IsNullOrWhiteSpace(guid) || AssetDatabase.AssetPathToGUID(path) != guid) errors.Add(slot + ": GUID 불일치");
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) errors.Add(slot + ": AudioClip 파일 누락");
            clips[slot] = clip;
        }
        foreach (string slot in new[] { "roomMusic", "endingMusic", "pageSound", "clearSound" })
            if (!clips.ContainsKey(slot)) errors.Add(slot + ": manifest 항목 누락");
        return clips;
    }
    public static void Register(string manifestPath, EndingContentCatalog catalog)
    {
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));
        var errors = new List<string>(); var clips = Read(manifestPath, errors);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
        Undo.RecordObject(catalog, "Register approved audio");
        catalog.roomMusic = clips["roomMusic"]; catalog.endingMusic = clips["endingMusic"];
        catalog.pageSound = clips["pageSound"]; catalog.clearSound = clips["clearSound"];
        EditorUtility.SetDirty(catalog); AssetDatabase.SaveAssetIfDirty(catalog);
    }
}
