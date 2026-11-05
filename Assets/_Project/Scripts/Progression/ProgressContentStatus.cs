using System;
using System.Collections.Generic;
using System.Linq;

namespace Gamsamo.Progression
{
    public sealed class RankStatus
    {
        public string Current = "승급 기준 준비 중", Next = "승급 기준 준비 중";
        public int? RequiredClears;
    }
    public static class ApprovedRankPolicy
    {
        // No default thresholds. The caller must supply an explicitly approved policy.
        public static RankStatus Evaluate(int clears, IReadOnlyDictionary<int, string> thresholds, bool approved)
        {
            var result = new RankStatus();
            if (!approved || thresholds == null || thresholds.Count == 0) return result;
            if (clears < 0 || clears > 5 || thresholds.Any(p => p.Key < 0 || p.Key > 5 || string.IsNullOrWhiteSpace(p.Value)))
                throw new ArgumentException("승급 기준을 확인해 주세요.");
            result.Current = thresholds.Where(p => p.Key <= clears).OrderByDescending(p => p.Key).Select(p => p.Value).FirstOrDefault() ?? "미승급";
            var next = thresholds.Where(p => p.Key > clears).OrderBy(p => p.Key).ToArray();
            result.Next = next.Length == 0 ? "최고 등급" : next[0].Value;
            result.RequiredClears = next.Length == 0 ? (int?)null : next[0].Key;
            return result;
        }
    }
    public sealed class EndingAvailability
    {
        public string PatientId, PhotoId, Title, Status;
        public bool Unlocked, Approved, PhotoPresent, StoryPresent;
        public bool Ready => Approved && StoryPresent;
    }
}
