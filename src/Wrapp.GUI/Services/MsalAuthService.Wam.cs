using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Wrapp.Models;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Wrapp.Services;

public sealed partial class MsalAuthService
{
    // -----------------------------------------------------------------------
    // WAM (Windows Account Manager) interactive flow plumbing -- request
    // builder, broker fallback heuristics. Used by the Interactive flow.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Executes an interactive token request via WAM broker, falling back
    /// to system browser if WAM is unavailable (DLL missing, runtime error).
    /// Serialized via _interactiveLock to prevent concurrent WAM requests
    /// (WAM crashes with ApiContractViolation if two run simultaneously).
    /// </summary>
    private async Task<AuthenticationResult> ExecuteInteractiveAsync(
        IAccount? loginHintAccount, Prompt prompt)
    {
        if (!await _interactiveLock.WaitAsync(0))
        {
            // Another interactive request is already in progress -- don't stack
            throw new OperationCanceledException(
                "Another sign-in is already in progress. Please wait for it to complete.");
        }
        try
        {
            var app = await EnsurePublicClientAsync();
            try
            {
                return await BuildInteractiveRequest(app, loginHintAccount, prompt)
                    .ExecuteAsync();
            }
            catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
            {
                // WAM throws MsalClientException (not OperationCanceledException) when the
                // user closes the dialog. Convert so callers can catch OperationCanceledException.
                throw new OperationCanceledException("User cancelled authentication.", ex);
            }
            catch (MsalException ex) when (IsWamUnavailableError(ex))
            {
                AppLogger.Warn($"WAM broker unavailable ({ex.ErrorCode}), falling back to system browser.");
                StatusChanged?.Invoke("Opening browser for sign-in...");

                var fallback = await EnsureBrowserFallbackAsync();
                try
                {
                    return await BuildInteractiveRequest(fallback, loginHintAccount, prompt)
                        .WithUseEmbeddedWebView(false)
                        .ExecuteAsync();
                }
                catch (MsalClientException ex2) when (ex2.ErrorCode == "authentication_canceled")
                {
                    throw new OperationCanceledException("User cancelled authentication.", ex2);
                }
            }
        }
        finally
        {
            _interactiveLock.Release();
        }
    }

    private AcquireTokenInteractiveParameterBuilder BuildInteractiveRequest(
        IPublicClientApplication app, IAccount? loginHintAccount, Prompt prompt)
    {
        // Prefer the dynamic func resolved at request time: the PCA was
        // configured with it because _parentWindow is captured
        // at InitializeAsync and can be stale (user moved focus, window
        // minimised/re-shown) - a stale HWND causes WAM to parent to a
        // destroyed window, which manifests as a crash or an auth prompt
        // hidden behind other windows.
        var parent = _parentWindowFunc?.Invoke() ?? _parentWindow;
        var builder = app.AcquireTokenInteractive(DelegatedScopes)
            .WithParentActivityOrWindow(parent)
            .WithPrompt(prompt);

        if (loginHintAccount is not null)
            builder = builder.WithLoginHint(loginHintAccount.Username);

        return builder;
    }

    private static bool IsWamUnavailableError(MsalException ex)
    {
        var code = ex.ErrorCode ?? string.Empty;
        return code == "wam_runtime_init_failed"
            || code == "unknown_broker_error"
            || code == "wam_not_supported"
            || code == "no_broker_installed";
    }

}
