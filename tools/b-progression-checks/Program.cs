using System;
using System.IO;
using System.Linq;
using Gamsamo.Progression;
using Gamsamo.Save;
class FixedReward : IRewardCalculator {
 public int Calls;
 public RewardCalculation Calculate(SavedEvaluation e, EconomyState old, bool first) {
  Calls++; old.Balance += 17;
  old.Ledger ??= Gamsamo.Economy.EconomyState.NewRun(e.Identity.ProgressId);
  old.Ledger.settlements.Add(new Gamsamo.Economy.SessionSettlement {patientId=e.Identity.PatientId,caseId=e.Identity.CaseId,sessionId=e.Identity.RoundId,
   patientScore=e.PatientScore,counselorScore=e.CounselorScore,cleared=e.PatientScore>=70 && e.CounselorScore>=70,firstClear=first,
   baseThreshold=old.Ledger.HighestPaidBaseFor(e.Identity.PatientId)+17,baseCoins=17});
  old.Ledger.balance+=17; old.Ledger.totalEarned+=17;
  return new RewardCalculation { SettlementKey=e.Identity.Key, Granted=17, Economy=old }; }
}
class Program {
 static int checks;
 static void Check(bool ok,string name) { if(!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
 static void Reject(Action a,string name) { try { a(); } catch(Exception) { Check(true,name); return; } throw new Exception("Accepted: "+name); }
 static SavedEvaluation Eval(ProgressService s,int a,int b,string round) {
  string id=s.State.Current.PatientId;
  s.CaptureAi(id,"case_"+id,round,"saved-A-snapshot","evaluated");
  return new SavedEvaluation { Identity=new RoundIdentity { ProgressId=s.State.ProgressId, PatientId=id, CaseId="case_"+id, RoundId=round }, AiSaveCompleted=true, Phase="evaluated",PatientScore=a,CounselorScore=b };
 }
 static void Main() {
  string root=Path.Combine(Path.GetTempPath(),"b-progress-check-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
  string path=Path.Combine(root,"run.json"); var store=new GameProgressStore(path); var s=new ProgressService(store); var c=new FixedReward();
  s.Create(new[]{"a","b","c","d","e"}); var firstId=s.State.ProgressId;
  s.BeginVisit(); Check(new ProgressService(store).State.Phase==ProgressPhase.Consulting,"restart during consultation");
  s.CaptureAi("a","case_a","r1","snapshot","pending_evaluation"); Check(new ProgressService(store).State.Phase==ProgressPhase.Evaluating,"restart during evaluation");
  Reject(()=>s.Depart(),"pending evaluation cannot depart");
  var e=Eval(s,90,60,"r1"); var before=s.State.Revision;
  var wrong=GameProgress.Copy(e); wrong.Identity.ProgressId="another-run"; Reject(()=>s.Settle(wrong,c),"foreign progress rejected");
  wrong=GameProgress.Copy(e); wrong.Identity.RoundId="stale-round"; Reject(()=>s.Settle(wrong,c),"stale round rejected");
  wrong=GameProgress.Copy(e); wrong.PatientScore=101; Reject(()=>s.Settle(wrong,c),"out of range score rejected");
  Reject(()=>s.Settle(e,null),"missing C is pending"); Check(s.State.Revision==before,"missing C does not mutate");
  e.AiSaveCompleted=false; Reject(()=>s.Settle(e,c),"unsaved evaluation rejected"); e.AiSaveCompleted=true;
  var r=s.Settle(e,c,"photo_a"); Check(!r.Cleared && s.State.ClearedCount==0 && s.State.Photos.Count==0,"90/60 is not clear");
  s.Settle(e,c,"photo_a"); Check(c.Calls==1 && s.State.Economy.Balance==17,"duplicate settlement once");
  s.Depart(); Check(s.State.CurrentIndex==0 && s.State.Phase==ProgressPhase.Waiting,"same patient waits for door");
  s.BeginVisit(); e=Eval(s,70,70,"r2"); Check(!s.State.Current.NeedsRevisit,"new round clears revisit intent"); store.BeforeReplace=()=>throw new IOException("injected disk failure");
  before=s.State.Revision; Reject(()=>s.Settle(e,c,"photo_a"),"save failure"); Check(s.State.Revision==before && s.State.ClearedCount==0 && store.Load().ClearedCount==0,"failed transaction leaves all state unchanged");
  store.BeforeReplace=null; r=s.Settle(e,c,"photo_a");
  Check(s.State.ClearedCount==1 && s.State.Photos.Count==1,"70/70 clears and unlocks photo");
  s=new ProgressService(store); Check(s.State.Phase==ProgressPhase.Presenting && s.State.PendingPresentation.Identity.Key==r.Identity.Key,"restart during presentation");
  var coins=s.State.Economy.Balance; s.Settle(e,c,"photo_a"); Check(s.State.Economy.Balance==coins,"replayed presentation has no reward");
  Reject(()=>s.Depart(),"presentation blocks departure"); s.CompletePresentation(r.Identity.Key); s.Depart(); Check(s.State.CurrentIndex==1,"clear advances on departure");
  for(int i=1;i<5;i++) { s.BeginVisit(); e=Eval(s,70,70,"r1"); r=s.Settle(e,c,"photo_"+i); if(i<4) Check(!s.State.FinalEndingUnlocked,"fewer than five stays locked "+i); s.CompletePresentation(r.Identity.Key); s.Depart(); }
  Check(s.State.ClearedCount==5 && s.State.FinalEndingUnlocked && s.State.Phase==ProgressPhase.Finished,"five DISTINCT patients unlock ending"); s.CompleteFinalEnding(); Check(new ProgressService(store).State.FinalEndingPlayed,"ending play state persists");
  var next=new ProgressService(new GameProgressStore(Path.Combine(root,"second.json"))); next.Create(new[]{"a","b","c","d","e"}); Check(next.State.ProgressId!=firstId && store.Load().ClearedCount==5,"new run preserves previous run and history");
  Reject(()=>new ProgressService(new GameProgressStore(Path.Combine(root,"bad-roster.json"))).Create(new[]{"a","a","b","c","d","e"}),"duplicate roster rejected");
  var detached=next.State; detached.Economy.Balance=999; Check(next.State.Economy.Balance==0,"read state cannot mutate service");
  var eco=next.State.Economy; eco.Balance=42; eco.OwnedItems.Add("test-hat"); eco.EquippedItems["hat"]="test-hat";
  eco.Ledger=Gamsamo.Economy.EconomyState.NewRun(next.State.ProgressId);
  eco.Ledger.balance=42; eco.Ledger.totalEarned=43;
  eco.Ledger.settlements.Add(new Gamsamo.Economy.SessionSettlement{patientId="a",caseId="fixture",sessionId="fixture",baseThreshold=43,baseCoins=43});
  eco.Ledger.ownedItemIds.Add("test-hat"); eco.Ledger.purchases.Add(new Gamsamo.Economy.PurchaseReceipt{itemId="test-hat",price=1});
  eco.Ledger.equippedItems.Add(new Gamsamo.Economy.EquippedItem{categoryId="hat",itemId="test-hat"});
  next.CommitEconomy(eco,next.State.Revision); var reloaded=new ProgressService(new GameProgressStore(Path.Combine(root,"second.json")));
  Check(reloaded.State.Economy.Balance==42 && reloaded.State.Economy.EquippedItems["hat"]=="test-hat","economic ownership and equipment persist together");
  Reject(()=>next.CommitEconomy(eco,0),"stale economic transaction rejected");
  File.WriteAllText(path,"{broken"); Reject(()=>new ProgressService(store),"corrupt save surfaced"); Check(File.ReadAllText(path)=="{broken" && File.Exists(path+".bak"),"corrupt save not overwritten; backup retained");
  Console.WriteLine("PASS "+checks+" checks; fixtures: "+root);
 }
}
