using System;
using System.Globalization;
using Wrapp.Models;

namespace Wrapp.Services.Gates;

/// <summary>
/// Typed accessors over the free-form <see cref="AppSettings.GateState"/>
/// dictionary. Each gate owns its keys; values are plain strings so a new gate
/// can persist whatever it needs (a version number, a bool, a timestamp)
/// without an <see cref="AppSettings"/> schema change. Keyed by gate
/// <see cref="IAppGate.Id"/> by convention.
/// </summary>
public static class GateState
{
    public static int GetInt(AppSettings settings, string key, int fallback = 0)
        => settings.GateState.TryGetValue(key, out var v)
           && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : fallback;

    public static void SetInt(AppSettings settings, string key, int value)
        => settings.GateState[key] = value.ToString(CultureInfo.InvariantCulture);

    public static bool GetBool(AppSettings settings, string key, bool fallback = false)
        => settings.GateState.TryGetValue(key, out var v)
            ? v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            : fallback;

    public static void SetBool(AppSettings settings, string key, bool value)
        => settings.GateState[key] = value ? "1" : "0";
}
