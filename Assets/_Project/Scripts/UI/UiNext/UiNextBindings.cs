using System;
using Gamsamo.UI.Presentation;
using UnityEngine;

namespace Gamsamo.UI.UiNext
{
    /// <summary>UI input wiring only. Integration must publish saved projections through ports.</summary>
    public sealed class UiNextBindings : MonoBehaviour
    {
        public ConsultationRoomController room;
        public UiFeaturePorts ports;
        public ProfileEditorView profileEditor;
        public EndingPresentationView replayPlayer;
        public UiStatusDetailsView statusDetails;
        IDisposable lease;
        void Awake()
        {
            profileEditor.BindModalGate(Acquire, Release);
            replayPlayer.BindModalGate(Acquire, Release);
            if (statusDetails != null) statusDetails.BindModalGate(AcquireStatus, Release);
        }
        bool AcquireStatus()
        {
            if (room == null || room.IsOverlayBlocking || lease != null) return false;
            lease = room.AcquireInputBlock(); return true;
        }
        bool Acquire()
        {
            if (room == null || !room.IsPcOpen || room.HasExternalInputBlock || lease != null) return false;
            lease = room.AcquireInputBlock(); return true;
        }
        void Release() { lease?.Dispose(); lease = null; }
        void OnDisable() { statusDetails?.Close(); profileEditor.Cancel(); replayPlayer.CloseReplay(); Release(); }
    }
}
