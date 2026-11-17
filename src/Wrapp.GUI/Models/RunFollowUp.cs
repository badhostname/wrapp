using System.Collections;
using System.Management.Automation;
using System.Text;

namespace Wrapp.Models;

/// <summary>
/// Something a failed run step left behind that a technician has to clean up:
/// an app half-created in Intune, an SCCM application without its deployment
/// type, an extraction folder that could not be removed. Emitted by the module
/// (<c>Write-WrappFollowUp</c>, <c>_Type = 'WrappFollowUp'</c>), shown in the
/// Run log and app.log, and kept in settings until the technician marks it
/// done, so it survives closing Wrapp (see <see cref="Services.Gates.RunFollowUpGate"/>).
/// </summary>
public sealed class RunFollowUp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset RaisedAt { get; set; }
    /// <summary>Intune or SCCM.</summary>
    public string Target { get; set; } = string.Empty;
    /// <summary>Tenant ID or site code.</summary>
    public string TargetId { get; set; } = string.Empty;
    public string Package { get; set; } = string.Empty;
    /// <summary>The run step that failed: AppCreation, Dependencies, Supersedence, Assignment.</summary>
    public string Step { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public List<string> Actions { get; set; } = new();
    /// <summary>The bundle the run was for, so the notice says which one.</summary>
    public string Bundle { get; set; } = string.Empty;

    /// <summary>The step as the Run view names it.</summary>
    public string StepLabel => Step switch
    {
        "AppCreation" => "App creation",
        "Dependencies" => "Dependencies",
        "Supersedence" => "Supersedence",
        "Assignment" => "Assignment",
        _ => Step,
    };

    /// <summary>A module follow-up object, or null when <paramref name="obj"/> is something else.</summary>
    public static RunFollowUp? FromPSObject(PSObject obj, DateTimeOffset raisedAt, string bundle)
    {
        if (obj.Properties["_Type"]?.Value?.ToString() != "WrappFollowUp") return null;
        var actions = new List<string>();
        var raw = obj.Properties["Actions"]?.Value;
        if (raw is PSObject wrapped) raw = wrapped.BaseObject;
        if (raw is string single) actions.Add(single);
        else if (raw is IEnumerable many)
            foreach (var a in many)
                if (a?.ToString() is { Length: > 0 } s) actions.Add(s);

        return new RunFollowUp
        {
            RaisedAt = raisedAt,
            Target   = obj.Properties["Target"]?.Value?.ToString() ?? string.Empty,
            TargetId = obj.Properties["TargetId"]?.Value?.ToString() ?? string.Empty,
            Package  = obj.Properties["Package"]?.Value?.ToString() ?? string.Empty,
            Step     = obj.Properties["Step"]?.Value?.ToString() ?? string.Empty,
            Summary  = obj.Properties["Summary"]?.Value?.ToString() ?? string.Empty,
            Reason   = obj.Properties["Reason"]?.Value?.ToString() ?? string.Empty,
            Actions  = actions,
            Bundle   = bundle,
        };
    }

    /// <summary>The lines the Run log shows for this notice.</summary>
    public IEnumerable<string> LogLines()
    {
        yield return $"[ACTION NEEDED] {Package} ({Target}): the run failed at step '{StepLabel}'. {Summary}";
        if (Reason.Length > 0) yield return $"[ACTION NEEDED]   Reason: {Reason}";
        foreach (var a in Actions) yield return $"[ACTION NEEDED]   - {a}";
    }

    /// <summary>All open notices as the markdown the action-needed dialog shows.</summary>
    public static string ToMarkdown(IReadOnlyList<RunFollowUp> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine(items.Count == 1
            ? "A packaging run failed part-way and left something to clean up."
            : $"{items.Count} packaging runs failed part-way and left something to clean up.");
        sb.AppendLine();
        foreach (var f in items)
        {
            var where = f.TargetId.Length > 0 ? $"{f.Target} {f.TargetId}" : f.Target;
            sb.AppendLine($"## {f.Package} - {where}");
            sb.AppendLine();
            sb.AppendLine($"Failed at step **{f.StepLabel}** on {f.RaisedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
                          (f.Bundle.Length > 0 ? $" (bundle `{f.Bundle}`)." : "."));
            sb.AppendLine();
            sb.AppendLine(f.Summary);
            if (f.Reason.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Reason: {f.Reason}");
            }
            sb.AppendLine();
            sb.AppendLine("Likely needed:");
            sb.AppendLine();
            foreach (var a in f.Actions) sb.AppendLine($"- {a}");
            sb.AppendLine();
        }
        sb.AppendLine("The Run log and app.log have the same details. Mark these done once you have cleaned up; they stay in this list until then, even if Wrapp is closed.");
        return sb.ToString();
    }
}
