// Dependencies not exercised by RuntimeGuardChecks. This is NOT a Unity runtime test.
using Gamsamo.Progression;
namespace Gamsamo.UI
{
    public class PcStub { public ShopStub shopView; public void BindEconomy(Gamsamo.Economy.EconomyWallet value) { } }
    public class ShopStub { public object catalog; }
}
namespace Gamsamo.UI.Presentation
{
    public class RoomStatusView : UnityEngine.Component { public void Bind(GameProgressRuntime value) { } }
}
namespace Gamsamo.Economy
{
    public class EconomyWallet
    {
        public EconomyWallet(string id, object store, object catalog) { }
        public void Reload() { }
    }
}
namespace Gamsamo.Progression
{
    public class EconomyIntegration : IRewardCalculator
    {
        public EconomyIntegration(ProgressService value) { }
        public RewardCalculation Calculate(SavedEvaluation e, EconomyState s, bool first) => throw new System.NotSupportedException();
    }
    public class GamePresentationIntegration : UnityEngine.Component
    {
        public void Bind(GameProgressRuntime r, Gamsamo.Dialogue.PatientVisitController v, Gamsamo.Networking.PatientAiAdapter a) { }
        public void BeginResultSave() { }
        public void CompleteResultSave() { }
        public void FailResultSave(string message) { }
    }
}
