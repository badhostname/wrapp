using System.Management.Automation;
using Microsoft.Identity.Client;

namespace Wrapp.Services;

/// <summary>
/// PowerShell cmdlet that performs a silent MSAL
/// token refresh on behalf of a PS script, without ever giving PS direct
/// access to the live <see cref="IPublicClientApplication"/>.
///
/// The cmdlet receives an opaque handle GUID produced by
/// <see cref="MsalRefreshRegistry.Register"/>; it looks up the registered
/// IPCA / IAccount / scopes tuple and calls <c>AcquireTokenSilent</c> with
/// <c>WithForceRefresh(true)</c>. The result is emitted as a PSCustomObject
/// matching the shape PS callers already use (so existing
/// <c>$Global:AccessToken</c> / <c>$Global:AuthenticationHeader</c> update
/// blocks keep working).
///
/// Registered into the runspace by
/// <see cref="PowerShellService.CreatePool"/> via
/// <c>InitialSessionState.Commands.Add</c> so every runspace in the pool
/// sees the cmdlet without needing module import.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "WrappTokenRefresh")]
[OutputType(typeof(PSObject))]
public sealed class InvokeWrappTokenRefreshCommand : PSCmdlet
{
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string Handle { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        if (!Guid.TryParse(Handle, out var handleGuid))
        {
            WriteError(new ErrorRecord(
                new ArgumentException("Handle must be a GUID-shaped string."),
                "InvalidHandle",
                ErrorCategory.InvalidArgument,
                Handle));
            return;
        }

        if (!MsalRefreshRegistry.TryGet(handleGuid, out var ctx))
        {
            // No matching entry -- treat as "refresh capability not available"
            // so the PS caller can fall through to the next tier (CLI refresh
            // or interactive re-auth). Surface as a non-terminating error so
            // the pipeline doesn't blow up; the WARN stream is the caller's
            // signal to move on.
            WriteWarning("Invoke-WrappTokenRefresh: handle not registered (refresh capability not available).");
            return;
        }

        try
        {
            var authResult = ctx.App
                .AcquireTokenSilent(ctx.Scopes, ctx.Account)
                .WithForceRefresh(true)
                .ExecuteAsync()
                .GetAwaiter()
                .GetResult();

            if (authResult is null || string.IsNullOrEmpty(authResult.AccessToken))
            {
                WriteWarning("Invoke-WrappTokenRefresh: AcquireTokenSilent returned no token.");
                return;
            }

            var output = new PSObject();
            output.Properties.Add(new PSNoteProperty("AccessToken", authResult.AccessToken));
            output.Properties.Add(new PSNoteProperty("ExpiresOn",   authResult.ExpiresOn));
            output.Properties.Add(new PSNoteProperty("Scopes",      authResult.Scopes?.ToArray() ?? Array.Empty<string>()));
            output.Properties.Add(new PSNoteProperty("TenantId",    authResult.TenantId));
            WriteObject(output);
        }
        catch (MsalUiRequiredException ex)
        {
            // Silent refresh failed because the user needs to re-auth. Not a
            // bug -- the caller falls back to interactive. Surface via the
            // warning stream so the PS log records the reason without
            // leaking the message details to the error stream.
            WriteWarning($"Invoke-WrappTokenRefresh: silent refresh requires UI ({ex.ErrorCode}).");
        }
        catch (Exception ex)
        {
            WriteError(new ErrorRecord(
                ex,
                "TokenRefreshFailed",
                ErrorCategory.AuthenticationError,
                Handle));
        }
    }
}
