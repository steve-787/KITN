using System;
using Brutal.Numerics;
using KSA;

namespace KITN
{
    /// <summary>Measure and command the thrust forward axis, independent of Navball Euler ordering.</summary>
    internal static class FlightAttitude
    {
        public const double RAD_TO_DEG = 180.0 / Math.PI;
        public const double DEG_TO_RAD = Math.PI / 180.0;

        public static doubleQuat ControlToCci(Vehicle vehicle)
        {
            var fc = vehicle.FlightComputer;
            return fc != null ? doubleQuat.Concatenate(fc.Ctrl2Asmb, vehicle.GetBody2Cci()) : vehicle.GetHelm2Cci();
        }

        public static double3 Forward(doubleQuat controlToCci)
        {
            if (!double.IsFinite(controlToCci.LengthSquared()) || controlToCci.LengthSquared() < 1e-12)
                throw new InvalidOperationException("Invalid vehicle control orientation");
            return double3.Transform(double3.UnitX, doubleQuat.Normalize(controlToCci));
        }

        public static double Pitch(double3 positionCci, double3 directionCci)
        {
            double denominator = positionCci.Length() * directionCci.Length();
            return denominator > 0.0 ? Math.Asin(Math.Clamp(double3.Dot(positionCci, directionCci) / denominator, -1.0, 1.0)) * RAD_TO_DEG : double.NaN;
        }

        public static double OrbitalThrustPitch(double3 positionCci, double3 directionCci, double3 tangentCci)
        {
            // Altitude pitch alone is zero for BOTH prograde and retrograde. The burn
            // predictor needs the signed tangential component to recognize a braking burn.
            return Math.Atan2(double3.Dot(directionCci, double3.Normalize(positionCci)),
                double3.Dot(directionCci, tangentCci)) * RAD_TO_DEG;
        }

        public static double Heading(double3 positionCci, double3 directionCci)
        {
            var enuToCci = VehicleReferenceFrameEx.GetEnu2Cci(positionCci);
            if (!enuToCci.HasValue) return double.NaN;
            double3 directionEnu = double3.Transform(directionCci, doubleQuat.Inverse(enuToCci.Value));
            if (Math.Sqrt(directionEnu.X * directionEnu.X + directionEnu.Y * directionEnu.Y) < 1e-6) return double.NaN;
            return (Math.Atan2(directionEnu.X, directionEnu.Y) * RAD_TO_DEG + 360.0) % 360.0;
        }

        public static double3 Direction(double3 positionCci, double pitchDeg, double headingDeg)
        {
            if (!double.IsFinite(pitchDeg) || !double.IsFinite(headingDeg))
                throw new InvalidOperationException("Invalid gravity-turn target angle");
            var enuToCci = VehicleReferenceFrameEx.GetEnu2Cci(positionCci);
            if (!enuToCci.HasValue) throw new InvalidOperationException("No local east/north/up frame for the vehicle position");
            double pitch = Math.Clamp(pitchDeg, 0.0, 90.0) * DEG_TO_RAD;
            double heading = headingDeg * DEG_TO_RAD;
            return double3.Transform(new double3(Math.Cos(pitch) * Math.Sin(heading),
                Math.Cos(pitch) * Math.Cos(heading), Math.Sin(pitch)), enuToCci.Value);
        }

        public static double Angle(double3 a, double3 b)
        {
            double denominator = a.Length() * b.Length();
            return denominator > 0.0 ? Math.Acos(Math.Clamp(double3.Dot(a, b) / denominator, -1.0, 1.0)) * RAD_TO_DEG : double.NaN;
        }

        public static double3 SlewDirection(double3 from, double3 to, doubleQuat current, double maxStepDeg)
        {
            double angle = Angle(from, to);
            if (angle < 1e-6 || maxStepDeg <= 0.0) return from;
            double3 axis = double3.Cross(from, to);
            if (axis.LengthSquared() < 1e-12)
            {
                axis = double3.Transform(double3.UnitY, current);
                axis -= from * double3.Dot(axis, from);
            }
            return double3.Normalize(double3.Transform(from, doubleQuat.CreateFromAxisAngle(
                double3.Normalize(axis), Math.Min(angle, maxStepDeg) * DEG_TO_RAD)));
        }

        public static doubleQuat AlignForward(doubleQuat currentControlToCci, double3 desiredForwardCci)
        {
            if (!double.IsFinite(desiredForwardCci.LengthSquared()) || desiredForwardCci.LengthSquared() < 1e-12)
                throw new InvalidOperationException("Invalid gravity-turn target direction");
            doubleQuat current = doubleQuat.Normalize(currentControlToCci);
            double3 from = Forward(current);
            double3 to = double3.Normalize(desiredForwardCci);
            double dot = Math.Clamp(double3.Dot(from, to), -1.0, 1.0);
            if (dot > 1.0 - 1e-12) return current;
            double3 axis = double3.Cross(from, to);
            if (axis.LengthSquared() < 1e-12) axis = double3.Transform(double3.UnitY, current);
            doubleQuat swing = doubleQuat.CreateFromAxisAngle(double3.Normalize(axis), Math.Acos(dot));
            return doubleQuat.Normalize(doubleQuat.Concatenate(current, swing));
        }

        public static void StopTracking(FlightComputer fc)
        {
            if (fc != null) fc.RateHold(VehicleReferenceFrame.EclBody);
        }

        public static double3 CustomAngles(double3 positionCci, doubleQuat targetControlToCci)
        {
            var enuBodyToCci = VehicleReferenceFrameEx.GetEnuBody2Cci(positionCci);
            if (!enuBodyToCci.HasValue) throw new InvalidOperationException("No local attitude reference frame");
            doubleQuat targetToFrame = doubleQuat.Concatenate(targetControlToCci, doubleQuat.Inverse(enuBodyToCci.Value));
            return VehicleReferenceFrameEx.QuaternionToEulerAngles(VehicleReferenceFrame.EnuBody, targetToFrame);
        }

        public static double3 InertialCustomAngles(Vehicle vehicle, doubleQuat targetControlToCci)
        {
            var nav = new FlightComputerNavigation
            {
                Parent = vehicle.Parent, PositionCci = vehicle.GetPositionCci(), VelocityCci = vehicle.GetVelocityCci(),
                Body2Cci = vehicle.GetBody2Cci(), Time = Universe.GetElapsedTime(),
                BodyRates = vehicle.GetBodyRates(), Helm2Body = doubleQuat.Identity, Target = null
            };
            var frameToCci = VehicleReferenceFrameEx.GetFrame2Cci(VehicleReferenceFrame.EclBody, in nav, null);
            if (!frameToCci.HasValue) throw new InvalidOperationException("No inertial attitude reference frame");
            doubleQuat targetToFrame = doubleQuat.Concatenate(targetControlToCci, doubleQuat.Inverse(frameToCci.Value));
            return VehicleReferenceFrameEx.QuaternionToEulerAngles(VehicleReferenceFrame.EclBody, targetToFrame);
        }
    }
}
