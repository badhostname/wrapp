using System.Threading.Tasks;
using Wrapp.Models;

namespace Wrapp.Services.Gates;

/// <summary>
/// Blocking gate: a versioned "use at your own risk" liability waiver + license
/// acknowledgement. Rendered through the shared <see cref="HelpMarkdownRenderer"/>
/// (the same themed Markdown formatting used by every help dialog) and shown as
/// a modal at startup; the user must accept to proceed (declining exits the
/// app). The accepted version is persisted, so it appears once and then not
/// again -- until <see cref="RequiredVersion"/> is bumped (a waiver-text
/// change), which forces every user to re-accept after their next update.
/// <para>
/// To re-test the prompt after accepting once, either bump
/// <see cref="RequiredVersion"/> or remove the <c>"liability-waiver"</c> entry
/// from <c>GateState</c> in <c>settings.json</c>.
/// </para>
/// </summary>
public sealed class LiabilityWaiverGate : IAppGate
{
    /// <summary>
    /// Current required waiver version. 0 = disabled (gate never fires). Bump
    /// whenever <see cref="WaiverMarkdown"/> changes to force previously-accepted
    /// users to re-accept after updating.
    /// </summary>
    public const int RequiredVersion = 5;

    public string Id => "liability-waiver";
    public GateKind Kind => GateKind.Blocking;
    public string Title => "Accept terms of use";

    public bool IsPending(AppSettings settings)
        => RequiredVersion > 0 && GateState.GetInt(settings, Id) < RequiredVersion;

    public async Task<bool> ResolveAsync(AppSettings settings)
    {
        // Render + show via the shared help dialog path (Markdown -> themed
        // formatting, bounded gentle-scroll viewport). Falls back to raw text if
        // the main window isn't available to resolve theme brushes (shouldn't
        // happen at startup, but keeps this UI-safe).
        var source = System.Windows.Application.Current?.MainWindow;
        var accepted = source is not null
            ? await FluentDialog.ConfirmMarkdownAsync(
                "Terms of Use & License -- Please Read", WaiverMarkdown, source, "I Accept", "Exit")
            : await FluentDialog.ShowSelectAsync(
                "Terms of Use & License -- Please Read", WaiverMarkdown, "I Accept", "Exit");
        if (!accepted) return false;

        GateState.SetInt(settings, Id, RequiredVersion);
        AppLogger.Info($"Gate 'liability-waiver': accepted waiver version {RequiredVersion}");
        return true;
    }

    // Markdown, rendered by HelpMarkdownRenderer. Keep in sync with the
    // repository LICENSE file; any change is a waiver-text change -- bump
    // RequiredVersion so users re-accept.
    private const string WaiverMarkdown =
        """
        **SUMMARY** -- Wrapp is free, open-source software provided "as is", with no
        warranty. It packages applications and uploads them to Microsoft Intune and
        Configuration Manager (SCCM), changing those environments directly. You are
        responsible for reviewing what it does and for the consequences. **You use it
        entirely at your own risk.** Wrapp is MIT-licensed; the full license and
        third-party notices are below.

        ---

        ## 1. No warranty

        Wrapp is provided "as is" and "as available", without warranty of any kind,
        express or implied, including but not limited to the warranties of
        merchantability, fitness for a particular purpose, title, and non-infringement.
        The authors and contributors do not warrant that Wrapp is error-free or secure,
        that it will operate without interruption, or that it will produce correct or
        complete results.

        ## 2. What Wrapp does

        Wrapp authors application packages and uploads and deploys them to Microsoft
        Intune via the Microsoft Graph API and to Microsoft Configuration Manager
        (SCCM). It creates and modifies applications, assignments, detection rules, and
        deployments; runs PowerShell against your tenants and sites; stores and
        retrieves content-encryption keys (including in a Git repository you configure
        as a key vault); and launches external tools (for example git, robocopy,
        AzCopy, and IntuneWinAppUtil). These operations make real, sometimes
        irreversible, changes to your environment.

        ## 3. Your responsibilities

        You are solely responsible for: reviewing Wrapp's actions before deploying;
        validating the correctness of any packaged application; maintaining backups;
        securing the credentials, tokens, secrets, and encryption keys Wrapp handles;
        configuring appropriate access controls on any Azure DevOps repository used as a
        key vault; and complying with the terms of the third-party services you direct
        Wrapp to use.

        ## 4. Credentials, secrets, and data

        Wrapp handles authentication tokens, client secrets, and content-encryption keys
        on your machine (protected at rest via Windows DPAPI) and in the services you
        configure. It does not transmit your data to the authors. You are responsible
        for the security of your workstation, your directory, your Intune and SCCM
        environments, and any vault repository.

        ## 5. Third-party services

        Wrapp interacts with Microsoft Graph, Microsoft Entra ID, Microsoft Intune,
        Microsoft Configuration Manager, and Azure DevOps. Your use of those services is
        governed by your agreements with their providers. Wrapp is an independent,
        community project and is not affiliated with, endorsed by, or sponsored by
        Microsoft Corporation.

        ## 6. Limitation of liability

        To the maximum extent permitted by applicable law, in no event shall the authors
        or copyright holders be liable for any claim, damages, or other liability --
        including without limitation direct, indirect, incidental, special,
        consequential, or exemplary damages, loss of data, loss of profits, business
        interruption, misconfiguration, service disruption, or security incidents --
        whether in an action of contract, tort, or otherwise, arising from, out of, or
        in connection with Wrapp or its use or other dealings in Wrapp, even if advised
        of the possibility of such damages.

        ## 7. Acceptance

        By clicking **I Accept** you acknowledge that you have read and agree to these
        terms and that you use Wrapp entirely at your own risk. If you do not agree,
        click **Exit** and do not use Wrapp.

        ---

        ## License

        **MIT License**

        Copyright (c) 2026 badhostname

        Permission is hereby granted, free of charge, to any person obtaining a copy of
        this software and associated documentation files (the "Software"), to deal in
        the Software without restriction, including without limitation the rights to
        use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
        the Software, and to permit persons to whom the Software is furnished to do so,
        subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
        FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
        COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN
        AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
        WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

        ---

        ## Third-party components

        Wrapp bundles and builds against third-party components, each under its own
        license, redistributed as part of the build output. Their licenses must be
        preserved in any redistribution.

        | Component | License |
        |-----------|---------|
        | Microsoft.Identity.Client (MSAL.NET) | MIT |
        | CommunityToolkit.Mvvm | MIT |
        | MaterialDesignThemes | MIT |
        | WPF-UI | MIT |
        | Microsoft.Web.WebView2 | Microsoft SDK EULA |
        | Microsoft.PowerShell.SDK | MIT |
        | Markdig.Signed (via PowerShell.SDK) | BSD-2-Clause |
        | Monaco Editor (vendored) | MIT |
        | Git-Windows-Minimal | GPL-2.0 |
        | IntuneWin32App (vendored) | MIT |
        | PSAppDeployToolkit (vendored) | LGPL-2.1 |

        Downstream redistributors are responsible for complying with each component's
        license terms. GPL-2.0 (git) and LGPL-2.1 (PSADT) impose additional obligations
        beyond the MIT license of Wrapp itself.
        """;
}
