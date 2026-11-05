using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gamsamo.UI.Presentation
{
    [Serializable]
    public sealed class EndingContent
    {
        public string patientId;
        public string photoId;
        public bool approved;
        public string title;
        [TextArea(3, 12)] public string description;
        public Sprite photo;
    }

    /// <summary>Authored media only. This asset grants no photos, endings, score or currency.</summary>
    [CreateAssetMenu(menuName = "Gamsamo/Presentation/Ending Content Catalog")]
    public sealed class EndingContentCatalog : ScriptableObject
    {
        public string contentVersion;
        public EndingContent[] patients = Array.Empty<EndingContent>();
        public bool creditsApproved;
        [TextArea(5, 20)] public string credits;
        public AudioClip roomMusic;
        public AudioClip endingMusic;
        public AudioClip pageSound;
        public AudioClip clearSound;
        public string[] Audit()
        {
            var issues = new List<string>();
            var ids = new HashSet<string>();
            foreach (var item in patients ?? Array.Empty<EndingContent>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.patientId)) { issues.Add("환자 ID 누락"); continue; }
                if (!ids.Add(item.patientId)) issues.Add(item.patientId + ": 중복 ID");
                if (item.photoId != item.patientId + "_smile") issues.Add(item.patientId + ": 사진 권리 ID 불일치");
                if (!item.approved) issues.Add(item.patientId + ": 승인 대기");
                if (item.photo == null) issues.Add(item.patientId + ": 사진 파일 누락");
                if (string.IsNullOrWhiteSpace(item.title)) issues.Add(item.patientId + ": 제목 누락");
                if (string.IsNullOrWhiteSpace(item.description)) issues.Add(item.patientId + ": 후일담 누락");
            }
            foreach (var id in new[] { "seoyun", "dohyeon", "taejun", "yeonwoo", "harin" })
                if (!ids.Contains(id)) issues.Add(id + ": 콘텐츠 등록 누락");
            if (!creditsApproved) issues.Add("크레딧 승인 대기");
            if (string.IsNullOrWhiteSpace(credits)) issues.Add("팀원 이름·역할·표기 정보 누락");
            if (roomMusic == null) issues.Add("roomMusic 누락");
            if (endingMusic == null) issues.Add("endingMusic 누락");
            if (pageSound == null) issues.Add("pageSound 누락");
            if (clearSound == null) issues.Add("clearSound 누락");
            return issues.ToArray();
        }

        public EndingContent FindApproved(string patientId)
        {
            if (patients == null) return null;
            foreach (var item in patients)
                if (item != null && item.approved && item.patientId == patientId) return item;
            return null;
        }
    }
}
