using System;
using System.Collections.Generic;

namespace KITN
{
    /// <summary>Message meaning is independent of its translated display text.</summary>
    internal static class MessageSeverity
    {
        private static readonly HashSet<string> Errors = new HashSet<string>(StringComparer.Ordinal)
        {
            "planning.phase_failed", "planning.invalid_settings", "planning.no_computer", "planning.no_steering",
            "planning.no_engines", "planning.no_physics", "planning.invalid_performance", "planning.low_twr",
            "planning.low_fuel", "planning.target_in_atmosphere", "planning.too_many_phases", "planning.manual_stage",
            "planning.fuel_exhausted", "planning.no_route", "planning.calculation_failed", "planning.start_blocked",
            "preview.no_stages", "preview.orbit_invalid", "preview.insufficient_performance", "preview.ends_early",
            "preview.unstable", "preview.fuel_exhausted", "preview.manual_stage", "preview.unavailable", "preview.no_performance",
            "status.failed", "status.invalid_orbit", "status.no_computer", "status.no_thrust", "status.reentry", "status.attitude_failed",
            "status.insertion_failed", "status.trim_failed", "status.no_hook", "display.unavailable",
            "plan.countdown_invalid", "plan.orbit_invalid", "plan.turn_invalid", "preset.load_failed", "preset.save_failed",
            "settings.load_failed", "settings.save_failed",
            "transfer.unsafe_orbit", "transfer.soi_orbit", "transfer.unsafe_arrival", "transfer.invalid_periapsis",
            "transfer.arrival_surface", "transfer.arrival_atmosphere", "transfer.arrival_soi", "transfer.rejected_window", "transfer.returning",
            "transfer.impact", "transfer.no_encounter", "transfer.missing_soi_dv", "transfer.missing_soi_dv_unknown",
            "transfer.no_safe_route", "transfer.missed_burn", "transfer.warp_interrupted", "transfer.capture_unsafe",
            "transfer.capture_missed", "transfer.failed", "transfer.invalid_bounds", "transfer.no_solution", "transfer.no_early",
            "transfer.stale", "transfer.invalid_cell", "transfer.oberth_no_window",
            "transfer.hohmann_geometry", "transfer.hohmann_no_solution",
            "transfer.assist_missed", "transfer.assist_unsafe", "transfer.route_hierarchy",
            "landing.invalid_settings", "landing.require_control", "landing.require_pad", "landing.require_fuel",
            "landing.require_thrust", "landing.fuel_exhausted", "landing.no_window", "landing.attitude_failed",
            "landing.failed", "landing.off_target",
            "dock.retry_lateral", "dock.retry_pointing", "dock.retry_roll", "dock.retry_rotation", "dock.retry_unsafe", "dock.stopped_reason", "dock.connection_failed",
            "docking.require_roll_rcs", "rendezvous.too_fast", "rendezvous.incompatible_ports", "rendezvous.require_rcs", "rendezvous.require_ports",
            "rendezvous.require_orbit", "rendezvous.require_engines", "rendezvous.no_route", "rendezvous.target_changed",
            "rendezvous.no_engines", "rendezvous.engines_inactive", "rendezvous.require_restartable", "rendezvous.no_fuel",
            "rendezvous.fuel_data", "rendezvous.engine_data", "rendezvous.insufficient_dv",
            "rendezvous.body_change", "rendezvous.impact", "rendezvous.target_collision", "rendezvous.arrival_miss",
            "rendezvous.target_clearance", "rendezvous.failed_detail",
            "rendezvous.unsafe_route", "rendezvous.no_prediction", "rendezvous.burn_interrupted", "rendezvous.missed_burn", "rendezvous.failed"
        };

        public static bool IsError(string key) => key != null && Errors.Contains(key);
    }
}
