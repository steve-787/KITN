using System;

namespace KITN
{
    internal enum DockingStep { None, Routing, Retreat, Alignment, Lateral, Damping, Approach, Contact, Connecting, Complete, Holding }

    /// <summary>Bounded forecast using the same waypoints, speeds and guards as live guidance.
    /// Target-relative coordinates follow the moving port. Future disturbances are not known.</summary>
    internal sealed class DockingGuidancePlan
    {
        public const int MaximumPoints = 96, MaximumSteps = 2048;
        private readonly LandingVector[] _raw = new LandingVector[MaximumSteps + 1];
        private readonly bool[] _corner = new bool[MaximumSteps + 1];
        public readonly LandingVector[] Points = new LandingVector[MaximumPoints];
        public int Count { get; private set; }
        public int PredictionSteps { get; private set; }
        public double EstimatedSeconds { get; private set; } = double.NaN;
        public LandingVector Checkpoint { get; private set; }
        public DockingStep Current { get; private set; }
        public DockingStep Next { get; private set; }
        public double SafeRadius { get; private set; }

        public void Clear()
        { Count = PredictionSteps = 0; EstimatedSeconds = double.NaN; Current = Next = DockingStep.None; }

        public void Build(LandingVector position, LandingVector velocity, double pointing, double orientation, double rate,
            double acceleration, double standOff, double safeRadius, DockingSettings settings, bool routing, bool retrying,
            double nativeAngle, double nativeRate, DockingCommand liveCommand, double turnAccelerationDegS2 = 0)
        {
            Clear();
            if (!position.IsFinite || !velocity.IsFinite || !double.IsFinite(acceleration) || acceleration <= 0
                || settings?.IsValid != true || liveCommand.Issue == DockingIssue.InvalidMotion) return;
            SafeRadius = Math.Max(5, safeRadius);
            Current = Step(liveCommand); Next = Following(Current);
            Checkpoint = liveCommand.Waypoint;
            var command = liveCommand;
            bool entryCleared = liveCommand.EntryCleared;
            double elapsed = 0;
            double predictedTurnRate = 0;
            int rawCount = 1;
            _raw[0] = position; _corner[0] = true;
            bool reached = false, checkpointFound = false;
            for (int i = 0; i < MaximumSteps; i++)
            {
                if (position.Length < 0.5 && velocity.Length <= settings.ContactSpeedMps + 0.01
                    && pointing <= settings.AlignmentToleranceDeg + Math.Max(0.5, Math.Min(5, nativeAngle)))
                { reached = true; break; }
                double dt = Math.Clamp(position.Length / Math.Max(0.01, settings.MaximumSpeedMps) / 200, 0.25, 60);
                if (position.X < 60) dt = Math.Min(dt, Math.Max(1, 0.2 / settings.NearSpeedMps));
                if (position.X < 12) dt = Math.Clamp(0.04 / Math.Max(0.01, Math.Min(settings.MaximumSpeedMps,
                    Math.Min(settings.NearSpeedMps, settings.ContactSpeedMps))), 0.25, 4);
                dt = Math.Min(dt, Math.Max(0.25, 0.25 * Math.Sqrt(Math.Max(0.1, (command.Waypoint - position).Length) / acceleration)));
                var previousStep = Step(command);
                var previousVelocity = velocity;
                velocity += (command.DesiredVelocity - velocity).Limited(acceleration * dt);
                position += (previousVelocity + velocity) * (dt * 0.5);
                elapsed += dt;
                // Include measured RCS angular acceleration and room to brake the turn.
                double turn = settings.MaximumTurnRateDegS * dt;
                if (double.IsFinite(turnAccelerationDegS2) && turnAccelerationDegS2 > 0)
                {
                    double remaining = Math.Max(pointing, orientation);
                    double cap = Math.Min(settings.MaximumTurnRateDegS, Math.Sqrt(turnAccelerationDegS2 * remaining));
                    double previousRate = predictedTurnRate;
                    predictedTurnRate += Math.Clamp(cap - predictedTurnRate, -turnAccelerationDegS2 * dt, turnAccelerationDegS2 * dt);
                    turn = (previousRate + predictedTurnRate) * dt * 0.5;
                }
                pointing = Math.Max(0, pointing - turn); orientation = Math.Max(0, orientation - turn);
                rate = turnAccelerationDegS2 > 0 ? Math.Max(0, rate - turnAccelerationDegS2 * dt) : rate * Math.Exp(-dt / 4);
                if (routing && position.X >= standOff - 0.5) routing = false;
                if (retrying && position.X >= standOff - 0.5 && Math.Abs(velocity.X) < 0.1) retrying = false;
                command = DockingMath.Approach(position, pointing, rate, acceleration, standOff, settings,
                    routing, safeRadius, orientation, true, nativeAngle, nativeRate, velocity, retrying, entryCleared);
                entryCleared = command.EntryCleared;
                if (!position.IsFinite || command.Issue == DockingIssue.InvalidMotion) break;
                var nextStep = Step(command);
                _raw[rawCount] = position; _corner[rawCount] = nextStep != previousStep;
                if (!checkpointFound && nextStep != Current && Current != DockingStep.Routing && Current != DockingStep.Retreat)
                { Checkpoint = position; checkpointFound = true; }
                rawCount++; PredictionSteps++;
            }
            if (reached) EstimatedSeconds = elapsed + 1; // Native docking confirmation takes another frame.
            // Keep phase transitions and evenly spaced samples, with no growing buffers.
            int stride = Math.Max(1, (rawCount + MaximumPoints - 8) / (MaximumPoints - 8));
            for (int i = 0; i < rawCount && Count < MaximumPoints - 2; i++)
                if (i == 0 || _corner[i] || i % stride == 0) Points[Count++] = _raw[i];
            Points[Count++] = _raw[rawCount - 1];
            if (reached && Count < MaximumPoints) Points[Count++] = default;
        }

        public void Connecting(LandingVector position)
        { Clear(); Current = DockingStep.Connecting; Next = DockingStep.Complete; Checkpoint = position; }

        public void Holding(LandingVector position)
        { Clear(); Current = DockingStep.Holding; Next = DockingStep.Alignment; Checkpoint = position; }

        public static DockingStep Step(DockingCommand command) => command.Phase == DockingPhase.Routing ? DockingStep.Routing
            : command.Phase == DockingPhase.Retreating ? DockingStep.Retreat
            : command.Issue == DockingIssue.Lateral ? DockingStep.Lateral
            : command.Issue == DockingIssue.Pointing || command.Issue == DockingIssue.Orientation ? DockingStep.Alignment
            : command.Issue == DockingIssue.AngularRate ? DockingStep.Damping
            : command.Phase == DockingPhase.Aligning ? DockingStep.Alignment
            : command.Phase == DockingPhase.Docking ? DockingStep.Contact : DockingStep.Approach;
        private static DockingStep Following(DockingStep step) => step == DockingStep.Routing || step == DockingStep.Retreat ? DockingStep.Alignment
            : step == DockingStep.Alignment || step == DockingStep.Lateral || step == DockingStep.Damping ? DockingStep.Approach
            : step == DockingStep.Approach ? DockingStep.Contact : DockingStep.Connecting;
    }
}
