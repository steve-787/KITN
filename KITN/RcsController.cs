using System;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>Owns RCS preferences only while KITN is controlling a vehicle.</summary>
    internal sealed class RcsController
    {
        private FlightComputer _computer;
        private FlightComputerRCSMode _savedMode;
        private float _savedRate, _savedDeadband, _requestedRate, _requestedDeadband;
        private float _writtenRate, _writtenDeadband;

        public static bool Allowed(Vehicle vehicle, bool allowed)
        {
            if (!allowed || vehicle?.Parent == null) return false;
            return RcsSteering.EnvironmentPermitted(allowed, vehicle.Parent.Id,
                (vehicle.Parent as Celestial)?.BodyTemplate?.Id,
                vehicle.GetPositionCci().Length(), vehicle.Parent.GetAtmosphereRadius());
        }

        public void Update(Vehicle vehicle, bool active, bool allowed, bool transfer, bool docking = false)
        {
            FlightComputer fc = vehicle?.FlightComputer;
            if (!active || fc == null) { Restore(); return; }
            allowed = Allowed(vehicle, allowed);
            ApplyCore(fc, true, allowed, transfer || docking, allowed ? PointingError(vehicle, transfer) : 0, docking);
        }

        public void Apply(FlightComputer fc, bool active, bool allowed, bool transfer, double errorDeg)
            => ApplyCore(fc, active, allowed, transfer, errorDeg, false);

        private void ApplyCore(FlightComputer fc, bool active, bool allowed, bool transfer, double errorDeg, bool docking)
        {
            if (!active || fc == null) { Restore(); return; }
            if (!ReferenceEquals(fc, _computer))
            {
                Restore(); _computer = fc; _savedMode = fc.RCSMode;
                _savedRate = _requestedRate = _writtenRate = fc.RateLimit;
                _savedDeadband = _requestedDeadband = _writtenDeadband = fc.AngleDeadband;
            }
            // Guidance can change its limits every frame. Do not feed our own cap back into itself.
            if (fc.RateLimit != _writtenRate) _requestedRate = fc.RateLimit;
            if (fc.AngleDeadband != _writtenDeadband) _requestedDeadband = fc.AngleDeadband;
            bool permitted = RcsSteering.Permitted(allowed, transfer, errorDeg);
            fc.RCSMode = permitted ? FlightComputerRCSMode.Enabled : FlightComputerRCSMode.Disabled;
            fc.RateLimit = _requestedRate;
            fc.AngleDeadband = _requestedDeadband;
            if (permitted)
            {
                fc.AngleDeadband = docking ? _requestedDeadband
                    : transfer ? (float)(RcsSteering.DeadbandDeg * FlightAttitude.DEG_TO_RAD)
                    : Math.Max(fc.AngleDeadband, (float)(RcsSteering.DeadbandDeg * FlightAttitude.DEG_TO_RAD));
                // Powered pitch/yaw steering remains under TVC. RCS-only axes use slower turns.
                if (fc.TvcTorqueAuthority.Y <= 0 || fc.TvcTorqueAuthority.Z <= 0)
                {
                    double authority = double.PositiveInfinity;
                    void Consider(float value) { if (float.IsFinite(value) && value > 0) authority = Math.Min(authority, value); }
                    if (fc.RollMode != FlightComputerRollMode.Decoupled) Consider(fc.RcsTorqueAuthority.X);
                    Consider(fc.RcsTorqueAuthority.Y); Consider(fc.RcsTorqueAuthority.Z);
                    fc.RateLimit = (float)RcsSteering.RateLimit(_requestedRate, errorDeg, authority);
                }
            }
            _writtenRate = fc.RateLimit; _writtenDeadband = fc.AngleDeadband;
        }

        private static double PointingError(Vehicle vehicle, bool transfer)
        {
            FlightComputer fc = vehicle.FlightComputer;
            if (fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.None) return 0;
            doubleQuat? target = null;
            var nav = new FlightComputerNavigation
            {
                Parent = vehicle.Parent, PositionCci = vehicle.GetPositionCci(), VelocityCci = vehicle.GetVelocityCci(),
                Body2Cci = vehicle.GetBody2Cci(), BodyRates = vehicle.GetBodyRates(), Time = Universe.GetElapsedTime(),
                Helm2Body = doubleQuat.Identity, Target = null
            };
            if (transfer && fc.Burn != null) target = fc.Burn.BurnBody2Cci;
            else if (fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.Custom)
            {
                var frame = VehicleReferenceFrameEx.GetFrame2Cci(fc.AttitudeFrame, in nav, fc.Burn);
                if (frame.HasValue) target = doubleQuat.Concatenate(
                    VehicleReferenceFrameEx.EulerAnglesToQuaternion(fc.AttitudeFrame, fc.CustomAttitudeTarget), frame.Value);
            }
            else target = FlightComputerAttitudeTrackTargetEx.GetTarget(fc.AttitudeTrackTarget, in nav, fc.Burn)?.Target2Cci;
            if (!target.HasValue) return double.NaN;
            double error = FlightAttitude.Angle(FlightAttitude.Forward(FlightAttitude.ControlToCci(vehicle)), FlightAttitude.Forward(target.Value));
            // The native controller also brakes roll using the same inertia-based phase lines.
            if (fc.RollMode != FlightComputerRollMode.Decoupled)
                error = Math.Max(error, Math.Abs(fc.ErrorAngles.X) * FlightAttitude.RAD_TO_DEG);
            return error;
        }

        public void Restore()
        {
            if (_computer == null) return;
            _computer.RCSMode = _savedMode; _computer.RateLimit = _savedRate; _computer.AngleDeadband = _savedDeadband;
            _computer = null;
        }
    }
}
