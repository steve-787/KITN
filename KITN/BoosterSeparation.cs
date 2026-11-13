using System;

namespace KITN
{
    internal readonly struct BoosterObservation
    {
        public readonly bool ConnectedRadially, ContainsControl, FuelDataValid;
        public readonly int ActivatedPropulsion, FuturePropulsion, FuelStores;
        public readonly double UsableFillFraction;

        public BoosterObservation(bool connectedRadially, bool containsControl, bool fuelDataValid,
            int activatedPropulsion, int futurePropulsion, int fuelStores, double usableFillFraction)
        {
            ConnectedRadially = connectedRadially; ContainsControl = containsControl; FuelDataValid = fuelDataValid;
            ActivatedPropulsion = activatedPropulsion; FuturePropulsion = futurePropulsion;
            FuelStores = fuelStores; UsableFillFraction = usableFillFraction;
        }
    }

    /// <summary>Per-booster confirmation. Neither core thrust nor a vehicle-wide fill level enters this decision.</summary>
    internal sealed class BoosterSeparation
    {
        internal const double EmptyFraction = 0.000001;
        internal const double ConfirmationSeconds = 0.5;
        private double _emptySince = double.NaN, _lastTime = double.NaN;
        public string Reason { get; private set; } = "not_evaluated";
        public double ConfirmationElapsed { get; private set; }

        public bool Update(in BoosterObservation observation, double simulationTime)
        {
            ConfirmationElapsed = 0;
            string blocked = !double.IsFinite(simulationTime) ? "invalid_simulation_time"
                : !observation.ConnectedRadially ? "no_radial_attachment"
                : observation.ContainsControl ? "branch_contains_control"
                : observation.ActivatedPropulsion == 0 ? "booster_not_ignited"
                : observation.FuturePropulsion > 0 ? "branch_has_unstarted_propulsion"
                : !observation.FuelDataValid || observation.FuelStores == 0
                    || !double.IsFinite(observation.UsableFillFraction) || observation.UsableFillFraction < 0
                    ? "booster_fuel_unavailable"
                : observation.UsableFillFraction > EmptyFraction ? "booster_has_usable_fuel" : null;
            if (double.IsFinite(_lastTime) && simulationTime < _lastTime) _emptySince = double.NaN;
            _lastTime = simulationTime;
            if (blocked != null)
            {
                _emptySince = double.NaN;
                Reason = blocked;
                return false;
            }
            if (double.IsNaN(_emptySince)) _emptySince = simulationTime;
            ConfirmationElapsed = simulationTime - _emptySince;
            bool confirmed = ConfirmationElapsed >= ConfirmationSeconds;
            Reason = confirmed ? "booster_local_propellant_depleted" : "confirming_booster_empty";
            return confirmed;
        }

        public void Reset()
        {
            _emptySince = _lastTime = double.NaN;
            ConfirmationElapsed = 0;
            Reason = "reset";
        }
    }
}
