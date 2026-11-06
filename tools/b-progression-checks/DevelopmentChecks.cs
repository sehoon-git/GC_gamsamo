using System;
using System.IO;
using System.Linq;
using Gamsamo.Progression;
using Gamsamo.Save;
using Newtonsoft.Json.Linq;

static class DevelopmentChecks
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        var root = Path.Combine(Path.GetTempPath(), "gamsamo-dev-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var roster = new[] { "seoyun", "dohyeon", "taejun", "yeonwoo", "harin" };
            var normalStore = new GameProgressStore(Path.Combine(root, "normal.json"));
            var normal = new ProgressService(normalStore); normal.Create(roster);
            var normalBytes = File.ReadAllBytes(Path.Combine(root, "normal.json"));
            reject(() => normal.BeginDevelopmentVisit(), "normal progress cannot enter developer traversal");
            reject(() => normal.SkipDevelopmentVisit(new RoundIdentity()), "normal progress cannot accept 70/70 skip");
            var store = new GameProgressStore(Path.Combine(root, "development.json"));
            var progress = new ProgressService(store); progress.Create(roster, true);
            var initialBalance = progress.State.Economy.Balance;
            for (int i = 0; i < roster.Length; i++)
            {
                progress.BeginDevelopmentVisit();
                var state = progress.State; var patient = state.Current;
                var identity = new RoundIdentity { ProgressId = state.ProgressId, PatientId = patient.PatientId, CaseId = patient.CaseId, RoundId = patient.RoundId };
                progress = new ProgressService(store); progress.BeginDevelopmentVisit();
                check(progress.State.Current.RoundId == identity.RoundId, "resume keeps development round " + i);
                reject(() => progress.CaptureAi(patient.PatientId, patient.CaseId, patient.RoundId, "not evidence", "evaluated"), "development preview cannot invent AI records " + i);
                var wrong = GameProgress.Copy(identity); wrong.RoundId = "different";
                reject(() => progress.SkipDevelopmentVisit(wrong), "wrong development round rejected " + i);
                var before = progress.State.Revision;
                store.BeforeReplace = () => { throw new IOException("test failure"); };
                reject(() => progress.SkipDevelopmentVisit(identity, patient.PatientId + "_smile"), "developer save failure " + i);
                check(progress.State.Revision == before && store.Load().ClearedCount == i
                    && progress.State.Phase == ProgressPhase.Consulting, "failed skip leaves progress and photo locked " + i);
                store.BeforeReplace = null;
                var receipt = progress.SkipDevelopmentVisit(identity, patient.PatientId + "_smile");
                check(receipt.DevelopmentSkip && receipt.PatientScore == 70 && receipt.CounselorScore == 70
                    && receipt.Granted == 0 && progress.State.Economy.Balance == initialBalance, "explicit 70/70 preview has no coin award " + i);
                var committed = progress.State.Revision;
                progress = new ProgressService(store);
                progress.SkipDevelopmentVisit(identity, patient.PatientId + "_smile");
                check(progress.State.Revision == committed && progress.State.Settlements.Count == i + 1, "retry/restart never duplicates development settlement " + i);
                reject(() => progress.Depart(), "developer skip still waits for presentation " + i);
                progress.SavePresentationStage(identity.Key, "skipped"); progress.Depart();
                progress.SkipDevelopmentVisit(identity);
                check(progress.State.CurrentIndex == i + 1 && progress.State.Settlements.Count == i + 1, "late prior-round replay cannot skip next patient " + i);
                check(progress.State.Patients.All(p => p.AiSnapshot == null && p.PendingAiOperation == null), "no fake AI save or pending request " + i);
            }
            check(progress.State.FinalEndingUnlocked && progress.State.Phase == ProgressPhase.Finished && progress.State.Photos.Count == 5,
                "all five development patients reach final ending");
            progress.CompleteFinalEnding();
            check(new ProgressService(store).State.FinalEndingPlayed, "development ending completion survives restart");
            var mixed = progress.State; mixed.DevelopmentMode = false;
            reject(() => mixed.Validate(), "developer receipt is rejected in normal progress");
            check(normalBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "normal.json"))), "development traversal preserves normal save byte for byte");
            var old = JObject.FromObject(normal.State); old.Remove("DevelopmentMode");
            var compatible = old.ToObject<GameProgress>(); compatible.Validate();
            check(!compatible.DevelopmentMode, "old saves default to normal mode");
        }
        finally { Directory.Delete(root, true); }
    }
}
