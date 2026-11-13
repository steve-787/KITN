namespace KSAAutopilot
{
    internal enum LandingState { Idle, Planning, WaitingForReturn, DeorbitBurn, Entry, PoweredDescent, Landed, Aborted }

    internal struct LandingTelemetry
    {
        public LandingState State;
        public bool Active, TerrainContact, MissionClockRunning;
        public string Pad, Reason;
        public double Latitude, Longitude, PadDistance, PredictedMiss, TimeUntilBurn;
        public double RemainingDeltaV, AvailableDeltaV, RequiredDeltaV, Clearance, VerticalSpeed, HorizontalSpeed;
        public double RequestedThrottle, CommandedThrottle, PointingError, TargetPitch, StoppingHeight, DesiredVerticalSpeed;
        public double DragArea, MissionElapsed;
        public double AlignmentHeight, TimeToTouchdown;
        public LandingVector CommandAcceleration;
        public void Append(TelemetryRecord record)
        {
            record.Section("LANDING");
            record.Add("landing_state", State.ToString());
            record.Add("landing_active", Active);
            record.Add("landing_pad", Pad);
            record.Add("landing_reason", Reason);
            record.Add("landing_pad_latitude_deg", Latitude);
            record.Add("landing_pad_longitude_deg", Longitude);
            record.Add("landing_pad_distance_m", PadDistance);
            record.Add("landing_predicted_miss_m", PredictedMiss);
            record.Add("landing_time_to_burn_s", TimeUntilBurn);
            record.Add("landing_remaining_delta_v_m_s", RemainingDeltaV);
            record.Add("landing_available_delta_v_m_s", AvailableDeltaV);
            record.Add("landing_required_delta_v_m_s", RequiredDeltaV);
            record.Add("landing_clearance_m", Clearance);
            record.Add("landing_vertical_speed_m_s", VerticalSpeed);
            record.Add("landing_horizontal_speed_m_s", HorizontalSpeed);
            record.Add("landing_requested_throttle", RequestedThrottle);
            record.Add("landing_commanded_throttle", CommandedThrottle);
            record.Add("landing_pointing_error_deg", PointingError);
            record.Add("landing_target_pitch_deg", TargetPitch);
            record.Add("landing_stopping_height_m", StoppingHeight);
            record.Add("landing_desired_vertical_speed_m_s", DesiredVerticalSpeed);
            record.Add("landing_alignment_clearance_m", AlignmentHeight);
            record.Add("landing_descent_time_to_go_s", TimeToTouchdown);
            record.Add("landing_command_accel_cci_x_m_s2", CommandAcceleration.X);
            record.Add("landing_command_accel_cci_y_m_s2", CommandAcceleration.Y);
            record.Add("landing_command_accel_cci_z_m_s2", CommandAcceleration.Z);
            record.Add("landing_terrain_contact", TerrainContact);
            record.Add("landing_drag_area_estimate_m2", DragArea);
            record.Add("mission_elapsed_s", MissionElapsed);
            record.Add("mission_clock_running", MissionClockRunning);
        }
    }
}
