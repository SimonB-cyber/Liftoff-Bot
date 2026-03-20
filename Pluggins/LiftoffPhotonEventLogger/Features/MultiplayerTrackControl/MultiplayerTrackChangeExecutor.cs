using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Photon.Pun;
using ExitGames.Client.Photon;
using UnityEngine;
using UnityEngine.UI;

namespace LiftoffPhotonEventLogger.Features.MultiplayerTrackControl;

internal enum MultiplayerTrackChangeExecutionStatus
{
    Applied,
    DryRun,
    DeferredWaitingForActivePopup,
    NoMatchingCandidate,
    AbortedNotInMultiplayer,
    AbortedNotHost,
    Failed
}

internal enum PopupAcquireStatus
{
    Acquired,
    WaitingForActivePopup,
    Unavailable
}

internal sealed class MultiplayerTrackChangeExecutor : ITrackControlAdapter
{
    private readonly MultiplayerTrackControlConfig _config;
    private readonly MultiplayerDiscoveryService _discovery;
    private readonly MultiplayerHostStateDetector _hostDetector;
    private readonly MultiplayerTrackControlLog _log;
    private readonly Func<object?, string> _describe;

    public MultiplayerTrackChangeExecutor(
        MultiplayerTrackControlConfig config,
        MultiplayerDiscoveryService discovery,
        MultiplayerHostStateDetector hostDetector,
        MultiplayerTrackControlLog log,
        Func<object?, string> describe)
    {
        _config = config;
        _discovery = discovery;
        _hostDetector = hostDetector;
        _log = log;
        _describe = describe;
    }

    public void DumpCurrentState()
    {
        _hostDetector.LogSnapshot("STATE");
        DumpKnownLiveObjects("STATE", "Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup");
        DumpKnownLiveObjects("STATE", "MultiplayerRaceScoreButtonPanel");
        DumpKnownLiveObjects("STATE", "InGameMenuMainPanel");
    }

    public MultiplayerTrackChangeExecutionStatus AttemptConfiguredChange()
    {
        var request = new ChangeRequest
        {
            EnvironmentName = _config.TargetEnvironmentName.Value?.Trim() ?? string.Empty,
            TrackName = _config.TargetTrackName.Value?.Trim() ?? string.Empty,
            RaceName = _config.TargetRaceName.Value?.Trim() ?? string.Empty,
            WorkshopId = _config.TargetWorkshopId.Value?.Trim() ?? string.Empty,
            CycleNext = false
        };

        return Execute(request);
    }

    public MultiplayerTrackChangeExecutionStatus AttemptCycleNext()
    {
        return Execute(new ChangeRequest { CycleNext = true });
    }

    private MultiplayerTrackChangeExecutionStatus Execute(ChangeRequest request)
    {
        _discovery.Refresh();
        var host = _hostDetector.Capture();

        _log.Info("EXEC", $"Requested change: environment=\"{request.EnvironmentName}\" track=\"{request.TrackName}\" race=\"{request.RaceName}\" workshop=\"{request.WorkshopId}\" cycle={request.CycleNext} dryRun={_config.EnableDryRun.Value}");

        if (!host.IsInMultiplayer)
        {
            _log.Warn("EXEC", $"Aborted because multiplayer is not active. reason={host.InMultiplayerReason}");
            return MultiplayerTrackChangeExecutionStatus.AbortedNotInMultiplayer;
        }

        if (!host.IsHost)
        {
            _log.Warn("EXEC", $"Aborted because local player is not host. reason={host.HostReason}");
            return MultiplayerTrackChangeExecutionStatus.AbortedNotHost;
        }

        if (!TryAcquirePopup(out var popupContext, out var popupAcquireStatus))
        {
            if (popupAcquireStatus == PopupAcquireStatus.WaitingForActivePopup)
            {
                _log.Warn("EXEC", "Deferring track/race change because no live popup/controller instance is active yet.");
                return MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup;
            }

            _log.Warn("EXEC", "No multiplayer settings popup/controller path was available. TODO: validate additional runtime entry points.");
            return MultiplayerTrackChangeExecutionStatus.Failed;
        }

        try
        {
            EnsurePopupPrepared(popupContext);

            if (!ConfigurePopupForRequest(popupContext, request, out var selectionSummary))
            {
                _log.Warn("EXEC", "Popup was acquired but no matching track/race candidate was found.");
                DumpPopupOptions(popupContext);
                return MultiplayerTrackChangeExecutionStatus.NoMatchingCandidate;
            }

            _log.Info("EXEC", $"Selection prepared: {selectionSummary}");

            if (_config.EnableDryRun.Value)
            {
                _log.Info("EXEC", "Dry-run enabled. Skipping PopupQuickPlayMultiplayerSetup.OnSetGame invocation.");
                DumpCurrentState();
                return MultiplayerTrackChangeExecutionStatus.DryRun;
            }

            if (!IsPopupActive(popupContext.Popup) &&
                _config.EnableCachedSetGameCallbackFallback.Value &&
                TryInvokePopupSetGameCallback(popupContext))
            {
                _log.Info("EXEC", "Track/race change invocation finished via popup onSetGame callback.");
                return MultiplayerTrackChangeExecutionStatus.Applied;
            }

            if (!IsPopupActive(popupContext.Popup))
            {
                _log.Warn("EXEC", "Aborting final apply because popup is inactive and cached callback fallback is disabled.");
                return MultiplayerTrackChangeExecutionStatus.DeferredWaitingForActivePopup;
            }

            var onSetGame = ReflectionHelper.FindMethod(popupContext.Popup.GetType(), "OnSetGame", 0);
            if (onSetGame == null)
            {
                _log.Warn("EXEC", "PopupQuickPlayMultiplayerSetup.OnSetGame could not be found.");
                return MultiplayerTrackChangeExecutionStatus.Failed;
            }

            _log.Info("EXEC", $"Invoking {ReflectionHelper.FormatMethodSignature(onSetGame)}");
            onSetGame.Invoke(popupContext.Popup, Array.Empty<object>());
            _log.Info("EXEC", "Track/race change invocation finished.");
            return MultiplayerTrackChangeExecutionStatus.Applied;
        }
        catch (Exception ex)
        {
            _log.Error("EXEC", "Track/race change execution failed.", ex);
            return MultiplayerTrackChangeExecutionStatus.Failed;
        }
    }

    private bool TryAcquirePopup(out PopupContext popupContext, out PopupAcquireStatus popupAcquireStatus)
    {
        popupContext = default;
        popupAcquireStatus = PopupAcquireStatus.Unavailable;

        var popupType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameSetup.PopupQuickPlayMultiplayerSetup");
        if (popupType == null)
            return false;

        if (MultiplayerRuntimeState.TryGetLatestPopupWithSetGameCallback(out var callbackPopup, out _, out var callbackSource, out var callbackCapturedUtc) &&
            callbackPopup != null)
        {
            if (callbackPopup is UnityEngine.Object unityPopup && unityPopup == null)
            {
                _log.Warn("EXEC", $"Discarding cached callback popup from {callbackSource} because the Unity object is destroyed.");
                MultiplayerRuntimeState.ClearLatestPopupObject();
            }
            else if (GetInstanceId(callbackPopup) == 0)
            {
                _log.Warn("EXEC", $"Discarding cached callback popup from {callbackSource} because it has an invalid instance id: {ReflectionHelper.DescribeObjectIdentity(callbackPopup)}");
                MultiplayerRuntimeState.ClearLatestPopupObject();
            }
            else if (TryBuildPopupContext(callbackPopup, out popupContext))
            {
                var callbackPresent = HasOnSetGameCallback(popupContext.Popup);
                var active = IsPopupActive(popupContext.Popup);
                if (callbackPresent && active)
                {
                    _log.Info("EXEC", $"Using popup with known onSetGame callback from {callbackSource} capturedAt={callbackCapturedUtc:O}: {ReflectionHelper.DescribeObjectIdentity(popupContext.Popup)} active={active} callback={callbackPresent}");
                    popupAcquireStatus = PopupAcquireStatus.Acquired;
                    return true;
                }

                _log.Warn("EXEC", $"Discarding cached callback popup from {callbackSource} because it is no longer usable: {ReflectionHelper.DescribeObjectIdentity(popupContext.Popup)} active={active} callback={callbackPresent}");
                MultiplayerRuntimeState.ClearLatestPopupObject();
                popupContext = default;
                if (!active)
                    popupAcquireStatus = PopupAcquireStatus.WaitingForActivePopup;
            }
            else
            {
                _log.Warn("EXEC", $"Discarding cached callback popup from {callbackSource} because its popup context could not be rebuilt: {ReflectionHelper.DescribeObjectIdentity(callbackPopup)}");
                MultiplayerRuntimeState.ClearLatestPopupObject();
            }
        }

        if (MultiplayerRuntimeState.TryGetLatestPopupWithSetGameCallback(out _, out var cachedCallback, out _, out _) &&
            cachedCallback != null)
        {
            _log.Info("EXEC", $"A cached onSetGame delegate is available. fallbackEnabled={_config.EnableCachedSetGameCallbackFallback.Value}");
        }

        var controllerCandidates = GetControllerCandidates().ToList();
        if (controllerCandidates.Count == 0)
            _log.Warn("EXEC", "No live controller candidates were found for popup acquisition.");

        foreach (var controllerCandidate in controllerCandidates)
        {
            var beforeIds = new HashSet<int>(ReflectionHelper.GetLiveObjects(popupType).Select(GetInstanceId));
            var openMethod = ReflectionHelper.FindMethod(controllerCandidate.Controller.GetType(), controllerCandidate.OpenMethodName, 0);
            if (openMethod == null)
                continue;

            _log.Info("EXEC", $"Opening popup via {controllerCandidate.Controller.GetType().FullName}.{controllerCandidate.OpenMethodName} on {ReflectionHelper.DescribeObjectIdentity(controllerCandidate.Controller)}");
            openMethod.Invoke(controllerCandidate.Controller, Array.Empty<object>());

            var visiblePopups = ReflectionHelper.GetLiveObjects(popupType).ToList();
            var popup = SelectActivePopupCandidate(visiblePopups, beforeIds);

            if (popup != null && TryBuildPopupContext(popup, out popupContext))
            {
                popupContext.Controller = controllerCandidate.Controller;
                popupContext.ControllerMethod = controllerCandidate.OpenMethodName;
                _log.Info("EXEC", $"Using popup from controller flow {ReflectionHelper.DescribeObjectIdentity(popupContext.Popup)} active={IsPopupActive(popupContext.Popup)}");
                popupAcquireStatus = PopupAcquireStatus.Acquired;
                return true;
            }

            if (visiblePopups.Any())
                popupAcquireStatus = PopupAcquireStatus.WaitingForActivePopup;
        }

        var existingPopups = ReflectionHelper.GetLiveObjects(popupType).ToList();
        var fallbackPopup = SelectActivePopupCandidate(existingPopups, beforeIds: null);
        if (fallbackPopup != null && TryBuildPopupContext(fallbackPopup, out popupContext))
        {
            _log.Info("EXEC", $"Reusing existing popup {ReflectionHelper.DescribeObjectIdentity(popupContext.Popup)} active={IsPopupActive(popupContext.Popup)} callback={HasOnSetGameCallback(popupContext.Popup)}");
            popupAcquireStatus = PopupAcquireStatus.Acquired;
            return true;
        }

        if (existingPopups.Any())
        {
            _log.Warn("EXEC", $"Only inactive popup instances were available after controller attempts. count={existingPopups.Count}");
            popupAcquireStatus = PopupAcquireStatus.WaitingForActivePopup;
        }

        return false;
    }

    private static object? SelectActivePopupCandidate(IEnumerable<object> popupCandidates, ISet<int>? beforeIds)
    {
        return popupCandidates.FirstOrDefault(candidate => IsEligiblePopupCandidate(candidate, beforeIds, requireCallback: true))
               ?? popupCandidates.FirstOrDefault(candidate => IsEligiblePopupCandidate(candidate, beforeIds, requireCallback: false));
    }

    private static bool IsEligiblePopupCandidate(object candidate, ISet<int>? beforeIds, bool requireCallback)
    {
        if (candidate == null || !IsPopupActive(candidate))
            return false;

        if (beforeIds != null && beforeIds.Contains(GetInstanceId(candidate)))
            return false;

        return !requireCallback || HasOnSetGameCallback(candidate);
    }

    private void EnsurePopupPrepared(PopupContext context)
    {
        if (HasSelectableContent(context))
            return;

        _log.Info("EXEC", "Popup has no selectable content yet. Attempting to repopulate from live session settings.");
        var desiredGameMode = GetRoomGameModeSnapshot();
        var desiredSlipstream = GetRoomSlipstreamSnapshot();

        if (TryApplyLiveSessionSettings(context))
            _log.Info("EXEC", "Reapplied live session settings to popup/content panel.");

        InvokeZeroArg(context.ContentPanel, "InitializeDefault");
        InvokeZeroArg(context.ContentPanel, "FillGameModeSelection");
        ApplyRoomSnapshotToContentPanel(context, desiredGameMode, desiredSlipstream);
        if (desiredGameMode != null)
            TryInvokeSelectionMethod(context.ContentPanel, "OnGameModeSelected", desiredGameMode);
        InvokeZeroArg(context.ContentPanel, "FillEnvironmentSelection");
        InvokeZeroArg(context.ContentPanel, "FillContentSelection");
        InvokeZeroArg(context.ContentPanel, "InvokeCurrentValues");

        _log.Info("EXEC", $"Post-prime selectable content: popupOptions={GetPopupOptionCount(context)} selectedContent={GetSelectedContentCount(context)}");
    }

    private void ApplyRoomSnapshotToContentPanel(PopupContext context, object? desiredGameMode, bool? desiredSlipstream)
    {
        if (desiredGameMode != null)
        {
            try
            {
                ReflectionHelper.SetMemberValue(context.ContentPanel, "SelectedGameMode", desiredGameMode);
                _log.Info("EXEC", $"Applied room game mode snapshot -> {desiredGameMode}");
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"Failed to apply room game mode snapshot: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (desiredSlipstream.HasValue)
        {
            try
            {
                ReflectionHelper.SetMemberValue(context.ContentPanel, "IncludeSlipstreamAssets", desiredSlipstream.Value);
                _log.Info("EXEC", $"Applied room slipstream snapshot -> {desiredSlipstream.Value}");
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"Failed to apply room slipstream snapshot: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private IEnumerable<ControllerCandidate> GetControllerCandidates()
    {
        foreach (var mapping in new[]
                 {
                     new ControllerCandidate("MultiplayerRaceScoreButtonPanel", "OnGameSettings"),
                     new ControllerCandidate("InGameMenuMainPanel", "OnMultiplayerGameSettings")
                 })
        {
            var type = _discovery.TryResolveKnownType(mapping.TypeName);
            foreach (var controller in ReflectionHelper.GetLiveObjects(type))
                yield return mapping.WithController(controller);
        }
    }

    private bool TryBuildPopupContext(object popup, out PopupContext context)
    {
        context = default;
        var contentPanel = ReflectionHelper.GetMemberValue(popup, "panelContentSetupPanel");
        if (contentPanel == null)
            return false;

        var dropdown = ReflectionHelper.GetMemberValue(contentPanel, "dropdownContentSelection");
        if (dropdown == null)
            return false;

        var environmentDropdown = ReflectionHelper.GetMemberValue(contentPanel, "dropdownEnvironmentSelection");

        context = new PopupContext
        {
            Popup = popup,
            ContentPanel = contentPanel,
            Dropdown = dropdown,
            EnvironmentDropdown = environmentDropdown
        };

        return true;
    }

    private bool ConfigurePopupForRequest(PopupContext context, ChangeRequest request, out string selectionSummary)
    {
        selectionSummary = string.Empty;
        if (request.CycleNext)
            return TryCycleNextSelection(context, out selectionSummary);

        var summaries = new List<string>();

        if (!EnsureEnvironmentForRequest(context, request, summaries))
            return false;

        if (TrySelectConfiguredContent(context, request, out var contentSummary))
        {
            summaries.Add(contentSummary);
            selectionSummary = string.Join("; ", summaries.Where(summary => !string.IsNullOrWhiteSpace(summary)));
            return true;
        }

        if (HasContentTarget(request) &&
            string.IsNullOrWhiteSpace(request.EnvironmentName) &&
            TrySearchAcrossEnvironments(context, request, summaries, out selectionSummary))
        {
            return true;
        }

        if (!HasContentTarget(request) && summaries.Count > 0)
        {
            selectionSummary = string.Join("; ", summaries.Where(summary => !string.IsNullOrWhiteSpace(summary)));
            return true;
        }

        return false;
    }

    private bool TryCycleNextSelection(PopupContext context, out string selectionSummary)
    {
        selectionSummary = string.Empty;
        if (context.Dropdown is not Dropdown dropdown)
            return false;

        if (dropdown.options.Count <= 1)
            return false;

        var nextIndex = (dropdown.value + 1) % dropdown.options.Count;
        dropdown.value = nextIndex;
        selectionSummary = $"cycled dropdown to index={nextIndex} caption=\"{dropdown.options[nextIndex].text}\"";
        return true;
    }

    private bool EnsureEnvironmentForRequest(PopupContext context, ChangeRequest request, List<string> summaries)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentName))
            return true;

        if (!TrySelectEnvironment(context, request.EnvironmentName, out var environmentSummary))
        {
            _log.Warn("EXEC", $"Requested environment/map \"{request.EnvironmentName}\" was not found in the environment selector.");
            DumpEnvironmentOptions(context);
            return false;
        }

        summaries.Add(environmentSummary);
        return true;
    }

    private bool TrySelectConfiguredContent(PopupContext context, ChangeRequest request, out string selectionSummary)
    {
        selectionSummary = string.Empty;

        if (TrySelectViaDropdownData(context, request, out selectionSummary))
            return true;

        if (!string.IsNullOrWhiteSpace(request.RaceName) && TrySelectRace(context, request, out selectionSummary))
            return true;

        if (!string.IsNullOrWhiteSpace(request.TrackName) && TrySelectTrack(context, request, out selectionSummary))
            return true;

        if (!string.IsNullOrWhiteSpace(request.WorkshopId))
        {
            if (TrySelectRace(context, request, out selectionSummary))
                return true;
            if (TrySelectTrack(context, request, out selectionSummary))
                return true;
        }

        return false;
    }

    private bool TrySearchAcrossEnvironments(PopupContext context, ChangeRequest request, List<string> prefixSummaries, out string selectionSummary)
    {
        selectionSummary = string.Empty;
        if (context.EnvironmentDropdown is not Dropdown environmentDropdown)
            return false;

        var originalIndex = environmentDropdown.value;
        foreach (var candidate in GetDropdownCandidates(context.EnvironmentDropdown))
        {
            if (candidate.Index == originalIndex)
                continue;

            if (!ApplyEnvironmentCandidate(context, candidate, out var environmentSummary))
                continue;

            if (!TrySelectConfiguredContent(context, request, out var contentSummary))
                continue;

            var summaries = new List<string>(prefixSummaries)
            {
                environmentSummary,
                contentSummary
            };
            selectionSummary = string.Join("; ", summaries.Where(summary => !string.IsNullOrWhiteSpace(summary)));
            return true;
        }

        if (originalIndex >= 0 && originalIndex < environmentDropdown.options.Count)
        {
            _ = ApplyEnvironmentCandidate(context, new DropdownCandidate(originalIndex, environmentDropdown.options[originalIndex].text ?? string.Empty, null), out _);
        }

        return false;
    }

    private bool TrySelectEnvironment(PopupContext context, string targetEnvironmentName, out string selectionSummary)
    {
        selectionSummary = string.Empty;
        if (context.EnvironmentDropdown == null)
            return false;

        foreach (var candidate in GetDropdownCandidates(context.EnvironmentDropdown))
        {
            if (!MatchesEnvironmentCandidate(candidate, targetEnvironmentName))
                continue;

            return ApplyEnvironmentCandidate(context, candidate, out selectionSummary);
        }

        return false;
    }

    private bool TrySelectViaDropdownData(PopupContext context, ChangeRequest request, out string selectionSummary)
    {
        selectionSummary = string.Empty;

        foreach (var candidate in GetDropdownCandidates(context.Dropdown))
        {
            if (!MatchesDropdownCandidate(candidate, request))
                continue;

            if (!ApplyDropdownCandidate(context, candidate))
                continue;

            selectionSummary = $"dropdown index={candidate.Index} caption=\"{candidate.Caption}\" data={FormatOptionSummary(candidate.Data)}";
            return true;
        }

        return false;
    }

    private bool TryApplyLiveSessionSettings(PopupContext context)
    {
        var sessionSettings = GetLiveSessionSettings();
        if (sessionSettings == null)
        {
            _log.Warn("EXEC", "Could not resolve live session settings from CurrentContentContainer.");
            return false;
        }

        var applied = false;
        applied |= TryInvokeSingleArgument(context.Popup, "ApplyFromGameSettings", sessionSettings);
        applied |= TryInvokeSingleArgument(context.ContentPanel, "ApplyFromGameSettings", sessionSettings);

        return applied;
    }

    private bool TryInvokePopupSetGameCallback(PopupContext context)
    {
        var callback = ReflectionHelper.GetMemberValue(context.Popup, "onSetGame") as Delegate;
        if (callback == null &&
            MultiplayerRuntimeState.TryGetLatestPopupWithSetGameCallback(out _, out var cachedCallback, out var callbackSource, out var callbackCapturedUtc) &&
            cachedCallback != null)
        {
            callback = cachedCallback;
            _log.Info("EXEC", $"Using cached onSetGame delegate from {callbackSource} capturedAt={callbackCapturedUtc:O}");
        }

        if (callback == null)
        {
            _log.Warn("EXEC", "Popup is inactive and onSetGame callback was not available.");
            return false;
        }

        var payload = CreatePopupGameSettingsPayload(context.Popup, callback);
        if (payload == null)
            return false;

        var applyToGameSettings = ReflectionHelper.FindMethod(context.Popup.GetType(), "ApplyToGameSettings", 1);
        if (applyToGameSettings == null)
        {
            _log.Warn("EXEC", "PopupQuickPlayMultiplayerSetup.ApplyToGameSettings could not be found for direct callback invocation.");
            return false;
        }

        try
        {
            applyToGameSettings.Invoke(context.Popup, new[] { payload });
            _log.Info("EXEC", $"Prepared popup game settings payload via {ReflectionHelper.FormatMethodSignature(applyToGameSettings)}");
            callback.DynamicInvoke(payload);
            _log.Info("EXEC", $"Invoked popup onSetGame callback directly via {callback.GetType().FullName}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"Direct popup onSetGame callback failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private object? CreatePopupGameSettingsPayload(object popup, Delegate callback)
    {
        var payloadType = callback.GetType().GenericTypeArguments.FirstOrDefault();
        if (payloadType == null)
        {
            var applyMethod = ReflectionHelper.FindMethod(popup.GetType(), "ApplyToGameSettings", 1);
            payloadType = applyMethod?.GetParameters().FirstOrDefault()?.ParameterType;
        }

        if (payloadType == null)
        {
            _log.Warn("EXEC", "Could not resolve popup game settings payload type.");
            return null;
        }

        try
        {
            return Activator.CreateInstance(payloadType);
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"Failed to create popup game settings payload of type {payloadType.FullName}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool TryGetCustomProperty(Hashtable table, string key, out object value)
    {
        value = null!;
        if (table == null || !table.ContainsKey(key))
            return false;

        value = table[key];
        return value != null;
    }

    private object? GetRoomGameModeSnapshot()
    {
        var room = PhotonNetwork.CurrentRoom;
        if (room?.CustomProperties == null)
            return null;

        if (!TryGetCustomProperty(room.CustomProperties, "GM", out var gameModeValue))
            return null;

        var gameModeType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameMode");
        if (gameModeType == null)
            return null;

        try
        {
            var enumValue = Enum.ToObject(gameModeType, Convert.ToInt32(gameModeValue));
            _log.Info("EXEC", $"Captured room game mode snapshot GM={gameModeValue} -> {enumValue}");
            return enumValue;
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"Failed to capture room game mode snapshot: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private bool? GetRoomSlipstreamSnapshot()
    {
        var room = PhotonNetwork.CurrentRoom;
        if (room?.CustomProperties == null)
            return null;

        if (!TryGetCustomProperty(room.CustomProperties, "S", out var slipstreamValue))
            return null;

        try
        {
            var value = Convert.ToBoolean(slipstreamValue);
            _log.Info("EXEC", $"Captured room slipstream snapshot S={slipstreamValue}");
            return value;
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"Failed to capture room slipstream snapshot: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private bool TrySelectRace(PopupContext context, ChangeRequest request, out string selectionSummary)
    {
        selectionSummary = string.Empty;

        var gameModeType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameMode");
        if (gameModeType != null)
        {
            var classicRace = Enum.Parse(gameModeType, "ClassicRace");
            ReflectionHelper.SetMemberValue(context.ContentPanel, "SelectedGameMode", classicRace);
        }

        foreach (var optionSet in GetSelectionOptionSets(context, "SelectedRace", "RaceQuickInfo", "GameContentEntry"))
        {
            var match = optionSet.Options.FirstOrDefault(option => MatchesContent(option, request, preferRaceName: true));
            if (match == null)
                continue;

            ReflectionHelper.SetMemberValue(context.ContentPanel, "SelectedRace", match);
            selectionSummary = $"race type={optionSet.OptionType.FullName} name=\"{ReadContentName(match)}\" localId=\"{ReadContentIdentifier(match, "LocalID")}\" managedId=\"{ReadContentIdentifier(match, "ManagedID")}\"";
            return true;
        }

        return false;
    }

    private bool TrySelectTrack(PopupContext context, ChangeRequest request, out string selectionSummary)
    {
        selectionSummary = string.Empty;

        foreach (var optionSet in GetSelectionOptionSets(context, "SelectedTrack", "TrackQuickInfo", "GameContentEntry"))
        {
            var match = optionSet.Options.FirstOrDefault(option => MatchesContent(option, request, preferRaceName: false));
            if (match == null)
                continue;

            ReflectionHelper.SetMemberValue(context.ContentPanel, "SelectedTrack", match);
            selectionSummary = $"track type={optionSet.OptionType.FullName} name=\"{ReadContentName(match)}\" localId=\"{ReadContentIdentifier(match, "LocalID")}\" managedId=\"{ReadContentIdentifier(match, "ManagedID")}\"";
            return true;
        }

        return false;
    }

    private List<object> GetDropdownDataOptions(object dropdown, Type contentType)
    {
        var method = AccessTools.Method(dropdown.GetType(), "GetOptionDataAs");
        if (method == null)
            return new List<object>();

        try
        {
            var genericMethod = method.MakeGenericMethod(contentType);
            var result = genericMethod.Invoke(dropdown, Array.Empty<object>());
            return ReflectionHelper.EnumerateAsObjects(result).ToList();
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"GetOptionDataAs<{contentType.FullName}> failed: {ex.GetType().Name}: {ex.Message}");
            return new List<object>();
        }
    }

    private bool MatchesContent(object option, ChangeRequest request, bool preferRaceName)
    {
        var name = ReadContentName(option);
        var localId = ReadContentIdentifier(option, "LocalID");
        var managedId = ReadContentIdentifier(option, "ManagedID");
        var trackDependency = ReadTrackDependency(option);

        if (preferRaceName && MatchesName(name, request.RaceName))
            return true;
        if (!preferRaceName && MatchesName(name, request.TrackName))
            return true;

        if (!string.IsNullOrWhiteSpace(request.WorkshopId))
        {
            var workshopId = request.WorkshopId;
            if (ContainsOrdinalIgnoreCase(localId, workshopId) ||
                ContainsOrdinalIgnoreCase(managedId, workshopId) ||
                ContainsOrdinalIgnoreCase(trackDependency, workshopId))
            {
                return true;
            }
        }

        return false;
    }

    private void DumpPopupOptions(PopupContext context)
    {
        if (context.Dropdown is not Dropdown dropdown)
            return;

        _log.Info("EXEC", $"Popup option count={dropdown.options.Count}");
        for (var index = 0; index < dropdown.options.Count; index++)
        {
            var option = dropdown.options[index];
            _log.Info("EXEC", $"  option[{index}] caption=\"{option.text}\"");
        }

        _log.Info("EXEC", $"Current SelectedEnvironment={FormatEnvironmentSummary(GetContentPanelMemberValue(context, "SelectedEnvironment"))}");
        _log.Info("EXEC", $"Current SelectedTrack={FormatOptionSummary(GetContentPanelMemberValue(context, "SelectedTrack"))}");
        _log.Info("EXEC", $"Current SelectedRace={FormatOptionSummary(GetContentPanelMemberValue(context, "SelectedRace"))}");
        _log.Info("EXEC", $"Current SelectedGameMode={ReflectionHelper.SafeDescribe(_describe, GetContentPanelMemberValue(context, "SelectedGameMode"))}");
        DumpSelectedContent(context);
        DumpEnvironmentOptions(context);

        foreach (var candidate in GetDropdownCandidates(context.Dropdown).Take(16))
            _log.Info("EXEC", $"dropdown[{candidate.Index}] caption=\"{candidate.Caption}\" data={FormatOptionSummary(candidate.Data)}");

        foreach (var optionSet in GetSelectionOptionSets(context, "SelectedTrack", "TrackQuickInfo", "GameContentEntry"))
            DumpOptionSet("track", optionSet);

        foreach (var optionSet in GetSelectionOptionSets(context, "SelectedRace", "RaceQuickInfo", "GameContentEntry"))
            DumpOptionSet("race", optionSet);
    }

    private void DumpOptionSet(string label, SelectionOptionSet optionSet)
    {
        _log.Info("EXEC", $"{label} option type={optionSet.OptionType.FullName} count={optionSet.Options.Count}");
        for (var index = 0; index < Math.Min(optionSet.Options.Count, 12); index++)
            _log.Info("EXEC", $"  {label}[{index}] {FormatOptionSummary(optionSet.Options[index])}");
    }

    private IEnumerable<SelectionOptionSet> GetSelectionOptionSets(PopupContext context, string memberName, params string[] fallbackTypeNames)
    {
        var seen = new HashSet<Type>();
        foreach (var optionType in ResolveSelectionTypes(context, memberName, fallbackTypeNames))
        {
            if (!seen.Add(optionType))
                continue;

            var options = GetDropdownDataOptions(context.Dropdown, optionType);
            if (options.Count == 0)
                continue;

            yield return new SelectionOptionSet(optionType, options);
        }
    }

    private IEnumerable<DropdownCandidate> GetDropdownCandidates(object? dropdownObject)
    {
        if (dropdownObject is not Dropdown dropdown)
            yield break;

        for (var index = 0; index < dropdown.options.Count; index++)
        {
            var option = dropdown.options[index];
            var data = ReflectionHelper.GetMemberValue(option, "Data") ??
                       ReflectionHelper.GetMemberValue(option, "data");

            yield return new DropdownCandidate(index, option.text ?? string.Empty, data);
        }
    }

    private bool MatchesDropdownCandidate(DropdownCandidate candidate, ChangeRequest request)
    {
        if (candidate.Data != null)
        {
            if (MatchesContent(candidate.Data, request, preferRaceName: true) ||
                MatchesContent(candidate.Data, request, preferRaceName: false))
            {
                return true;
            }
        }

        if (MatchesName(candidate.Caption, request.RaceName) || MatchesName(candidate.Caption, request.TrackName))
            return true;

        return false;
    }

    private bool MatchesEnvironmentCandidate(DropdownCandidate candidate, string targetEnvironmentName)
    {
        if (string.IsNullOrWhiteSpace(targetEnvironmentName))
            return false;

        if (MatchesName(candidate.Caption, targetEnvironmentName))
            return true;

        if (candidate.Data == null)
            return false;

        return MatchesName(ReadEnvironmentDisplayName(candidate.Data), targetEnvironmentName) ||
               MatchesName(ReadEnvironmentInternalName(candidate.Data), targetEnvironmentName);
    }

    private bool ApplyDropdownCandidate(PopupContext context, DropdownCandidate candidate)
    {
        var applied = false;

        if (candidate.Data != null)
        {
            applied |= TrySetDropdownSelectionByData(context.Dropdown, candidate.Data);
        }

        if (context.Dropdown is Dropdown dropdown)
        {
            try
            {
                if (dropdown.value != candidate.Index)
                    dropdown.value = candidate.Index;

                dropdown.RefreshShownValue();
                applied = true;
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"Failed to move dropdown to index {candidate.Index}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        TryNotifyContentSelected(context, candidate.Index);
        return applied;
    }

    private bool ApplyEnvironmentCandidate(PopupContext context, DropdownCandidate candidate, out string selectionSummary)
    {
        selectionSummary = string.Empty;
        if (context.EnvironmentDropdown == null)
            return false;

        var applied = false;
        if (candidate.Data != null)
        {
            TrySetSelectedEnvironment(context, candidate.Data);
            applied |= TrySetDropdownSelectionByData(context.EnvironmentDropdown, candidate.Data);
        }

        if (context.EnvironmentDropdown is Dropdown dropdown)
        {
            try
            {
                if (dropdown.value != candidate.Index)
                    dropdown.value = candidate.Index;

                dropdown.RefreshShownValue();
                applied = true;
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"Failed to move environment dropdown to index {candidate.Index}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (!TryInvokeSelectionMethod(context.ContentPanel, "OnEnvironmentSelected", candidate.Index))
            InvokeZeroArg(context.ContentPanel, "FillEnvironmentSelection");

        InvokeZeroArg(context.ContentPanel, "FillContentSelection");
        InvokeZeroArg(context.ContentPanel, "InvokeCurrentValues");

        selectionSummary = $"environment index={candidate.Index} caption=\"{candidate.Caption}\" data={FormatEnvironmentSummary(candidate.Data)}";
        return applied;
    }

    private void TrySetSelectedEnvironment(PopupContext context, object environmentData)
    {
        try
        {
            ReflectionHelper.SetMemberValue(context.ContentPanel, "SelectedEnvironment", environmentData);
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"Failed to apply SelectedEnvironment directly: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TrySetDropdownSelectionByData(object dropdown, object data)
    {
        var methods = dropdown.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == "SetSelectedOptionForData")
            .ToList();

        foreach (var method in methods)
        {
            try
            {
                if (method.IsGenericMethodDefinition)
                {
                    var generic = method.MakeGenericMethod(data.GetType());
                    generic.Invoke(dropdown, new[] { data });
                    _log.Info("EXEC", $"Selected dropdown data via {ReflectionHelper.FormatMethodSignature(generic)}");
                    return true;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 1)
                    continue;

                if (!parameters[0].ParameterType.IsInstanceOfType(data) && parameters[0].ParameterType != typeof(object))
                    continue;

                method.Invoke(dropdown, new[] { data });
                _log.Info("EXEC", $"Selected dropdown data via {ReflectionHelper.FormatMethodSignature(method)}");
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"SetSelectedOptionForData on {dropdown.GetType().FullName} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private void TryNotifyContentSelected(PopupContext context, int index)
    {
        if (TryInvokeSelectionMethod(context.ContentPanel, "OnContentSelected", index))
            return;

        InvokeZeroArg(context.ContentPanel, "InvokeCurrentValues");
    }

    private void DumpSelectedContent(PopupContext context)
    {
        var selectedContent = GetSelectedContentItems(context).ToList();
        _log.Info("EXEC", $"selectedContent count={selectedContent.Count}");
        for (var index = 0; index < Math.Min(selectedContent.Count, 8); index++)
            _log.Info("EXEC", $"  selectedContent[{index}] {FormatOptionSummary(selectedContent[index])}");
    }

    private void DumpEnvironmentOptions(PopupContext context)
    {
        if (context.EnvironmentDropdown is not Dropdown dropdown)
            return;

        _log.Info("EXEC", $"Environment option count={dropdown.options.Count}");
        foreach (var candidate in GetDropdownCandidates(context.EnvironmentDropdown).Take(12))
            _log.Info("EXEC", $"environment[{candidate.Index}] caption=\"{candidate.Caption}\" data={FormatEnvironmentSummary(candidate.Data)}");
    }

    private object? GetLiveSessionSettings()
    {
        if (MultiplayerRuntimeState.TryGetLatestGameSettingsSnapshot(out var cachedSettings, out var cachedSource, out var capturedUtc))
        {
            _log.Info("EXEC", $"Using cached game settings snapshot from {cachedSource} capturedAt={capturedUtc:O}");
            return cachedSettings;
        }

        var containerType = AccessTools.TypeByName("CurrentContentContainer");
        if (containerType == null)
            return null;

        foreach (var container in ReflectionHelper.GetLiveObjects(containerType))
        {
            var sessionSettings = ReflectionHelper.GetMemberValue(container, "SessionSettings") ??
                                  ReflectionHelper.GetMemberValue(container, "sessionSettings");
            if (sessionSettings != null)
                return sessionSettings;
        }

        return null;
    }

    private bool HasSelectableContent(PopupContext context)
    {
        return GetPopupOptionCount(context) > 0 || GetSelectedContentCount(context) > 0;
    }

    private int GetPopupOptionCount(PopupContext context)
    {
        return context.Dropdown is Dropdown dropdown ? dropdown.options.Count : 0;
    }

    private int GetSelectedContentCount(PopupContext context)
    {
        return GetSelectedContentItems(context).Count();
    }

    private void InvokeZeroArg(object instance, string methodName)
    {
        try
        {
            var method = ReflectionHelper.FindMethod(instance.GetType(), methodName, 0);
            if (method == null)
                return;

            method.Invoke(instance, Array.Empty<object>());
            _log.Info("EXEC", $"Invoked {ReflectionHelper.FormatMethodSignature(method)}");
        }
        catch (Exception ex)
        {
            _log.Warn("EXEC", $"{methodName} invocation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TryInvokeSingleArgument(object instance, string methodName, object argument)
    {
        var methods = instance.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == methodName && method.GetParameters().Length == 1)
            .ToList();

        foreach (var method in methods)
        {
            try
            {
                var parameterType = method.GetParameters()[0].ParameterType;
                if (!parameterType.IsInstanceOfType(argument) && !parameterType.IsAssignableFrom(argument.GetType()))
                    continue;

                method.Invoke(instance, new[] { argument });
                _log.Info("EXEC", $"Invoked {ReflectionHelper.FormatMethodSignature(method)} with {argument.GetType().FullName}");
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"{methodName} invocation failed on {instance.GetType().FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private bool TryInvokeSelectionMethod(object instance, string methodName, object argument)
    {
        var methods = instance.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == methodName && method.GetParameters().Length == 1)
            .ToList();

        foreach (var method in methods)
        {
            try
            {
                var parameterType = method.GetParameters()[0].ParameterType;
                if (parameterType == typeof(int) && argument is int integer)
                {
                    method.Invoke(instance, new object[] { integer });
                    _log.Info("EXEC", $"Invoked {ReflectionHelper.FormatMethodSignature(method)} with index={integer}");
                    return true;
                }

                if (parameterType.IsInstanceOfType(argument) || parameterType.IsAssignableFrom(argument.GetType()))
                {
                    method.Invoke(instance, new[] { argument });
                    _log.Info("EXEC", $"Invoked {ReflectionHelper.FormatMethodSignature(method)} with {argument.GetType().FullName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("EXEC", $"{methodName} invocation failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return false;
    }

    private IEnumerable<Type> ResolveSelectionTypes(PopupContext context, string memberName, params string[] fallbackTypeNames)
    {
        var declaredType = ReflectionHelper.GetMemberType(context.ContentPanel, memberName);
        if (declaredType != null && declaredType != typeof(object))
            yield return declaredType;

        foreach (var candidateType in GetDropdownCandidates(context.Dropdown)
                     .Select(candidate => candidate.Data?.GetType())
                     .Where(type => type != null)
                     .Cast<Type>())
        {
            yield return candidateType;
        }

        foreach (var selectedType in GetSelectedContentItems(context).Select(item => item.GetType()))
            yield return selectedType;

        var currentValue = GetContentPanelMemberValue(context, memberName);
        if (currentValue != null)
            yield return currentValue.GetType();

        foreach (var typeName in fallbackTypeNames)
        {
            var resolved = _discovery.TryResolveKnownType(typeName);
            if (resolved != null)
                yield return resolved;
        }
    }

    private IEnumerable<object> GetSelectedContentItems(PopupContext context)
    {
        return ReflectionHelper.EnumerateAsObjects(GetContentPanelMemberValue(context, "selectedContent"));
    }

    private object? GetContentPanelMemberValue(PopupContext context, string memberName)
    {
        if (ReflectionHelper.TryGetMemberValue(context.ContentPanel, memberName, out var value, out var exception))
            return value;

        if (exception != null)
            _log.Warn("EXEC", $"Reading {context.ContentPanel.GetType().FullName}.{memberName} failed: {exception.GetType().Name}: {exception.Message}");

        return null;
    }

    private static string FormatOptionSummary(object? option)
    {
        if (option == null)
            return "<null>";

        var builder = new StringBuilder();
        builder.Append(option.GetType().FullName);
        builder.Append(" name=\"").Append(ReadContentName(option)).Append('"');

        var localId = ReadContentIdentifier(option, "LocalID");
        if (!string.IsNullOrWhiteSpace(localId))
            builder.Append(" localId=\"").Append(localId).Append('"');

        var managedId = ReadContentIdentifier(option, "ManagedID");
        if (!string.IsNullOrWhiteSpace(managedId))
            builder.Append(" managedId=\"").Append(managedId).Append('"');

        var trackDependency = ReadTrackDependency(option);
        if (!string.IsNullOrWhiteSpace(trackDependency))
            builder.Append(" trackDependency=\"").Append(trackDependency).Append('"');

        return builder.ToString();
    }

    private static string FormatEnvironmentSummary(object? environment)
    {
        if (environment == null)
            return "<null>";

        var displayName = ReadEnvironmentDisplayName(environment);
        var internalName = ReadEnvironmentInternalName(environment);
        if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(internalName))
            return environment.ToString() ?? environment.GetType().FullName ?? "<unknown-environment>";

        var builder = new StringBuilder();
        builder.Append(environment.GetType().FullName);
        if (!string.IsNullOrWhiteSpace(displayName))
            builder.Append(" displayName=\"").Append(displayName).Append('"');
        if (!string.IsNullOrWhiteSpace(internalName))
            builder.Append(" internalName=\"").Append(internalName).Append('"');
        return builder.ToString();
    }

    private void DumpKnownLiveObjects(string category, string typeName)
    {
        var type = _discovery.TryResolveKnownType(typeName);
        foreach (var liveObject in ReflectionHelper.GetLiveObjects(type).Take(3))
        {
            _log.Info(category, $"live object {ReflectionHelper.DescribeObjectIdentity(liveObject)}");
            _log.Info(category, $"snapshot {ReflectionHelper.SafeDescribe(_describe, liveObject)}");
        }
    }

    private static int GetInstanceId(object value)
    {
        return value is UnityEngine.Object unityObject ? unityObject.GetInstanceID() : value.GetHashCode();
    }

    private static bool IsPopupActive(object popup)
    {
        if (popup == null)
            return false;

        if (popup is UnityEngine.Object unityObject && unityObject == null)
            return false;

        if (popup is Behaviour behaviour)
        {
            try
            {
                return behaviour.isActiveAndEnabled || (behaviour.gameObject != null && behaviour.gameObject.activeInHierarchy);
            }
            catch
            {
                return false;
            }
        }

        if (!ReflectionHelper.TryGetMemberValue(popup, "gameObject", out var gameObjectValue, out _))
            gameObjectValue = null;

        var gameObject = gameObjectValue as GameObject;
        if (gameObject != null)
        {
            try
            {
                return gameObject.activeInHierarchy;
            }
            catch
            {
                return false;
            }
        }

        if (!ReflectionHelper.TryGetMemberValue(popup, "isActiveAndEnabled", out var isActive, out _))
            return false;

        if (isActive is bool activeValue)
            return activeValue;

        return false;
    }

    private static bool HasOnSetGameCallback(object popup)
    {
        if (popup == null)
            return false;

        if (popup is UnityEngine.Object unityObject && unityObject == null)
            return false;

        return ReflectionHelper.TryGetMemberValue(popup, "onSetGame", out var callback, out _) && callback is Delegate;
    }

    private static string ReadContentName(object option)
    {
        return ReflectionHelper.GetMemberValue(option, "Name") as string
               ?? ReflectionHelper.GetMemberValue(option, "name") as string
               ?? option.ToString()
               ?? string.Empty;
    }

    private static string ReadContentIdentifier(object option, string memberName)
    {
        if (ReflectionHelper.TryGetNestedString(option, out var text, memberName, "str"))
            return text;

        return string.Empty;
    }

    private static string ReadTrackDependency(object option)
    {
        if (ReflectionHelper.TryGetNestedString(option, out var text, "TrackDependency", "str"))
            return text;

        return string.Empty;
    }

    private static string ReadEnvironmentDisplayName(object environment)
    {
        return ReflectionHelper.GetMemberValue(environment, "DisplayName") as string
               ?? ReflectionHelper.GetMemberValue(environment, "displayName") as string
               ?? ReflectionHelper.GetMemberValue(environment, "environmentDisplayName") as string
               ?? string.Empty;
    }

    private static string ReadEnvironmentInternalName(object environment)
    {
        return ReflectionHelper.GetMemberValue(environment, "name") as string
               ?? ReflectionHelper.GetMemberValue(environment, "Name") as string
               ?? environment.ToString()
               ?? string.Empty;
    }

    private static bool ContainsOrdinalIgnoreCase(string source, string match)
    {
        return !string.IsNullOrWhiteSpace(source) &&
               source.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool MatchesName(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return false;

        return string.Equals(source, target, StringComparison.OrdinalIgnoreCase) ||
               source.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool HasContentTarget(ChangeRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.TrackName) ||
               !string.IsNullOrWhiteSpace(request.RaceName) ||
               !string.IsNullOrWhiteSpace(request.WorkshopId);
    }

    // ── Catalog snapshot ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads the current multiplayer setup popup and returns a structured catalog
    /// of all available environments, tracks, and races.
    /// Returns false if the popup is not currently open/available.
    /// </summary>
    public bool TryCatalogSnapshot(out Dictionary<string, object?> catalog)
    {
        catalog = new Dictionary<string, object?>();

        if (!TryAcquirePopup(out var context, out var acquireStatus) || acquireStatus != PopupAcquireStatus.Acquired)
        {
            _log.Info("CATALOG", $"TryCatalogSnapshot: popup not acquired (status={acquireStatus})");
            return false;
        }

        // Save original environment index so we can restore it after scanning
        var originalEnvIndex = -1;
        if (context.EnvironmentDropdown is Dropdown envDropdown)
            originalEnvIndex = envDropdown.value;

        var envCandidates = GetDropdownCandidates(context.EnvironmentDropdown).ToList();
        var environments  = new List<Dictionary<string, object?>>();

        foreach (var envCandidate in envCandidates)
        {
            // Switch the popup to this environment so tracks/races update
            ApplyEnvironmentCandidate(context, envCandidate, out _);

            var entry = new Dictionary<string, object?> { ["caption"] = envCandidate.Caption };
            if (envCandidate.Data != null)
            {
                var displayName  = ReadEnvironmentDisplayName(envCandidate.Data);
                var internalName = ReadEnvironmentInternalName(envCandidate.Data);
                if (!string.IsNullOrWhiteSpace(displayName))  entry["display_name"]  = displayName;
                if (!string.IsNullOrWhiteSpace(internalName)) entry["internal_name"] = internalName;
            }

            // Tracks for this environment
            var tracks = new List<Dictionary<string, object?>>();
            foreach (var optionSet in GetSelectionOptionSets(context, "SelectedTrack", "TrackQuickInfo", "GameContentEntry"))
            {
                foreach (var option in optionSet.Options)
                    tracks.Add(new Dictionary<string, object?>
                    {
                        ["name"]             = ReadContentName(option),
                        ["local_id"]         = ReadContentIdentifier(option, "LocalID"),
                        ["track_dependency"] = ReadTrackDependency(option),
                    });
                break;
            }
            entry["tracks"] = tracks;

            entry["races"] = null; // populated once below, not per-environment

            environments.Add(entry);
            _log.Info("CATALOG", $"  env \"{envCandidate.Caption}\": tracks={tracks.Count}");
        }

        // Restore the original environment selection
        if (originalEnvIndex >= 0 && originalEnvIndex < envCandidates.Count)
            ApplyEnvironmentCandidate(context, envCandidates[originalEnvIndex], out _);

        var totalTracks = 0;
        foreach (var e in environments)
            totalTracks += (e["tracks"] as List<Dictionary<string, object?>>)?.Count ?? 0;

        // Game modes — enumerate the GameMode enum rather than duplicating track data
        var gameModes = new List<Dictionary<string, object?>>();
        var gameModeType = _discovery.TryResolveKnownType("Liftoff.Multiplayer.GameMode");
        if (gameModeType != null)
        {
            foreach (var name in Enum.GetNames(gameModeType))
                gameModes.Add(new Dictionary<string, object?> { ["name"] = name });
        }

        catalog["environments"] = environments;
        catalog["game_modes"]   = gameModes;
        _log.Info("CATALOG", $"TryCatalogSnapshot: full scan complete — environments={environments.Count} total_tracks={totalTracks} game_modes={gameModes.Count}");
        return true;
    }

    private sealed class ChangeRequest
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string TrackName { get; set; } = string.Empty;
        public string RaceName { get; set; } = string.Empty;
        public string WorkshopId { get; set; } = string.Empty;
        public bool CycleNext { get; set; }
    }

    private sealed class SelectionOptionSet
    {
        public SelectionOptionSet(Type optionType, List<object> options)
        {
            OptionType = optionType;
            Options = options;
        }

        public Type OptionType { get; }
        public List<object> Options { get; }
    }

    private sealed class DropdownCandidate
    {
        public DropdownCandidate(int index, string caption, object? data)
        {
            Index = index;
            Caption = caption;
            Data = data;
        }

        public int Index { get; }
        public string Caption { get; }
        public object? Data { get; }
    }

    private struct PopupContext
    {
        public object Popup;
        public object ContentPanel;
        public object Dropdown;
        public object? EnvironmentDropdown;
        public object? Controller;
        public string? ControllerMethod;
    }

    private sealed class ControllerCandidate
    {
        public ControllerCandidate(string typeName, string openMethodName)
        {
            TypeName = typeName;
            OpenMethodName = openMethodName;
        }

        public string TypeName { get; }
        public string OpenMethodName { get; }
        public object Controller { get; private set; } = null!;

        public ControllerCandidate WithController(object controller)
        {
            return new ControllerCandidate(TypeName, OpenMethodName)
            {
                Controller = controller
            };
        }
    }
}
