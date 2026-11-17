# Wrapp Changelog

All notable changes to Wrapp are documented here. Versioning follows
SemVer2 (`MAJOR.MINOR.PATCH`); the update pipeline orders releases by this
version.

---

## [1.0.15] - 2026-11-17

### Changed

- **A bundle is found from any path inside it.** Open, the Splash screen
  and drag-drop accept the bundle folder, any folder inside it (the B\
  folder, a driver sub-folder) or the Config.json itself; Wrapp looks for
  `Script\Config.json` there and in the folders above. The module does the
  same: `Resolve-WrappBundle` is new, and `-ConfigPath` on
  `Invoke-WrappPackaging`, `Invoke-WrappIntune` and `Invoke-WrappSccm`
  accepts the root, the Config.json or any inside path. `Add-CMAppFromConfig`
  now requires `-PackagePath` (the bundle root); its fallback that rebuilt
  the content location from `Domain.isDistPath\AppFolder\Version` is gone.
  `Domain.AppFolder` is written per domain from the real path: the bundle's
  parent folder relative to that domain's `isDistPath` when the bundle is
  under it, so `isDistPath\AppFolder\<version folder>` is the bundle whatever
  the depth; otherwise the two folders above, as before. The contract is in
  `docs/bundle-layout.md`.
- **One save dialog, and the upgrade dialog says what it will do.** Saving
  a new bundle, Save As, and the Save as… of an upgrade or a full installer
  change all open the same sheet: a location (Browse only, starting at the
  last place used on this computer or the organisation's default), a choice
  between creating the folders from the naming convention or saving
  directly into the location for a folder you made yourself, the exact
  folder it will save to, and warnings when that folder already holds a
  bundle, sits inside one, is a share root or is not empty. The relative
  folders can be edited for one save. Fields the convention needs are
  required only in convention mode. The upgrade dialog's "Save Alongside"
  is now "Create the new version folder", showing the sibling folder named
  after the scraped version (spaces rule applied) and a note when the
  current folder is not named after the old version; the full-apply
  dialog's options read "Replace in place" and "Save as…". New settings
  `BundleSaveMode` and `BundleOutputRoot` sit in Settings › Bundle, the
  policy catalog, the ADMX/ADML, the sample policy JSON and a new `Bundle`
  block of the organisation defaults file (which also carries
  `DirectoryFormat` and `FolderNameSpaces`); the last location used is kept
  per machine and left out of exports.
- **Where the `.intunewin` goes is a setting, and each bundle can
  override it.** Settings › Bundle › Intune Package Output: `BesideBundle`
  (the default: an `Output` folder next to the version folder, so
  `…\PCL6\1.0.0.0` writes to `…\PCL6\Output`, outside the bundle) or
  `Central` under a browsed root, in a sub-folder format with the Directory
  Format tokens (blank = directly in the root) and its own spaces rule. The
  Intune view gains an Output card above the package list: it always shows
  what Settings resolve to for this bundle, and "Custom folder for this
  bundle" takes a browsed folder, nested under the Settings format or used
  directly, with the resulting path shown and a warning when the share is
  not reachable from this computer. The module wraps one file per run that
  every package shares, so this is per bundle, not per package. At every
  save Wrapp writes the final folder into `Config.json`
  (`Script.IntunePackager.Output`; beside-the-bundle is the relative
  `..\Output`, which travels with the bundle), and that is the only thing
  the run reads, so a CLI run lands in the same place without any settings
  on that machine. Bundles saved earlier fall back to the tenant's old
  `IntuneWinPath`, then to beside the bundle; the preflight warns about an
  unreachable root before anything is uploaded. Four new settings in the
  policy catalog, ADMX/ADML, the sample policy JSON and the organisation
  defaults file.
- **Bundle notices.** When a bundle is opened or saved, Wrapp looks at
  its real folder and its Config.json and lists what they disagree on at
  the top of the General view, with a count on the General nav item: a
  folder named after another version ("Set version from folder"), a
  folder outside the naming convention when you save by the convention
  ("Copy to the conventional folder", which leaves the current one as it
  is), an older layout the next save migrates, the `.intunewin` output
  folder changing on the next save, and an output folder whose share is
  not reachable from this computer ("Open the Output card"). Nothing is
  stored to produce or dismiss them, so a bundle moved in Explorer is
  simply looked at again.

- **A fresh install updates from Wrapp's GitHub releases.** The update feed
  used to default to empty, which turned update checks off until someone
  set a feed in Settings > Updates or by policy. It now defaults to
  `https://github.com/badhostname/wrapp`, and the update mode to `Auto`
  (as before). The feed still needs its one-time approval on each machine
  before Wrapp contacts it; a machine-mandated feed policy still skips that
  prompt. Settings you already saved are untouched - an empty feed stays
  empty - and Reset to defaults now restores the GitHub feed. Clear the
  field to turn update checks off.

### Fixed

- **A failed run says what it left behind, and where to clean up.** When a
  step fails after something was created (an app registered in Intune
  without its content, an app whose dependencies, supersedence or
  assignments did not complete, an SCCM application without its deployment
  type, an extraction folder that could not be removed), the run now writes
  ACTION NEEDED lines to the live Run log, the run's log file and app.log:
  the step that failed, the reason, and what is likely needed, with the
  app's ID and the paths. The same notice goes into the status bar's
  "action needed" list, where it stays until you choose Mark done, even
  after Wrapp is closed. Wrapp never deletes anything in Intune or
  Configuration Manager itself. CLI runs get the notices in the result's
  `FollowUps` list.
- **A failed upload of a new Intune app is retried onto that same app.**
  When the upload fails after the app was registered, the run reads the
  app back by the ID Intune returned when it was created and retries only
  the content, up to two more times (30 s, then 60 s), and only while the
  app still has that name and no content. It never creates a second app.
  If the content turns out to have landed after all, the run carries on.
  The attempts and the wait are in the module's Defaults.psd1.

- **The collision check before an Intune run works again.** Since
  0.6.313-beta the check Wrapp runs before starting set up only part of the
  sign-in the IntuneWin32App library expects. The library answered
  "Authentication token was not found" and listed no apps, Wrapp read that
  as "no collisions", and the run was told to skip its own check, so an
  existing app with the same name was never caught. The check now has the
  full sign-in, and the module's collision check refuses to run without one
  instead of reporting nothing, so the run falls back to its own check.
- **A failed upload says why, and leaves no 400 MB folder behind.** When
  Intune refused an upload, the IntuneWin32App library reported the reason
  as a warning and returned nothing; Wrapp kept only "App was not created in
  Intune". Its warnings and errors now go into the run log and into the
  failed step's message. The library unpacks the upload into an `Expand_…`
  folder beside the `.intunewin` and deletes it only when the upload
  succeeds; Wrapp now removes it after a failed one too. A failed content
  upload in Update mode is now reported as a failure; it used to log
  "Package file updated" and count the app as deployed.

- **Replacing the icon now counts as an unsaved change.** Wrapp decided
  "unsaved" by comparing the Config.json it would write with the one last
  opened or saved, plus the scripts in the editor. The icon image is not in
  Config.json, only its file name, so Extract Icon Only on a bundle that
  already had `Icon\<App>.png` changed nothing it looked at: the bar kept
  saying "Up to date" and Ctrl+S had nothing to save. The icon on screen is
  now compared with the one last opened or saved (by its pixels), so a new
  icon turns the bar to "Save Changes", puts the `*` after the path in the
  status bar and enables Ctrl+S; putting the same picture back clears it.
  The status line after Extract Icon Only also says the icon is not saved
  yet and which file Save writes.

- **The Intune and SCCM target dropdowns follow the bundle's tenants and
  sites.** The list of tenants (Intune) or sites (SCCM) a package can target
  was rebuilt only when you selected a different package. Removing,
  renaming or adding a tenant or site, or syncing them from Preferences,
  left the open package offering the old list. The dropdowns now update as
  the bundle's lists change.

- **An icon picked from an MSI is saved again.** Choosing one of an MSI's
  embedded icons on a drop showed it in the app, but no save ever wrote it:
  the icon was decoded on a background thread and stayed tied to that
  thread, so encoding it at save failed ("a different thread owns it"), and
  the log still said the icon was written. The `Icon` folder was left empty
  while Config.json and the Intune package pointed at `Icon\<App>.png`, so
  "Use app icon" found nothing. Icons are now copied free of their decoder
  before they reach the UI, the writer copies whatever it is given as well,
  and a failed icon write is logged as a failure and no longer points
  Config.json at a file that does not exist.
- **The Intune Output card reads cleanly.** The middle dots showed as "Â·"
  (the same double-encoding hit an ellipsis in the Settings and help text
  and in one code comment), the Settings button sat oversized in the middle
  of a sentence, and the custom-folder choices left empty rows when no
  folder was chosen. The card now uses one line per choice with its folder
  underneath, a Settings link in the sentence, and previews that appear
  only once a folder is browsed.

- **Save As no longer fails with "file in use", and keeps every folder
  under B\.** Save, Save As, Upgrade and Full apply now share one save
  pipeline: the bundle tree is copied first, sub-folders included, and
  Config.json, the scripts, the shortcuts and the icon are each written
  once; a script the editor holds is rewritten only when its text changed.
  Writing the same file twice within a second was what an on-access
  scanner or the share turned into "file in use". A file something still
  holds is retried for about a second and a half; one that still refuses
  is listed at the end with Retry, and the save completes. A directory
  format that would put the bundle outside the folder you chose (a rooted
  path, an empty folder name, `..`) is refused with a message instead of
  saving somewhere else. The Settings help now says the folder preview uses
  sample values, which it always did.

- **Wheel and touchpad scrolling is gentle in lists and dropdowns too.**
  A dropdown list (the detection Property list, every other dropdown)
  moved a fixed step for each wheel event however small, so a touchpad
  swipe made it jump. Dropdown lists, the Intune and SCCM package lists,
  the app picker, file history, the registry browser and the account
  details now scroll in proportion to the swipe, as the pages do. The
  Logs, Run-log, Git history and Inventory lists were already meant to
  but never did. A table with a height limit (the detection tests)
  now scrolls its own rows before the page; the wheel used to skip them.
- **Scroll bars stay usable on long lists.** On a table or list of
  thousands of rows the scroll bar thumb shrank to a dot that was hard to
  catch. The thumb now has a floor of 32 px everywhere in the app, as in
  AlwaysBlue and Unwrapped; dragging still maps the track onto the whole
  list, and the arrow buttons keep their size.
- **Job clocks tick on the second and only while you are looking.** The
  Background Jobs pop-up's elapsed times used to be refreshed twice a
  second from a timer that ran whenever a job was active, showed tenths
  that jumped by half-seconds, and never updated a running step's time.
  A job now shows whole seconds while it runs ("12s", "1m 05s") and
  tenths once it has finished ("4.3s"); running steps in a packaging
  run's tree tick with it; a finished card says how long ago it ended,
  with the exact time on hover; and the clock runs only while the pop-up
  is open, timed to the next change. Ported from AlwaysBlue and Unwrapped.
- **Script and packager tags follow the app name.** The Install,
  Uninstall, Console, IntunePackager and SCCMPackager tags name the
  endpoint `.TFR` logs, the central-share log folder and the packager run
  logs. Wrapp generated them once (`{Name}_{Version}_{Section}`) and then
  kept them as written, so renaming the app, a Save As with a new name, or
  dropping in a new installer left them on the old name (only the version
  part was updated, on upgrade). A tag that still reads as generated now
  follows the current name and version on every save, like the bundle
  folder and icon name do. A tag edited by hand is kept as written. After a
  rename, new logs are written under the new name; earlier logs stay where
  they are. The Console tag now also follows a version upgrade.

### Added

- **Keyboard shortcuts.** Ctrl+S saves the bundle from any view, including
  the script editor; on the Settings page it saves the settings first, then
  the bundle if it has changes. Ctrl+Alt+S is Save As and never touches
  Settings. Ctrl+O opens a bundle and Ctrl+N starts a new one, with the
  usual unsaved-changes prompt. Each shortcut runs the command its button
  runs and respects the same rule, so nothing is saved that the button
  would not save. The toolbar tooltips and the General help list them.

---

## [1.0.14] - 2026-10-22

### Added

- **The Detection view warns when the tests and the expressions disagree.**
  Before, a bundle could ship tests that no expression used, or an
  expression whose symbols had no tests, and nothing said so until
  detection misbehaved on an endpoint. Now, as you type:
  - Tests exist but an expression is blank: *An expression must be entered*
    beside the Boolean Expressions header, and the blank expression is
    outlined in amber. A blank named expression silently falls back to
    Default at run time, so it is flagged too.
  - An expression has content but there are no tests: *At least one test
    must be added* beside the Detection Tests header, and the table is
    outlined.
  - A test has no symbol: *A test needs a symbol*, and its Symbol cell is
    outlined.
  - A test's symbol appears in no expression, so it never runs: *Not used
    in any expression: C, D*, and the Symbol cells are outlined. This
    matches the detect script: symbols are case-sensitive and must stand
    alone, so `B` inside `Base` does not count.

  These are warnings: the bundle still saves and runs. Duplicate symbols
  stay a red error, and wins over a warning on the same cell. The Detection
  item in the navigation now has an amber warning count beside its red
  error count, like Intune and SCCM, and the Detection help explains each
  check.

### Changed

- **Theme files are plain `.json`, checked against a theme template.** A
  custom theme no longer needs the `.wrapptheme.json` extension: any `.json`
  file is a theme when it follows the template - `Name`, `BaseTheme`
  (`Dark`/`Light`), `MonacoTheme` (`vs` light, `vs-dark`), `Colors` and
  `ShadowOpacity`, plus an optional `$schema` for your editor. Anything else
  is rejected by name: a misspelt property (`"Colours"`) used to be ignored,
  so the theme imported and changed nothing; an editor theme other than
  `vs`/`vs-dark` used to fall back to dark silently. **Settings > General >
  Save template** writes a complete theme to start from - every property and
  every color of the theme selected there. Importing a theme replaces an
  earlier copy of the same name, so a re-import does not leave two. Existing
  `*.wrapptheme.json` files keep working (they are `.json` files), and so
  does a `ThemeFilePath` policy that points at one.

### Fixed

- **A detection test's Value can be edited again.** Since the August change
  that puts the caret where you click in every table, the Value column
  could not enter edit mode, so it kept whatever Add Test or Browse put
  there (usually `True`). It is now an always-on field like the other
  columns. A new check stops any table with click-to-edit cells from
  having a column that cannot be edited.
- **The Path lock follows the Command.** Typing a Command locks Path, but
  the lock outline and icon on the Path cell never updated: they kept the
  state the row was first drawn with, so a test loaded with a Command
  stayed marked as locked after the Command was cleared, and a new one
  never showed the lock. The change notification was being compiled away
  because the test has a property named `Property`, whose generated
  "changed" hook shares the name of the notification method. Fixed, with a
  check that catches the same trap in any other class.

---

## [1.0.13] - 2026-09-29

### Fixed

- **The update screen explains the administrator prompt and the "Installing
  Update" window.** On a Wrapp installed for all users (under Program
  Files), the updater cannot write to the install folder, so it re-launches
  itself as administrator. Wrapp asks the updater to work silently, but the
  elevated copy is started without that flag, so it shows Velopack's own
  "Installing Update - Updating to version X, please wait..." window, and
  because that window defines no buttons Windows gives it a default OK
  button. Clicking OK only closes the window: the update carries on and
  Wrapp reopens by itself. Wrapp now says exactly that - on the update
  screen, in the "Update Wrapp" prompt, and in the Install tooltip - when
  the install folder is not writable, and logs it when the apply is
  scheduled. Help > Settings > Updates explains the per-machine case and
  the per-user alternative.
- **Appease Install and Uninstall shortcuts work for a non-admin user.** The
  shortcuts ran `powershell.exe -ExecutionPolicy Bypass -File
  "..\Script\InstallScript.ps1"`. When Appease elevates, it relaunches
  PowerShell with the same arguments, rewriting only the one that starts
  with `&` to the script's full path. The elevated window starts in
  System32, so the relative `-File` path pointed at nothing. The shortcuts
  now run `-noexit -ExecutionPolicy Bypass -command
  "&..\Script\InstallScript.ps1"` (and `UninstallScript.ps1`), the form
  Appease expects, and the window stays open to show the outcome. Saving a
  bundle replaces a shortcut that still carries the old arguments exactly;
  a shortcut you have edited is left alone.
- **The date and time picker's calendar follows the app theme.** It decided
  whether the app was dark by checking the code editors' theme. The two
  always matched under Dark and Light, but a custom theme that pairs a dark
  app with light editors (`"BaseTheme": "Dark"`, `"MonacoTheme": "vs"`)
  opened a light calendar inside a dark window.

---

## [1.0.12] - 2026-09-29

### Fixed

**The problem.** An SCCM run created the application, then failed its
deployment type with "The computer running the Configuration Manager
console does not have Read permissions to the share folder, or the share
does not exist." The run was not pointing at the bundle. It rebuilt a
content path from the domain's distribution root, an app folder recomposed
from the display name, and the version, and the app folder kept the
display name's spaces while the application tree, and the bundle itself,
used underscores. The path it named did not exist.

- **Runs use the open bundle's folder as the content.** The SCCM deployment
  type points at it, the `.intunewin` is built from it, and the icon is read
  from it. Nothing is recomposed from the domain settings any more.
- **SCCM preflight checks the content location first.** A bundle folder that
  is missing, unreadable, or not on a network share fails by name before the
  application is created, instead of leaving a half-made application behind.
- The domain `AppFolder` recorded at save is now the bundle's real
  `Vendor\App` folder names, so it describes where the bundle is.

### Added

- **Templates on the DetectScript.ps1 tab.** The Scripts editor's Template
  dropdown now works on the detection script under both frameworks. Your own
  detect templates save and list there like the install and uninstall ones.
- **Each script dropdown starts with the framework's current file.** The
  Detect, Install and Uninstall lists now lead with `DetectScript.ps1`,
  `InstallScript.ps1` and `UninstallScript.ps1` as shipped with this version
  of Wrapp, labelled with their revision where they carry one. A bundle that
  lacks one of these files, or carries an older revision, can be brought up
  to date from the editor in one step. They are read from the shipped
  framework folder, so they always match what a new bundle would get, and
  they cannot be overwritten or deleted from the picker.
- **Settings > Bundle Output > Spaces in Folder Names.** Choose whether a
  space in the app name, or any other token value, is written into the
  bundle folder and the generated icon file name as a space or as an
  underscore, so a new bundle lands beside its earlier versions in a tree
  that uses either convention. The default keeps spaces, as before.
  Administrable as the `FolderNameSpaces` policy.

### Changed

- **The list of placeholder names lives in one place.** Settings &gt;
  Placeholders already shows every built-in name with the value it resolves to
  for the open bundle, plus your custom ones. The hand-written copies of that
  list are gone: the table in the template-token reference, and the
  twelve-token strips under the Intune and SCCM metadata editors, now point at
  the Placeholders tab. Those copies could not stay honest and already omitted
  custom placeholders entirely. Help still names individual placeholders as
  field examples, and a test fails the build if any help text or Settings
  label starts listing the whole set again.

---

## [1.0.11] - 2026-09-21

### Fixed

**The problem.** After 1.0.10 the SCCM inventory worked from a console-only
workstation, but the Run view still showed the site as "Not reachable", and
a run only packages to sites shown as connected. The Run view never
connected: it read the site code the Configuration Manager console had
recorded in the registry and marked a configured site connected only when
that code matched. The console records a site code only once it has
completed a connection under the same Windows account, so on that
workstation nothing ever matched.

- **The Run view connects to each configured site** the way a run will:
  import the module, find or build the site drive against the SMS Provider,
  and ask the provider for the site. A card is "Connected" only when the
  provider answers, and it shows the server it answered from.
- When a site cannot be reached, the card's error shows the reason and the
  remedy, the same message the inventory view gives.

---

## [1.0.10] - 2026-09-21

### Fixed

**The problem.** On a workstation with only the Configuration Manager
console installed, the SCCM inventory query failed with "Cannot find drive
'SITE'" and a packaging run stopped at "No SCCM site detected". Wrapp
waited for the ConfigurationManager module to create the site drive, which
it only does when the console has completed a connection to that site under
the same Windows account. When it had not, the inventory fallback built the
drive against the local machine, which is only correct on the site server
itself, and hid the failure.

- **SCCM inventory** builds the site drive against the SMS Provider server
  the console knows about: its recent connections first, then the server
  recorded when the console was installed.
- **SCCM packaging runs** connect the same way, for the site code the run
  was given, so a console connected to a different site no longer redirects
  the run.
- When the drive still cannot be created, the message names the site, the
  server tried and what to do: open the console as this account and connect
  once, and check the account is an administrative user in the site.
- A missing `SMS_ADMIN_UI_PATH` now says to restart Wrapp after installing
  the console, since a running process keeps the environment it started
  with.
- The ConfigMgr connection card shows the installer-recorded server for an
  account that has never opened the console.

---

## [1.0.9] - 2026-09-15

### Fixed

- **The account button signs in anywhere again and discovers the tenant.**
  1.0.8 pinned every sign-in to the configured tenant, including the
  account button's, which takes the open bundle's first tenant only for
  its client settings. On a fresh machine whose bundle listed a tenant the
  user is not a member of, the Windows sign-in opened for that tenant and
  could only be cancelled. Tenant binding is now explicit: the account
  button signs in unpinned and adopts the tenant the token comes back
  from, as before 1.0.8; a packaging run, a tenant card's sign-in, a
  tenant's test connection and an inventory query stay pinned to their
  tenant and refuse a token for any other, which is what closed the
  "Connected with another tenant's token" hole.

### Changed

- The account help says where the tenant comes from: the account you pick,
  not the bundle.

---

## [1.0.8] - 2026-09-15

### Fixed

- **Tenants no longer show "Connected" with another tenant's token.** The
  sign-in client uses the multi-tenant authority, so a token request for a
  tenant the cached account does not belong to was answered from the
  account's home tenant, and the result was labelled with the requested
  tenant. Every configured tenant then showed Connected with the same
  account and expiry, and a run would have injected the wrong tenant's
  token. Requests are now pinned to the configured tenant, a token that
  comes back for a different tenant is refused, the run marks that tenant
  as failed, and a tenant card only reads Connected for a token issued to
  that tenant.
- **The Detection expression box shows a field tooltip, not the whole
  guide.** Its hover text pointed at the section's help document, so the
  entire guide - headings, worked example and table - popped up over the
  field. The box now has a one-line tooltip, and a test keeps every
  section or view guide out of tooltips.
---

## [1.0.7] - 2026-09-15

### Fixed

- **Open dropdowns no longer float away when the page scrolls.** While a
  dropdown list is open, WPF captures the mouse for it, so a wheel anywhere
  outside the list reported the field itself as the source and the 0.6.321
  guard mistook it for a wheel over the list. The page then scrolled under
  the open popup, which cannot follow, in Intune and SCCM package editors
  and in table cells. Now a wheel over the list scrolls only the list, even
  when the list is too short to scroll; a wheel anywhere else closes the
  list and scrolls the page in the same motion; and any other scroll of the
  page the field sits in (scrollbar, keyboard, touch) closes the list too.
- **Script History crashed after a draft's first save.** When a new bundle
  was saved before the Scripts view had ever been opened, the view kept the
  temp workspace as its script folder, and that folder is deleted by the
  save. Clicking History then tried to initialise git there and threw. The
  scripts view now reads the bundle's live location instead of a copy, and
  History reports a missing folder instead of crashing.

---

## [1.0.6] - 2026-09-15

### Fixed

- **Intune assignments were skipped with "GUID mismatch" on every new app.**
  Since the per-package Notes field arrived, the packager forwarded the raw
  package note as an override on top of the tracking JSON it had just built
  from it. An empty note was stripped as blank, so the app was created with
  no Notes at all; a typed note shipped as plain text. Either way the
  assignment step could not find the bundle GUID in the app and stopped.
  Notes (and the orchestrator's IsEnabled flag, which logged a spurious
  "not a recognized parameter" warning) are now handled keys, and a lint
  test keeps every package key the function reads out of the override
  path. Apps created by 0.6.329 through 1.0.5 carry no tracking Notes;
  re-publishing them in update mode writes the JSON.
- **Closing with nothing to save could leave the window unclosable.** When
  the close guard had no dirty scopes and no jobs it completed
  synchronously, and the confirmed close re-entered the original Closing
  dispatch and threw. The close now yields once before it runs.
- **Job cards no longer risk a cross-thread crash.** Registering,
  completing and failing a background job, and adding a fact to its
  details card, now marshal to the UI thread themselves instead of
  relying on every caller to do it; a job finishing on a worker thread
  while the jobs popup had the card open could throw.
- **Help paragraphs that start with a button token** (`[btn:Save]`)
  render as written; the legacy "Label: value" preprocessor no longer
  bolds the token's prefix.
- **Long view titles trim instead of pushing the header's buttons off the
  edge.** The view header lays out its right-hand content first and gives
  the title what is left.

### Changed

- A XAML lint test now catches the scroll helper being attached to
  anything but a ScrollViewer, where it is a silent no-op.

---

## [1.0.5] - 2026-08-27

### Fixed

- The sidebar toggle points its arrow the way the rail will move, and shares
  a nav row's box, so it sits on the same left rule as the icons when
  expanded and the same centre line when collapsed.

---

## [1.0.4] - 2026-08-27

### Added

- **GitHub release feeds.** The update feed URL now accepts a GitHub
  repository (`https://github.com/owner/repo`) alongside UNC shares, local
  folders, and static HTTPS directories. GitHub releases are not a browsable
  directory, so these feeds resolve through the releases API. The repository
  must be public: no access token is sent, and Wrapp does not store secrets
  for the update path.

### Changed

- Navigation rows are a fixed height with larger icons and labels, and the
  error/warning badges are drawn as an overlay, so a row no longer changes
  size when a count appears or clears. Collapsed rows centre their icon with
  the badge stack riding its edge. The sidebar toggle shows an arrow that
  points the way it will move.
- View headers are a uniform height, so titles sit on the same baseline
  whether or not a view has header buttons.

### Fixed

- Release notes embedded in the update manifest no longer mangle non-ASCII
  characters (the changelog was read with the wrong encoding at pack time).
- Help popups, the background-jobs list, and the commit history scroll with
  the same gentle wheel step as the rest of the app.
- Removed a no-op handler that re-applied an already-set brush for every code
  block rendered in a help popup.

---

## [1.0.3] - 2026-08-27

**First public release.** Wrapp is a Windows desktop app for building,
validating, and shipping Win32 application packages to Microsoft Intune and
SCCM/ConfigMgr - the whole flow from installer to deployed app in one tool.

### The packaging flow

- **Bundles**: every app lives in a bundle folder - metadata, scripts,
  detection, icon, and source files together, tracked by an embedded git
  repository (full history and diffs in-app, no git install required).
- **App info**: name, publisher, versions (dot and underscore forms with
  one-click conversion), categories, owner/developer fields, and
  end-user-visible fields labeled with where they surface (Company Portal /
  Software Center). Icons come from an in-app icon editor with a
  spectrum/opacity picker, an icon library, or file import.
- **Install/uninstall scripts**: generated from a template catalog (silent
  MSI, EXE with parameters, MSI+MSP patch, MSIX bundle, winget,
  uninstall-then-install variants) with token replacement, or authored
  directly in the built-in Monaco editor (the VS Code editor, fully
  offline). PSADT v4 bundles are a first-class alternative - the framework
  template ships in the box.
- **Detection**: PowerShell script, MSI product code, file, or registry
  detection, plus a detection-tag system for script-based state marking on
  endpoints.
- **Config JSON**: one JSON document per bundle drives the entire package -
  editable as a form or as raw JSON, always in sync.
- **Run**: end-to-end packaging from the GUI - `.intunewin` creation,
  upload to Intune via Microsoft Graph, app creation with detection,
  requirements, dependencies and supersedence, assignment creation; or SCCM
  application creation, content distribution, and deployments. Powered by
  the bundled **Wrapp.Packager** PowerShell module, which is fully
  CLI-capable for automation without the GUI.
- **Templates everywhere**: package templates, assignment templates, and
  deployment templates - sparse (only checked fields apply), hierarchical
  (a package template can carry its assignments/deployments), with
  placeholder expansion on import.
- **Validation badges**: errors (blocking) and warnings (amber,
  non-blocking) tracked from the navigation rail down through package rows,
  buttons, dialogs, and individual fields - including duplicate
  name/target detection across enabled packages.

### Views

- **General** - bundle picker and app metadata.
- **Intune** - per-tenant package configuration and assignments.
- **SCCM** - per-site package configuration and deployments.
- **Detection / Scripts / JSON** - detection rules, script editing
  (Monaco), and the raw config document.
- **Run** - the packaging pipeline with live output and background jobs
  (progress, per-job details, exportable facts).
- **Inventory** - a live catalog browser for deployed Win32 apps: full
  detail panes (program, requirements, detection, scope tags), assignments
  with **nested Entra ID group expansion** (a view the Intune console
  doesn't offer), dependency and supersedence graphs including the reverse
  direction ("depended on by" / "superseded by"), icon and `.intunewin`
  download, JSON export per app or for the **entire tenant catalog** in one
  run, and import-to-Wrapp / full clone of any deployed app.
- **Tools** - utilities including `.intunewin` decryption and inspection.
- **Logs** - live application log with filtering; CMTrace-compatible
  logging throughout the module.
- **Git History** - the bundle's commit timeline with double-click diffs.
- **Settings** - preferences, tenants, sites, domains, endpoints, Key
  Vault, updates, placeholders, provisioning.

### Enterprise readiness

- **Policy engine**: every setting is administrator-controllable via
  registry policy (`HKLM/HKCU\Software\Policies\Wrapp`) - delivered by
  Group Policy (ADMX/ADML templates included), Intune, or the offline
  `Apply-WrappPolicy.ps1` script for disconnected fleets. Mandated values
  lock their controls with a padlock and a "Managed by your organization"
  indicator; sections and tabs can be hidden outright. Policy changes are
  detected live and surface a restart-to-apply prompt.
- **Organization defaults**: a JSON seed file provisions tenants, sites,
  domains, placeholders, and preferences on first run; keyed lists merge so
  user-added entries survive.
- **Placeholders**: reusable `{{tokens}}` across scripts and fields, with
  per-user encrypted storage for sensitive values and redaction of secrets
  from every log line (extensible regex patterns).
- **Custom themes**: import `.wrapptheme.json` color overlays on the Dark
  or Light base - JSON data only, safe by construction.
- **Authentication**: Microsoft Entra ID via MSAL with Windows broker
  (WAM); client secrets are stored per-user with DPAPI encryption and are
  never provisionable via policy or seed files.
- **Key Vault**: optional publishing of package encryption keys to an
  Azure DevOps repository, with pull-request mode for protected branches.

### Distribution and updates

- **Velopack** update pipeline: delta updates (a few MB per release, with
  the full package rebuilt locally and hash-verified), full packages, a
  one-click Setup.exe, a portable ZIP, and a full MSI wizard build for
  technician/endpoint-management deployment. The update feed is a plain
  folder - UNC share, local path, or static https.

### Bundled components

.NET 8 (WPF), WPF-UI, MaterialDesignThemes, CommunityToolkit.Mvvm,
Microsoft.Identity.Client (+ broker), Microsoft PowerShell SDK 7.4,
WebView2 + Monaco editor (offline), Velopack, embedded MinGit, and the
vendored modules: **Wrapp.Packager** (ours), IntuneWin32App 1.5.0, the
PSADT v4 framework template, and the Appease packaging-environment
template. See `THIRD-PARTY-NOTICES.txt` for licenses.
