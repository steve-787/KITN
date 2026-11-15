using System;
using System.Reflection;
using Brutal.Numerics;
using KSA;

internal static partial class Program
{
    private static void RendezvousRateHoldChecks()
    {
        var fc = new FlightComputer
        {
            AttitudeMode = FlightComputerAttitudeMode.Auto,
            AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Custom,
            AttitudeFrame = VehicleReferenceFrame.EclBody,
            CustomAttitudeTarget = new double3(1.58, 0, 0.155),
            RollMode = FlightComputerRollMode.Decoupled
        };
        var attitude = typeof(FlightComputer).GetMethod("UpdateAttitudeTarget", BindingFlags.Instance | BindingFlags.NonPublic);
        var rateError = typeof(FlightComputer).GetMethod("UpdateAttitudeRateError", BindingFlags.Instance | BindingFlags.NonPublic);
        var rateAxis = typeof(FlightComputer).GetMethod("ComputeRcsRateAxis", BindingFlags.Static | BindingFlags.NonPublic);
        var stop = Mod.GetType("KITN.FlightAttitude").GetMethod("StopTracking");
        var parent = DispatchProxy.Create<IParentBody, PlannerBodyProxy>();
        var rotation = doubleQuat.CreateFromAxisAngle(double3.UnitY, 0.7);
        var nav = new FlightComputerNavigation { Parent = parent, PositionCci = new double3(7e6, 0, 0),
            VelocityCci = new double3(0, 7500, 0), Body2Cci = rotation, Helm2Body = doubleQuat.Identity,
            BodyRates = double3.Zero, Time = Universe.GetElapsedTime(), Target = null };

        // Reproduce KSA's old transition with the exact custom values from the live log.
        fc.RateHold(VehicleReferenceFrame.EclBody);
        attitude.Invoke(fc, new object[] { nav });
        Check(fc.AttitudeTarget.RatesCci.Length() > 1.5, "Fixture does not reproduce stale angles becoming a commanded spin");

        stop.Invoke(null, new object[] { fc });
        attitude.Invoke(fc, new object[] { nav });
        Check(fc.AttitudeTarget.RatesCci.Length() < 1e-10 && fc.AttitudeTrackTarget == FlightComputerAttitudeTrackTarget.None
            && fc.AttitudeFrame == VehicleReferenceFrame.EclBody, "RCS approach requests rotation instead of an inertial zero rate");

        double Impulse(double rate)
        {
            object[] args = { (float)rate, 0.0001f, 1000f, 0f }; rateAxis.Invoke(null, args);
            return (float)args[3];
        }
        double3 rates = new double3(1.57946, 0.0029, 0.155);
        for (int step = 0; step < 1000 && rates.Length() > 0.0002; step++)
        {
            rateError.Invoke(fc, new object[] { rotation, rates });
            var error = fc.ErrorRates;
            var command = new double3(Impulse(error.X), Impulse(error.Y), Impulse(error.Z));
            Check(command.X * rates.X <= 0 && command.Y * rates.Y <= 0 && command.Z * rates.Z <= 0,
                "Native RCS rate control accelerates an axis during approach");
            command.X = Math.Clamp(command.X, -2, 2); command.Y = Math.Clamp(command.Y, -2, 2); command.Z = Math.Clamp(command.Z, -2, 2);
            rates += command / 1000;
        }
        Check(rates.Length() < 0.0002, "RCS does not damp the logged existing rotation on every axis");
        rateError.Invoke(fc, new object[] { rotation, double3.Zero });
        Check(Impulse(fc.ErrorRates.X) == 0 && Impulse(fc.ErrorRates.Y) == 0 && Impulse(fc.ErrorRates.Z) == 0,
            "Settled approach continues consuming rotational RCS impulse");
        Console.WriteLine("PASS logged RCS approach spin: stale Euler values become zero commanded rate; native control brakes all axes and uses no rotational impulse at rest");
    }
}
