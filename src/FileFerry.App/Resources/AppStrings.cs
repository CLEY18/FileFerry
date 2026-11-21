using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace FileFerry.App.Resources;

/// <summary>
/// Localized UI strings: <c>AppStrings.resx</c> carries the neutral text, each locale
/// adds a satellite <c>AppStrings.&lt;culture&gt;.resx</c>. Key = property name; values are read
/// from <c>CurrentUICulture</c>, unknown cultures fall back to the neutral text.
/// </summary>
public static class AppStrings
{
    private static readonly ResourceManager Manager =
        new("FileFerry.App.Resources.AppStrings", typeof(AppStrings).Assembly);

    /// <summary>
    /// Product name in titles and the About mark — one value for every locale (a brand is not
    /// reworded), yet it still lives in resources: no UI string is ever hardcoded in XAML.
    /// </summary>
    public static string AppDisplayName => Get();

    /// <summary>
    /// Title of the pair list.
    /// </summary>
    public static string PairListTitle => Get();

    /// <summary>
    /// EmptyState of the pair list: why the list is empty.
    /// </summary>
    public static string PairListEmptyTitle => Get();

    /// <summary>
    /// EmptyState of the pair list: the next action.
    /// </summary>
    public static string PairListEmptyBody => Get();

    /// <summary>
    /// Title of the welcome screen.
    /// </summary>
    public static string WelcomeTitle => Get();

    /// <summary>
    /// Subtitle of the welcome screen: the value proposition in one phrase.
    /// </summary>
    public static string WelcomeSubtitle => Get();

    /// <summary>
    /// Hint of the server-address field (welcome screen; the manual host/IP field is always available).
    /// </summary>
    public static string WelcomeHostHint => Get();

    /// <summary>
    /// Visible label of the server-address field (welcome screen): the field keeps its name while
    /// the user types, the hint alone cannot carry it.
    /// </summary>
    public static string WelcomeHostLabel => Get();

    /// <summary>
    /// Button initiating Login Flow v2 (welcome screen).
    /// </summary>
    public static string WelcomeConnect => Get();

    /// <summary>
    /// Hint-button on the welcome screen after a DNS failure: focus on manual IP entry.
    /// </summary>
    public static string WelcomeManualIp => Get();

    /// <summary>
    /// Title of the settings screen.
    /// </summary>
    public static string SettingsTitle => Get();

    /// <summary>
    /// Inline error on the welcome screen: the address failed normalization.
    /// </summary>
    public static string ErrorHostInvalid => Get();

    /// <summary>
    /// WebView screen with no active flow session (Activity restart etc.).
    /// </summary>
    public static string ErrorSessionExpired => Get();

    /// <summary>
    /// Title of the help dialog on the welcome screen (DNS/Private DNS/IP).
    /// </summary>
    public static string HelpTitle => Get();

    /// <summary>
    /// Body of the help dialog on the welcome screen.
    /// </summary>
    public static string HelpBody => Get();

    /// <summary>
    /// Close button of the help dialog.
    /// </summary>
    public static string HelpOk => Get();

    /// <summary>
    /// "Close" button of the WebView screen.
    /// </summary>
    public static string LoginClose => Get();

    /// <summary>
    /// Title of the WebView screen when the server is unknown.
    /// </summary>
    public static string LoginTitle => Get();

    /// <summary>
    /// Title format of the WebView screen: "Sign in to &lt;server&gt;".
    /// </summary>
    public static string LoginTitleFormat => Get();

    /// <summary>
    /// Waiting-for-consent hint shown while polling.
    /// </summary>
    public static string LoginWaiting => Get();

    /// <summary>
    /// Idle state chip.
    /// </summary>
    public static string StatusIdle => Get();

    /// <summary>
    /// Syncing state chip.
    /// </summary>
    public static string StatusSyncing => Get();

    /// <summary>
    /// Success state chip.
    /// </summary>
    public static string StatusSuccess => Get();

    /// <summary>
    /// Conflict state chip.
    /// </summary>
    public static string StatusConflict => Get();

    /// <summary>
    /// Counter form for 1 ("N conflict") on the Conflict chip.
    /// </summary>
    public static string ChipConflictOne => Get();

    /// <summary>
    /// Counter form for 2–4 ("N conflicts") on the Conflict chip.
    /// </summary>
    public static string ChipConflictFew => Get();

    /// <summary>
    /// Counter form for all remaining numbers ("N conflicts") on the Conflict chip.
    /// </summary>
    public static string ChipConflictMany => Get();

    /// <summary>
    /// Error state chip.
    /// </summary>
    public static string StatusErrorChip => Get();

    /// <summary>
    /// Offline state chip; term.
    /// </summary>
    public static string StatusOffline => Get();

    /// <summary>
    /// Header DNS chip on the pair list when there is no account at all.
    /// </summary>
    public static string AccountChipEmpty => Get();

    /// <summary>
    /// Prefix of the local line of PairCard ("Device files › …").
    /// </summary>
    public static string PairCardLocalPrefix => Get();

    /// <summary>
    /// CTA of the EmptyState and the FAB (verb-as-button).
    /// </summary>
    public static string PairListAdd => Get();

    /// <summary>
    /// Card context-menu action: pause.
    /// </summary>
    public static string CardActionPause => Get();

    /// <summary>
    /// Card context-menu action: resume from pause.
    /// </summary>
    public static string CardActionResume => Get();

    /// <summary>
    /// Card context-menu action: delete.
    /// </summary>
    public static string CardActionDelete => Get();

    /// <summary>
    /// Name for a reader of the card's context-menu opener (the three-dot glyph is decoration, the
    /// action has a word).
    /// </summary>
    public static string CardActionMenu => Get();

    /// <summary>
    /// Title of the pair-deletion confirmation.
    /// </summary>
    public static string DeletePairTitle => Get();

    /// <summary>
    /// Confirmation body: config minus, files intact.
    /// </summary>
    public static string DeletePairBody => Get();

    /// <summary>
    /// Confirmation action button (no "OK").
    /// </summary>
    public static string DeletePairAccept => Get();

    /// <summary>
    /// Universal cancelling action for dialogs.
    /// </summary>
    public static string DialogCancel => Get();

    /// <summary>
    /// "Sync now" action (verb in the button).
    /// </summary>
    public static string SyncNowAction => Get();

    /// <summary>
    /// Title of the add-pair wizard.
    /// </summary>
    public static string WizardTitle => Get();

    /// <summary>
    /// Breadcrumb of the DAV root (step 1).
    /// </summary>
    public static string WizardRootCrumb => Get();

    /// <summary>
    /// Step description for a11y: "Step N of 3".
    /// </summary>
    public static string WizardStepOf => Get();

    /// <summary>
    /// Navigation button up the tree (step 1).
    /// </summary>
    public static string WizardUp => Get();

    /// <summary>
    /// Primary action of step 1 (verb-as-button).
    /// </summary>
    public static string WizardChooseThisFolder => Get();

    /// <summary>
    /// Primary action of step 2.
    /// </summary>
    public static string WizardNext => Get();

    /// <summary>
    /// Primary action of step 3 — saving the pair.
    /// </summary>
    public static string WizardCreatePair => Get();

    /// <summary>
    /// Title of the "no account" dialog (entering the wizard outside the gate).
    /// </summary>
    public static string WizardNoAccountTitle => Get();

    /// <summary>
    /// Body of the "no account" dialog: fact + next action.
    /// </summary>
    public static string WizardNoAccountBody => Get();

    /// <summary>
    /// Inline error of step 1: PROPFIND failure + details.
    /// </summary>
    public static string WizardLoadFailed => Get();

    /// <summary>
    /// Inline error of step 3: not all steps are filled.
    /// </summary>
    public static string WizardIncomplete => Get();

    /// <summary>
    /// Button launching the SAF picker (step 2).
    /// </summary>
    public static string WizardPickLocalButton => Get();

    /// <summary>
    /// Inline SAF privacy explanation (add-pair wizard).
    /// </summary>
    public static string WizardPickLocalHint => Get();

    /// <summary>
    /// Header of the interval segment (step 3).
    /// </summary>
    public static string WizardScheduleTitle => Get();

    /// <summary>
    /// Header of the direction section (informational).
    /// </summary>
    public static string WizardDirectionTitle => Get();

    /// <summary>
    /// Bidirectionality is fixed in v1 — we show it honestly.
    /// </summary>
    public static string WizardDirectionInfo => Get();

    /// <summary>
    /// Header of the conflict-strategy section.
    /// </summary>
    public static string WizardConflictTitle => Get();

    /// <summary>
    /// The default KeepBoth strategy as microcopy.
    /// </summary>
    public static string WizardConflictInfo => Get();

    /// <summary>
    /// Switch for the background network constraint.
    /// </summary>
    public static string WizardWifiOnly => Get();

    /// <summary>
    /// Schedule interval of 15 minutes.
    /// </summary>
    public static string Schedule15 => Get();

    /// <summary>
    /// Schedule interval of 30 minutes.
    /// </summary>
    public static string Schedule30 => Get();

    /// <summary>
    /// Schedule interval of 1 hour.
    /// </summary>
    public static string Schedule60 => Get();

    /// <summary>
    /// "Manually only" schedule.
    /// </summary>
    public static string ScheduleManual => Get();

    /// <summary>
    /// Header of the run-status card (pair details).
    /// </summary>
    public static string DetailsLastRunTitle => Get();

    /// <summary>
    /// Status of a pair without any run yet.
    /// </summary>
    public static string DetailsNeverRun => Get();

    /// <summary>
    /// Event count at run completion (a label without plural forms).
    /// </summary>
    public static string DetailsEventsCount => Get();

    /// <summary>
    /// Run duration in the details (absolute).
    /// </summary>
    public static string DetailsDurationLabel => Get();

    /// <summary>
    /// Next scheduled run — "Next at HH:mm".
    /// </summary>
    public static string DetailsNextAt => Get();

    /// <summary>
    /// The pair is paused — scheduling is off.
    /// </summary>
    public static string DetailsNextPaused => Get();

    /// <summary>
    /// Manual schedule — a run only at the user's wish.
    /// </summary>
    public static string DetailsNextManual => Get();

    /// <summary>
    /// Label of the pause switch (pair details).
    /// </summary>
    public static string DetailsPauseSwitch => Get();

    /// <summary>
    /// Section of the pair details opening the conflicts screen.
    /// </summary>
    public static string DetailsConflictsTitle => Get();

    /// <summary>
    /// Section of the pair details opening the trash-buffer screen; term.
    /// </summary>
    public static string DetailsStashTitle => Get();

    /// <summary>
    /// Stash TTL note: "30-day retention".
    /// </summary>
    public static string DetailsStashNote => Get();

    /// <summary>
    /// Pair journal section (latest events of the last run).
    /// </summary>
    public static string DetailsLogTitle => Get();

    /// <summary>
    /// Empty pair journal.
    /// </summary>
    public static string DetailsLogEmpty => Get();

    /// <summary>
    /// Pair settings section.
    /// </summary>
    public static string DetailsSettingsTitle => Get();

    /// <summary>
    /// Row/header of the interval-change sheet.
    /// </summary>
    public static string DetailsIntervalTitle => Get();

    /// <summary>
    /// SAF re-pick action for the local folder (pair details menu).
    /// </summary>
    public static string DetailsChangeFolder => Get();

    /// <summary>
    /// Destructive action of the pair details — deleting the pair configuration.
    /// </summary>
    public static string DetailsDeletePair => Get();

    /// <summary>
    /// Deletion confirmation: config minus, files intact, the stash lives on.
    /// </summary>
    public static string DetailsDeletePairBody => Get();

    /// <summary>
    /// The pair was already deleted from another screen — honest dialog and navigation back.
    /// </summary>
    public static string DetailsGoneBody => Get();

    /// <summary>
    /// Title of the "pair is gone" dialog (deleted from another screen).
    /// </summary>
    public static string DetailsGoneTitle => Get();

    /// <summary>
    /// Run outcome: still running.
    /// </summary>
    public static string OutcomeRunning => Get();

    /// <summary>
    /// Run outcome: some operations were not applied.
    /// </summary>
    public static string OutcomePartial => Get();

    /// <summary>
    /// Run outcome: interrupted (network/cancellation).
    /// </summary>
    public static string OutcomeFailed => Get();

    /// <summary>
    /// Run outcome: cancelled by the user/scheduler.
    /// </summary>
    public static string OutcomeCancelled => Get();

    /// <summary>
    /// Title of the reassuring empty conflicts screen.
    /// </summary>
    public static string ConflictsEmptyTitle => Get();

    /// <summary>
    /// Empty conflicts list: no conflicts, KeepBoth will protect in the future.
    /// </summary>
    public static string ConflictsEmptyBody => Get();

    /// <summary>
    /// v1 conflict state: both versions already kept (no action needed).
    /// </summary>
    public static string ConflictKeptBoth => Get();

    /// <summary>
    /// Conflict action: keep the server version.
    /// </summary>
    public static string ConflictKeepServer => Get();

    /// <summary>
    /// Conflict action: keep the local version.
    /// </summary>
    public static string ConflictKeepLocal => Get();

    /// <summary>
    /// Confirmation dialog title before a keep-side resolution.
    /// </summary>
    public static string ConflictResolveConfirmTitle => Get();

    /// <summary>
    /// Confirmation body: the server version is kept, the local one goes to the trash buffer.
    /// </summary>
    public static string ConflictResolveServerConfirmFormat => Get();

    /// <summary>
    /// Confirmation body: the local version is kept, the server one goes to the trash buffer.
    /// </summary>
    public static string ConflictResolveLocalConfirmFormat => Get();

    /// <summary>
    /// Title after a completed resolution.
    /// </summary>
    public static string ConflictResolvedTitle => Get();

    /// <summary>
    /// Body after a completed resolution: kept on both sides, discarded copy in the buffer.
    /// </summary>
    public static string ConflictResolvedBody => Get();

    /// <summary>
    /// Body when a resolution stopped mid-way: the journal continues it on the next sync.
    /// </summary>
    public static string ConflictResolvePartialBody => Get();

    /// <summary>
    /// Title when the resolution could not run (busy pair, versions already sorted out).
    /// </summary>
    public static string ConflictResolveFailedTitle => Get();

    /// <summary>
    /// Title of the empty trash buffer screen.
    /// </summary>
    public static string StashEmptyTitle => Get();

    /// <summary>
    /// Empty state of the trash buffer: the 30-day retention promise.
    /// </summary>
    public static string StashEmptyBody => Get();

    /// <summary>
    /// Row action of the trash buffer screen: return a copy from the stash.
    /// </summary>
    public static string StashRestore => Get();

    /// <summary>
    /// Remaining copy TTL: "Deletes in 21 days".
    /// </summary>
    public static string StashExpiresIn => Get();

    /// <summary>
    /// When the deletion happened (relative time).
    /// </summary>
    public static string StashDeletedAt => Get();

    /// <summary>
    /// Relative time: "just now".
    /// </summary>
    public static string RtNow => Get();

    /// <summary>
    /// Relative time: "N min ago".
    /// </summary>
    public static string RtMinAgo => Get();

    /// <summary>
    /// Relative time: "N h ago".
    /// </summary>
    public static string RtHourAgo => Get();

    /// <summary>
    /// Relative time: "N d ago".
    /// </summary>
    public static string RtDayAgo => Get();

    /// <summary>
    /// Duration: "N s".
    /// </summary>
    public static string DurSec => Get();

    /// <summary>
    /// Duration: "N min M s".
    /// </summary>
    public static string DurMinSec => Get();

    /// <summary>
    /// Duration: "N h M min".
    /// </summary>
    public static string DurHourMin => Get();

    /// <summary>
    /// Short remainder: "N d".
    /// </summary>
    public static string ShortDays => Get();

    /// <summary>
    /// Short remainder: "N h".
    /// </summary>
    public static string ShortHours => Get();

    /// <summary>
    /// Short remainder: "N min" (minimum 1).
    /// </summary>
    public static string ShortMinutes => Get();

    /// <summary>
    /// Header of the palette segment (settings screen).
    /// </summary>
    public static string SettingsThemeTitle => Get();

    /// <summary>
    /// Nextcloud palette chip (factual name — not promotional).
    /// </summary>
    public static string ThemeNextcloudLabel => Get();

    /// <summary>
    /// Dynamic palette chip (Material You).
    /// </summary>
    public static string ThemeDynamicLabel => Get();

    /// <summary>
    /// Default-interval row for new pairs.
    /// </summary>
    public static string SettingsDefaultIntervalTitle => Get();

    /// <summary>
    /// "Only LAN" default switch (settings screen).
    /// </summary>
    public static string SettingsWifiOnlyTitle => Get();

    /// <summary>
    /// Explanation: the default applies to new pairs, not to existing ones.
    /// </summary>
    public static string SettingsWifiOnlyHint => Get();

    /// <summary>
    /// Battery-optimization section.
    /// </summary>
    public static string SettingsBatteryTitle => Get();

    /// <summary>
    /// Battery status: ignore exemption is on — background is reliable.
    /// </summary>
    public static string BatteryIgnored => Get();

    /// <summary>
    /// Battery status: background delays possible (a fact, no panic).
    /// </summary>
    public static string BatteryNotIgnored => Get();

    /// <summary>
    /// Button for the system request to ignore optimization.
    /// </summary>
    public static string BatteryOpen => Get();

    /// <summary>
    /// Account section (host·user).
    /// </summary>
    public static string SettingsAccountTitle => Get();

    /// <summary>
    /// Account sign-out action (also its confirmation button).
    /// </summary>
    public static string AccountLogout => Get();

    /// <summary>
    /// Title of the sign-out confirmation.
    /// </summary>
    public static string LogoutTitle => Get();

    /// <summary>
    /// Confirmation body: honest pair cascade + files intact (fact + consequence).
    /// </summary>
    public static string LogoutBody => Get();

    /// <summary>
    /// The global log, as the flyout section and the screen title.
    /// </summary>
    public static string LogTitle => Get();

    /// <summary>
    /// Navigation to the about screen about the app.
    /// </summary>
    public static string SettingsAboutTitle => Get();

    /// <summary>
    /// Privacy-policy link, in the about screen.
    /// </summary>
    public static string PrivacyTitle => Get();

    /// <summary>
    /// Privacy screen intro: the one-sentence promise.
    /// </summary>
    public static string PrivacyIntro => Get();

    /// <summary>
    /// Privacy section: nothing is collected from the user.
    /// </summary>
    public static string PrivacyNothingTitle => Get();

    /// <summary>
    /// Privacy section body: no account, analytics, ads, tracking, crash reports, SDKs.
    /// </summary>
    public static string PrivacyNothingBody => Get();

    /// <summary>
    /// Privacy section: what lives on the device and where the token is kept.
    /// </summary>
    public static string PrivacyDeviceTitle => Get();

    /// <summary>
    /// Privacy section body: files, log, baseline, trash buffer, keystore.
    /// </summary>
    public static string PrivacyDeviceBody => Get();

    /// <summary>
    /// Privacy section: the only network peer is the user's own server.
    /// </summary>
    public static string PrivacyServerTitle => Get();

    /// <summary>
    /// Privacy section body: LAN-only default, local cleartext exception.
    /// </summary>
    public static string PrivacyServerBody => Get();

    /// <summary>
    /// Privacy section: the requested permissions with their purpose.
    /// </summary>
    public static string PrivacyPermissionsTitle => Get();

    /// <summary>
    /// Privacy section body: internet, battery, notifications, foreground service; what is never asked.
    /// </summary>
    public static string PrivacyPermissionsBody => Get();

    /// <summary>
    /// Privacy section: what signing out and uninstalling delete.
    /// </summary>
    public static string PrivacyControlTitle => Get();

    /// <summary>
    /// Privacy section body: the wipe list, the untouched files, where to ask questions.
    /// </summary>
    public static string PrivacyControlBody => Get();

    /// <summary>
    /// Privacy screen link: the full policy document in the public repository.
    /// </summary>
    public static string PrivacyFullLink => Get();

    /// <summary>
    /// Tagline of the about screen (promotional copy without "Nextcloud" — identity convention).
    /// </summary>
    public static string AboutTagline => Get();

    /// <summary>
    /// Version line of the about screen.
    /// </summary>
    public static string AboutVersion => Get();

    /// <summary>
    /// Trademark disclaimer of the about screen — the wording; every locale states the same
    /// fact in its own language, the trademark names themselves untranslated.
    /// </summary>
    public static string AboutDisclaimer => Get();

    /// <summary>
    /// Source-code link (about screen).
    /// </summary>
    public static string AboutSource => Get();

    /// <summary>
    /// Bug-report link (about screen).
    /// </summary>
    public static string AboutBugs => Get();

    /// <summary>
    /// "All" filter chip (global log).
    /// </summary>
    public static string LogFilterAll => Get();

    /// <summary>
    /// "Errors" filter chip (global log).
    /// </summary>
    public static string LogFilterErrors => Get();

    /// <summary>
    /// Title of the active run's FGS notification.
    /// </summary>
    public static string SyncForegroundTitle => Get();

    /// <summary>
    /// FGS-notification body without a pair name (fallback).
    /// </summary>
    public static string SyncForegroundDefaultBody => Get();

    /// <summary>
    /// Name of the background-run notification channel.
    /// </summary>
    public static string SyncChannelName => Get();

    /// <summary>
    /// Run progress line "File X of Y".
    /// </summary>
    public static string SyncProgressFormat => Get();

    /// <summary>
    /// Run state before the total is announced (probe/scan/planning).
    /// </summary>
    public static string SyncPreparing => Get();

    /// <summary>
    /// "Cancel run" action (pair details).
    /// </summary>
    public static string CancelRun => Get();

    /// <summary>
    /// Cancellation requested, the run has not stopped yet.
    /// </summary>
    public static string RunCancelling => Get();

    /// <summary>
    /// Confirmation of a stash restore (path in quotes).
    /// </summary>
    public static string StashRestoreConfirmFormat => Get();

    /// <summary>
    /// Title of the "file restored" dialog.
    /// </summary>
    public static string StashRestoreDoneTitle => Get();

    /// <summary>
    /// Body of the "file restored" dialog: the next run will send it to the server.
    /// </summary>
    public static string StashRestoreDoneBody => Get();

    /// <summary>
    /// Title of the restore-failure dialog.
    /// </summary>
    public static string StashRestoreFailedTitle => Get();

    /// <summary>
    /// Line for the current culture; a missing key returns the key name itself, so a locale gap
    /// is visible immediately.
    /// </summary>
    private static string Get([CallerMemberName] string key = "") =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>
    /// Dynamic lookup by key (rendering of journal events whose keys are defined by Core).
    /// An unknown key (a new kind on older resources) yields <paramref name="fallback"/>, so the screen does not break.
    /// </summary>
    public static string GetByKey(string key, string fallback) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture) ?? fallback;
}
