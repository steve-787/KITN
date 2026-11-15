using System;

namespace KITN
{
    internal sealed partial class RendezvousController
    {
        public bool IsAutoDocking => IsActive && !_matching && !_rendezvousRunning;
        public DockingGuidancePlan DockingPreview { get; } = new DockingGuidancePlan();
        public double DockingRotationRemaining { get; private set; } = double.NaN;
        public double DockingAngularRate { get; private set; } = double.NaN;
        public double DockingClosingSpeed => Math.Abs(_relativeVelocity.X) < 0.00005 ? 0 : -_relativeVelocity.X;
        public LandingVector DockingRelativePosition => Vector(_relativePosition);
        public double DockingStandOff => _standOff;
        public double DockingCheckpointDistance => (DockingPreview.Checkpoint - Vector(_relativePosition)).Length;
        public string DockingCurrentStep => DockingOptions.EngineAssist && _engineAssistSuppressClosing
            ? DockingEngineStatus : StepText(DockingPreview.Current);
        public string DockingNextStep => StepText(DockingPreview.Next == DockingStep.None ? DockingStep.Alignment : DockingPreview.Next);
        public string DockingEta => DockingPreview.Current == DockingStep.Connecting ? L10n.Get("dock.eta_confirming") : double.IsFinite(DockingPreview.EstimatedSeconds)
            ? "~ " + TransferPlannerPage.Duration(DockingPreview.EstimatedSeconds) : L10n.Get("dock.eta_pending");
        public string DockingMotionSummary => double.IsFinite(Distance) && double.IsFinite(RelativeSpeed)
            ? L10n.Format("dock.compact_motion", Distance, RelativeSpeed) : "—";
        public string DockingRotationSummary => double.IsFinite(DockingRotationRemaining) && double.IsFinite(DockingAngularRate)
            ? L10n.Format("dock.compact_rotation", DockingRotationRemaining, DockingAngularRate) : "—";
        private double _dockingPreviewTime = double.NaN;

        private static string StepText(DockingStep step) => L10n.Get("dock.step." + step.ToString().ToLowerInvariant());

        private void UpdateDockingTelemetry(double now, DockingCommand command, double orientation, double rate, double angleWindow, double rateWindow)
        {
            DockingRotationRemaining = orientation; DockingAngularRate = rate;
            bool changed = DockingPreview.Current != DockingGuidancePlan.Step(command);
            if (!changed && double.IsFinite(_dockingPreviewTime) && now >= _dockingPreviewTime && now - _dockingPreviewTime < 0.2) return;
            _dockingPreviewTime = now;
            var authority = _activeVehicle.FlightComputer.RcsTorqueAuthority;
            double angularAcceleration = Math.Min(authority.X, Math.Min(authority.Y, authority.Z)) * FlightAttitude.RAD_TO_DEG;
            DockingPreview.Build(Vector(_relativePosition), Vector(_relativeVelocity), PointingError, orientation, rate,
                _acceleration, _standOff, _dockingSafeDistance, DockingOptions, _routingAround, _dockingRetrying, angleWindow, rateWindow, command, angularAcceleration);
        }

        private void ClearDockingTelemetry()
        {
            DockingPreview.Clear(); _dockingPreviewTime = double.NaN;
            DockingRotationRemaining = DockingAngularRate = double.NaN;
        }
    }
}
