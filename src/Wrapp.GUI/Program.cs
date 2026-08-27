using System;
using Velopack;

namespace Wrapp;

/// <summary>
/// Explicit entry point (csproj StartupObject). Workstream D: the Velopack
/// hook processor MUST run before any other app code — during install,
/// update, and uninstall, Update.exe relaunches the exe with --veloapp-*
/// arguments and <c>VelopackApp.Run()</c> performs the hook then exits the
/// process. Only a normal launch falls through into WPF's generated
/// <c>App.Main()</c>. Keep this method free of anything with side effects
/// (logging, mutexes, settings) so hook invocations stay instant and clean.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Perf-plan P2.3: Wrapp has no stylus/touch features, yet WPF routes
        // every mouse event through the WISP stylus stack — the code path 3 of
        // 4 captured UI-freeze stacks were parked in (tablet-input service
        // wedge). Disabling it removes that plumbing entirely; touch degrades
        // to standard mouse promotion. Must be set before any WPF type loads.
        AppContext.SetSwitch("Switch.System.Windows.Input.Stylus.DisableStylusAndTouchSupport", true);

        // AutoApplyOnStartup(false): Velopack's default applies a previously
        // staged package at launch using ITS OWN progress window — seen in
        // the field when an update was downloaded but the flow was cancelled
        // before the apply. Wrapp's splash update screen is the only updater
        // UI; a staged package is simply offered again there (its download
        // step completes instantly since the package is already local).
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .Run();
        App.Main();
    }
}
