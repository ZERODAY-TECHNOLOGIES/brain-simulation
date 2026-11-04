using BrainSim.Engine.Config;

namespace BrainSim.Engine.Circadian;

/// <summary>
/// Circadian clock, light and the simulated pineal gland (spec: "Ceasul circadian", "Glanda pineala").
/// The clock does not keep time itself: the caller passes the current time, real or simulated.
/// </summary>
public sealed class CircadianClock
{
    private readonly CircadianConfig _config;
    private DateTime _lightUntil = DateTime.MinValue;
    private bool _wasDark;
    private double _lightMinutesThisNight;

    public CircadianClock(CircadianConfig config) => _config = config;

    /// <summary>Accumulated delay from light at night, in hours. phi = clock time minus this.</summary>
    public double ShiftHours { get; private set; }

    /// <summary>Circadian phase phi in hours, 0 to 24.</summary>
    public double Phase(DateTime now) => Mod24(now.TimeOfDay.TotalHours - ShiftHours);

    /// <summary>Circadian alertness C(phi) = 0.5 cos(2 pi (phi - 18) / 24). Peak at 18:00, minimum at 06:00.</summary>
    public static double Alertness(double phi) => 0.5 * Math.Cos(2 * Math.PI * (phi - 18) / 24);

    public bool InDarkWindow(double phi) => HoursIntoDark(phi) < DarkLength;

    /// <summary>D(phi): 0 outside the dark window, 1 inside, with linear ramps at both ends.</summary>
    public double Darkness(double phi)
    {
        var h = HoursIntoDark(phi);
        if (h >= DarkLength)
        {
            return 0;
        }

        return Math.Min(1, Math.Min(h, DarkLength - h) / _config.RampHours);
    }

    /// <summary>L(t): 1 while a user message's light lasts, else 0.</summary>
    public double Light(DateTime now) => now < _lightUntil ? 1 : 0;

    /// <summary>A user message counts as light for light_minutes.</summary>
    public void OnLight(DateTime now) => _lightUntil = now.AddMinutes(_config.LightMinutes);

    /// <summary>p_MEL = p_max D(phi) (1 - L(t)), per tick.</summary>
    public double MelatoninProduction(DateTime now) =>
        _config.MelatoninProduction * Darkness(Phase(now)) * (1 - Light(now));

    /// <summary>
    /// CORT target from the clock alone: 1.0 by day, cortisol_night from dark start, then a linear rise
    /// from cortisol_rise_start back to 1.0 at dark end. The awakening peak is handled by the sleep logic.
    /// </summary>
    public double CortisolTarget(double phi)
    {
        if (!InDarkWindow(phi))
        {
            return 1.0;
        }

        var riseLength = Mod24(_config.DarkEnd - _config.CortisolRiseStart);
        var intoRise = Mod24(phi - _config.CortisolRiseStart);
        return intoRise < riseLength
            ? _config.CortisolNight + (1.0 - _config.CortisolNight) * intoRise / riseLength
            : _config.CortisolNight;
    }

    /// <summary>
    /// Tracks light during the dark window. When a window ends after repeated light, the clock is delayed
    /// by jet_lag_shift_minutes (spec: repeated light at night shifts the clock by 15 minutes per night).
    /// </summary>
    public void Advance(DateTime now, double elapsedSeconds)
    {
        var dark = InDarkWindow(Phase(now));
        if (dark && Light(now) > 0)
        {
            _lightMinutesThisNight += elapsedSeconds / 60;
        }

        if (_wasDark && !dark)
        {
            if (_lightMinutesThisNight >= _config.JetLagLightMinutes)
            {
                ShiftHours += _config.JetLagShiftMinutes / 60;
            }

            _lightMinutesThisNight = 0;
        }

        _wasDark = dark;
    }

    private double DarkLength => Mod24(_config.DarkEnd - _config.DarkStart);

    private double HoursIntoDark(double phi) => Mod24(phi - _config.DarkStart);

    private static double Mod24(double hours) => ((hours % 24) + 24) % 24;
}
