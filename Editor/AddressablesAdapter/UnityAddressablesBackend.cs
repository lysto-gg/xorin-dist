using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using Xorin.Editor.Addressables;
using RuntimeAddressables = UnityEngine.AddressableAssets.Addressables;

namespace Xorin.Editor.AddressablesAdapter
{
    [InitializeOnLoad]
    internal static class AddressablesAdapterBootstrap
    {
        static AddressablesAdapterBootstrap()
        {
            try
            {
                AddressablesBackendRegistry.Register(new UnityAddressablesBackend());
                SerializedPropertyExtensionRegistry.Register(
                    new AddressableReferencePropertyWriter());
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogException(exception);
            }
        }
    }

    internal sealed class UnityAddressablesBackend : IAddressablesMutationBackend,
        IAddressablesValidationBackend, IAddressablesBuildBackend,
        IAddressablesRuntimeSceneBackend
    {
        private const string UndoLabelPrefix = "Xorin: ";
        private const string CanonicalSettingsFolder = "Assets/AddressableAssetsData";
        private const string CanonicalSettingsPath =
            CanonicalSettingsFolder + "/AddressableAssetSettings.asset";
        private const string EditorBuildSettingsPath =
            "ProjectSettings/EditorBuildSettings.asset";

        private readonly Func<AddressableAssetSettings> _settingsProvider;
        private readonly Func<AddressableAssetSettings> _settingsCreator;
        private readonly Action<AddressableAssetSettings> _settingsRegistrar;
        private readonly Action _saveAssets;
        private readonly Func<string> _versionProvider;
        private readonly Func<AddressableAssetSettings, AddressablesPlayerBuildResult>
            _fullBuild;
        private readonly Func<AddressableAssetSettings, string, AddressablesPlayerBuildResult>
            _contentUpdateBuild;
        private readonly Func<BuildTarget> _activeBuildTargetProvider;
        private AsyncOperationHandle<SceneInstance>? _activeSceneLoad;
        private Action<AddressablesSceneLoadResult> _sceneLoadCompletion;
        private bool _sceneLoadCancelled;

        public bool IsSceneLoadActive => _activeSceneLoad.HasValue;

        internal UnityAddressablesBackend(
            Func<AddressableAssetSettings> settingsProvider = null,
            Func<string> versionProvider = null,
            Func<AddressableAssetSettings, AddressablesPlayerBuildResult> fullBuild = null,
            Func<AddressableAssetSettings, string, AddressablesPlayerBuildResult>
                contentUpdateBuild = null,
            Func<BuildTarget> activeBuildTargetProvider = null,
            Func<AddressableAssetSettings> settingsCreator = null,
            Action<AddressableAssetSettings> settingsRegistrar = null,
            Action saveAssets = null)
        {
            _settingsProvider = settingsProvider
                ?? (() => AddressableAssetSettingsDefaultObject.GetSettings(false));
            _versionProvider = versionProvider ?? ResolvePackageVersion;
            _fullBuild = fullBuild ?? BuildFullContent;
            _contentUpdateBuild = contentUpdateBuild ?? ContentUpdateScript.BuildContentUpdate;
            _activeBuildTargetProvider = activeBuildTargetProvider
                ?? (() => EditorUserBuildSettings.activeBuildTarget);
            _settingsCreator = settingsCreator ?? CreateCanonicalSettings;
            _settingsRegistrar = settingsRegistrar
                ?? (settings => AddressableAssetSettingsDefaultObject.Settings = settings);
            _saveAssets = saveAssets ?? AssetDatabase.SaveAssets;
        }

        public AddressablesCapability GetCapability()
        {
            string version = _versionProvider();
            if (!AddressablesBackendRegistry.IsVersionSupported(version))
            {
                return new AddressablesCapability
                {
                    Installed = true,
                    Supported = false,
                    State = AddressablesState.UnsupportedVersion,
                    PackageVersion = version,
                    Message = $"Addressables {version ?? "unknown"} is older than Xorin's supported minimum {AddressablesBackendRegistry.MinimumSupportedVersion}."
                };
            }
            if (EditorApplication.isCompiling
                || EditorApplication.isUpdating
                || BuildPipeline.isBuildingPlayer)
            {
                return new AddressablesCapability
                {
                    Installed = true,
                    Supported = true,
                    State = AddressablesState.EditorBusy,
                    PackageVersion = version,
                    Message = "Addressables settings are temporarily unavailable while Unity compiles, imports, or builds."
                };
            }

            var settings = _settingsProvider();
            if (settings == null)
            {
                return new AddressablesCapability
                {
                    Installed = true,
                    Supported = true,
                    State = AddressablesState.SettingsNotCreated,
                    PackageVersion = version,
                    Message = "Addressables is installed, but the project has no default settings asset."
                };
            }

            return ReadyCapability(version);
        }

        public AddressablesSnapshot Inspect(AddressablesInspectQuery query)
        {
            query = query ?? new AddressablesInspectQuery();
            var capability = GetCapability();
            if (capability.State != AddressablesState.Ready)
                return new AddressablesSnapshot { Capability = capability };

            var settings = _settingsProvider();
            if (settings == null)
            {
                return new AddressablesSnapshot
                {
                    Capability = new AddressablesCapability
                    {
                        Installed = true,
                        Supported = true,
                        State = AddressablesState.SettingsNotCreated,
                        PackageVersion = capability.PackageVersion,
                        Message = "Addressables settings became unavailable during inspection."
                    }
                };
            }

            string scope = query.Scope ?? AddressablesScope.Summary;
            bool includeProfiles = scope == AddressablesScope.Profiles || scope == AddressablesScope.All;
            bool includeGroups = scope == AddressablesScope.Groups || scope == AddressablesScope.All;
            bool includeEntries = scope == AddressablesScope.Entries || scope == AddressablesScope.All;
            bool includeScenes = scope == AddressablesScope.Scenes || scope == AddressablesScope.All;
            int limit = Math.Max(1, query.Limit);

            var allGroups = (settings.groups ?? new List<AddressableAssetGroup>())
                .Where(group => group != null)
                .OrderBy(group => group.Name, StringComparer.Ordinal)
                .ToList();
            var selectedGroups = allGroups
                .Where(group => string.IsNullOrEmpty(query.Group)
                    || string.Equals(group.Name, query.Group, StringComparison.Ordinal))
                .ToList();

            var allEntries = allGroups
                .SelectMany(group => (group.entries ?? Array.Empty<AddressableAssetEntry>())
                    .Where(entry => entry != null)
                    .Select(entry => new { Group = group, Entry = entry }))
                .ToList();
            var matchingEntries = allEntries
                .Where(item => string.IsNullOrEmpty(query.Group)
                    || string.Equals(item.Group.Name, query.Group, StringComparison.Ordinal))
                .Where(item => string.IsNullOrEmpty(query.Label)
                    || (item.Entry.labels != null && item.Entry.labels.Contains(query.Label)))
                .Where(item => string.IsNullOrEmpty(query.PathPrefix)
                    || (item.Entry.AssetPath ?? string.Empty).StartsWith(
                        query.PathPrefix, StringComparison.Ordinal))
                .OrderBy(item => item.Entry.AssetPath, StringComparer.Ordinal)
                .ThenBy(item => item.Entry.address, StringComparer.Ordinal)
                .ToList();

            var matchingScenes = includeScenes
                ? GatherAddressableScenes(selectedGroups)
                    .Where(item => string.IsNullOrEmpty(query.Label)
                        || (item.Entry.labels != null && item.Entry.labels.Contains(query.Label)))
                    .Where(item => string.IsNullOrEmpty(query.PathPrefix)
                        || (item.Entry.AssetPath ?? string.Empty).StartsWith(
                            query.PathPrefix, StringComparison.Ordinal))
                    .OrderBy(item => item.Entry.AssetPath, StringComparer.Ordinal)
                    .ThenBy(item => item.Entry.address, StringComparer.Ordinal)
                    .ToList()
                : new List<GroupedEntry>();

            var profileNames = settings.profileSettings == null
                ? new List<string>()
                : settings.profileSettings.GetAllProfileNames()
                    .Where(name => !string.IsNullOrEmpty(name))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
            string activeProfile = settings.profileSettings == null
                ? null
                : settings.profileSettings.GetProfileName(settings.activeProfileId);

            var returnedProfiles = includeProfiles
                ? profileNames.Take(limit).ToList()
                : new List<string>();
            var returnedGroups = includeGroups
                ? selectedGroups.Take(limit).Select(group => BuildGroup(settings, group)).ToList()
                : new List<AddressablesGroupInfo>();
            var returnedEntries = includeEntries
                ? matchingEntries.Take(limit).Select(item => BuildEntry(item.Group, item.Entry)).ToList()
                : new List<AddressablesEntryInfo>();
            var returnedScenes = includeScenes
                ? matchingScenes.Take(limit).Select(item => BuildEntry(item.Group, item.Entry)).ToList()
                : new List<AddressablesEntryInfo>();

            bool truncated = (includeProfiles && profileNames.Count > returnedProfiles.Count)
                || (includeGroups && selectedGroups.Count > returnedGroups.Count)
                || (includeEntries && matchingEntries.Count > returnedEntries.Count)
                || (includeScenes && matchingScenes.Count > returnedScenes.Count);

            return new AddressablesSnapshot
            {
                Capability = capability,
                SettingsAssetPath = string.IsNullOrEmpty(settings.AssetPath)
                    ? AssetDatabase.GetAssetPath(settings)
                    : settings.AssetPath,
                SettingsGuid = AssetDatabase.AssetPathToGUID(
                    string.IsNullOrEmpty(settings.AssetPath)
                        ? AssetDatabase.GetAssetPath(settings)
                        : settings.AssetPath),
                ActiveProfile = activeProfile,
                DefaultGroup = settings.DefaultGroup?.Name,
                DefaultGroupGuid = settings.DefaultGroup?.Guid,
                GroupCount = allGroups.Count,
                EntryCount = allEntries.Count,
                MatchingGroupCount = selectedGroups.Count,
                MatchingEntryCount = matchingEntries.Count,
                MatchingSceneCount = matchingScenes.Count,
                IncludesProfiles = includeProfiles,
                IncludesGroups = includeGroups,
                IncludesEntries = includeEntries,
                IncludesScenes = includeScenes,
                Truncated = truncated,
                Profiles = returnedProfiles,
                Groups = returnedGroups,
                Entries = returnedEntries,
                Scenes = returnedScenes,
                ConfigurationAssetPaths = CollectSettingsAssetInventory(settings)
            };
        }

        public AddressablesSceneResolution ResolveScene(string exactAddress)
        {
            AddressablesCapability capability = GetCapability();
            if (capability.State != AddressablesState.Ready)
            {
                return ResolutionFailure(capability,
                    AddressablesBackendRegistry.CapabilityErrorCode(capability),
                    capability.Message ?? "Addressables is unavailable.");
            }

            if (string.IsNullOrWhiteSpace(exactAddress))
            {
                return ResolutionFailure(capability,
                    AddressablesSceneLoadError.AddressNotFound,
                    "An exact Addressables scene address is required.");
            }

            AddressableAssetSettings settings = _settingsProvider();
            if (settings == null)
            {
                return ResolutionFailure(capability,
                    AddressablesSceneLoadError.SettingsMissing,
                    "Addressables settings became unavailable during scene resolution.");
            }

            var matches = GatherAddressableEntries(settings.groups)
                .Where(item => string.Equals(
                    item.Entry.address, exactAddress, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
            {
                return ResolutionFailure(capability,
                    AddressablesSceneLoadError.AddressNotFound,
                    $"No Addressables entry has the exact address '{exactAddress}'.",
                    "Run inspect_addressables with scope 'scenes' and use an exact returned address.");
            }
            if (matches.Count > 1)
            {
                return ResolutionFailure(capability,
                    AddressablesSceneLoadError.AddressAmbiguous,
                    $"Addressables contains {matches.Count} entries with the exact address '{exactAddress}'.",
                    "Assign unique addresses before loading the scene.");
            }

            GroupedEntry match = matches[0];
            if (!match.Entry.IsScene
                || string.IsNullOrEmpty(match.Entry.AssetPath)
                || string.IsNullOrEmpty(match.Entry.guid))
            {
                return ResolutionFailure(capability,
                    AddressablesSceneLoadError.AddressNotScene,
                    $"Addressables entry '{exactAddress}' is not a valid scene entry.",
                    "Use an exact scene address returned by inspect_addressables.");
            }

            return new AddressablesSceneResolution
            {
                Capability = capability,
                Scene = new AddressablesResolvedScene
                {
                    Guid = match.Entry.guid,
                    AssetPath = match.Entry.AssetPath,
                    Address = match.Entry.address,
                    Group = match.Group.Name
                }
            };
        }

        public AddressablesSceneLoadResult StartSceneLoad(
            AddressablesSceneLoadRequest request,
            Action<AddressablesSceneLoadResult> completion)
        {
            if (request == null || request.ResolvedScene == null)
                return LoadFailure(request, AddressablesSceneLoadError.LoadFailed,
                    "The resolved Addressables scene evidence is missing.");
            if (completion == null)
                return LoadFailure(request, AddressablesSceneLoadError.LoadFailed,
                    "The Addressables scene completion callback is missing.");
            if (IsSceneLoadActive)
                return LoadFailure(request, AddressablesSceneLoadError.LoadFailed,
                    "Another Addressables scene load is already active.");

            AddressablesSceneResolution current = ResolveScene(request.Address);
            if (!current.Success
                || !string.Equals(current.Scene.Guid, request.ResolvedScene.Guid,
                    StringComparison.Ordinal)
                || !string.Equals(current.Scene.AssetPath, request.ResolvedScene.AssetPath,
                    StringComparison.Ordinal))
            {
                return LoadFailure(request,
                    current.ErrorCode ?? AddressablesSceneLoadError.LoadFailed,
                    current.Error ?? "The resolved Addressables scene changed before loading.",
                    current.Suggestion);
            }

            try
            {
                LoadSceneMode loadMode = request.Mode == AddressablesSceneLoadMode.Additive
                    ? LoadSceneMode.Additive
                    : LoadSceneMode.Single;
                _sceneLoadCancelled = false;
                _sceneLoadCompletion = completion;
                AsyncOperationHandle<SceneInstance> handle = RuntimeAddressables.LoadSceneAsync(
                    request.Address, loadMode, true);
                _activeSceneLoad = handle;
                handle.Completed += completed => CompleteSceneLoad(request, completed);
                return null;
            }
            catch (Exception exception)
            {
                _activeSceneLoad = null;
                _sceneLoadCompletion = null;
                return LoadFailure(request, AddressablesSceneLoadError.LoadFailed,
                    $"Addressables could not start scene key '{request.Address}': {exception.Message}");
            }
        }

        public void CancelSceneLoad()
        {
            if (!_activeSceneLoad.HasValue) return;
            _sceneLoadCancelled = true;
            _sceneLoadCompletion = null;
            // The registered completion callback owns cleanup and clears the handle.
        }

        private void CompleteSceneLoad(AddressablesSceneLoadRequest request,
            AsyncOperationHandle<SceneInstance> handle)
        {
            Action<AddressablesSceneLoadResult> completion = _sceneLoadCompletion;
            _sceneLoadCompletion = null;
            _activeSceneLoad = null;

            if (_sceneLoadCancelled || completion == null)
            {
                CleanupFailedSceneLoad(handle);
                return;
            }

            AddressablesSceneLoadResult result;
            try
            {
                result = VerifyCompletedSceneLoad(request, handle);
            }
            catch (Exception exception)
            {
                result = LoadFailure(request, AddressablesSceneLoadError.LoadFailed,
                    $"Addressables scene verification failed: {exception.Message}");
            }

            if (!result.Success)
                CleanupFailedSceneLoad(handle);
            completion(result);
        }

        private static AddressablesSceneLoadResult VerifyCompletedSceneLoad(
            AddressablesSceneLoadRequest request,
            AsyncOperationHandle<SceneInstance> handle)
        {
            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                string detail = handle.OperationException?.Message;
                return LoadFailure(request, AddressablesSceneLoadError.LoadFailed,
                    string.IsNullOrEmpty(detail)
                        ? $"Addressables could not load scene key '{request.Address}'."
                        : $"Addressables could not load scene key '{request.Address}': {detail}");
            }

            Scene scene = handle.Result.Scene;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return LoadFailure(request,
                    AddressablesSceneLoadError.VerificationFailed,
                    $"Addressables reported success for '{request.Address}', but the scene is not valid and loaded.");
            }

            bool mustBeActive = request.Mode == AddressablesSceneLoadMode.Single
                || request.MakeActive;
            if (mustBeActive && SceneManager.GetActiveScene() != scene
                && !SceneManager.SetActiveScene(scene))
            {
                var activationFailure = LoadFailure(request,
                    AddressablesSceneLoadError.ActivationFailed,
                    $"Scene '{scene.name}' loaded, but Unity could not make it active.");
                PopulateSceneEvidence(activationFailure, scene);
                return activationFailure;
            }

            Scene active = SceneManager.GetActiveScene();
            bool pathMatches = string.Equals(scene.path,
                request.ResolvedScene.AssetPath, StringComparison.Ordinal);
            bool activeMatches = !mustBeActive || active == scene;
            if (!pathMatches || !activeMatches)
            {
                var verificationFailure = LoadFailure(request,
                    AddressablesSceneLoadError.VerificationFailed,
                    $"Scene '{scene.name}' loaded, but its path or active-scene state did not match the request.");
                PopulateSceneEvidence(verificationFailure, scene);
                verificationFailure.ActiveScene = active.name;
                return verificationFailure;
            }

            return new AddressablesSceneLoadResult
            {
                Success = true,
                Address = request.Address,
                SceneName = scene.name,
                ScenePath = scene.path,
                LoadMode = request.Mode,
                OperationStatus = "succeeded",
                ActiveScene = active.name,
                Verified = true
            };
        }

        private static void CleanupFailedSceneLoad(AsyncOperationHandle<SceneInstance> handle)
        {
            if (!handle.IsValid()) return;
            try
            {
                if (handle.Status == AsyncOperationStatus.Succeeded
                    && handle.Result.Scene.IsValid())
                    RuntimeAddressables.UnloadSceneAsync(handle, true);
                else
                    RuntimeAddressables.Release(handle);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Xorin] Addressables scene cleanup failed: {exception.Message}");
            }
        }

        private static AddressablesSceneResolution ResolutionFailure(
            AddressablesCapability capability, string errorCode, string error,
            string suggestion = null)
        {
            return new AddressablesSceneResolution
            {
                Capability = capability,
                ErrorCode = errorCode,
                Error = error,
                Suggestion = suggestion
            };
        }

        private static AddressablesSceneLoadResult LoadFailure(
            AddressablesSceneLoadRequest request, string errorCode, string error,
            string suggestion = null)
        {
            return new AddressablesSceneLoadResult
            {
                Success = false,
                ErrorCode = errorCode,
                Error = error,
                Suggestion = suggestion,
                Address = request?.Address,
                LoadMode = request?.Mode,
                OperationStatus = "failed"
            };
        }

        private static void PopulateSceneEvidence(
            AddressablesSceneLoadResult result, Scene scene)
        {
            result.SceneName = scene.name;
            result.ScenePath = scene.path;
        }

        public AddressablesValidationResult Validate(AddressablesValidationRequest request)
        {
            request = request ?? new AddressablesValidationRequest();
            var capability = GetCapability();
            var result = new AddressablesValidationResult { Capability = capability };
            if (capability.State != AddressablesState.Ready)
            {
                result.Error = capability.Message;
                return result;
            }

            var settings = _settingsProvider();
            if (settings == null)
            {
                result.Error = "Addressables settings became unavailable during validation.";
                return result;
            }

            var findings = new List<AddressablesValidationFinding>();
            var groups = settings.groups ?? new List<AddressableAssetGroup>();
            foreach (var missingGroup in groups.Where(group => group == null))
            {
                AddFinding(findings, "missing_group_asset",
                    AddressablesValidationSeverity.Error,
                    "Addressables settings contain a missing group asset.",
                    suggestion: "Open Addressables Groups and remove or restore the missing group reference.");
            }

            var validGroups = groups.Where(group => group != null).ToList();
            var entries = validGroups
                .SelectMany(group => (group.entries ?? Array.Empty<AddressableAssetEntry>())
                    .Where(entry => entry != null)
                    .Select(entry => new GroupedEntry(group, entry)))
                .ToList();
            var runtimeEntries = AllAddressableEntries(settings)
                .Select(entry => new GroupedEntry(entry.parentGroup, entry))
                .ToList();
            result.CheckedGroupCount = validGroups.Count;
            result.CheckedEntryCount = entries.Count;

            ValidateEntries(entries, findings);
            ValidateDuplicateAddresses(runtimeEntries, findings);
            ValidateSceneOverlap(entries, findings);
            ValidateGroupPaths(
                settings, validGroups, findings, settings.activeProfileId);
            ValidateCodeKeys(request.CodeKeys, runtimeEntries, findings);
            ValidateUnresolvedKeyEvidence(request.UnresolvedKeyEvidence, findings);
            result.CheckedCodeKeyCount = request.CodeKeys?.Count ?? 0;
            result.CheckedReferenceCount = ValidateAssetReferences(
                settings, request.AssetPaths, findings);

            result.TotalFindingCount = findings.Count;
            result.HasErrors = findings.Any(finding =>
                finding.Severity == AddressablesValidationSeverity.Error);
            result.Findings = findings.Take(Math.Max(1, request.Limit)).ToArray();
            result.Truncated = findings.Count > result.Findings.Count;
            return result;
        }

        public AddressablesBuildPlan PrepareBuild(AddressablesBuildRequest request)
        {
            var capability = GetCapability();
            var plan = new AddressablesBuildPlan
            {
                Capability = capability,
                Request = request
            };
            if (capability.State != AddressablesState.Ready)
            {
                plan.Error = capability.Message ?? "Addressables is unavailable.";
                return plan;
            }
            if (request == null)
            {
                plan.Error = "Addressables build request is missing.";
                return plan;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                plan.Error = "Addressables content cannot be built while Unity is entering or in Play Mode.";
                return plan;
            }

            var settings = _settingsProvider();
            if (settings == null)
            {
                plan.Error = "Addressables settings became unavailable during build validation.";
                return plan;
            }
            string profileId = settings.profileSettings?.GetProfileId(request.Profile);
            if (string.IsNullOrEmpty(profileId))
            {
                plan.Error = $"Addressables profile '{request.Profile}' does not exist.";
                plan.Suggestion = "Run inspect_addressables with scope 'profiles' and use an exact profile name.";
                return plan;
            }

            BuildTarget activeTarget = _activeBuildTargetProvider();
            if (!Enum.TryParse(request.BuildTarget, true, out BuildTarget requestedTarget)
                || requestedTarget != activeTarget)
            {
                plan.Error = $"Requested build target '{request.BuildTarget}' does not match " +
                    $"Unity's active target '{activeTarget}'.";
                plan.Suggestion = "Switch targets through Unity's Build Settings, wait for imports to finish, then retry.";
                return plan;
            }
            if (settings.ActivePlayerDataBuilder == null
                || !settings.ActivePlayerDataBuilder
                    .CanBuildData<AddressablesPlayerBuildResult>())
            {
                plan.Error = "The active Addressables data builder cannot build player content.";
                plan.Suggestion = "Select a packed player data builder in Addressables settings.";
                return plan;
            }

            var pathFindings = new List<AddressablesValidationFinding>();
            ValidateGroupPaths(settings,
                (settings.groups ?? new List<AddressableAssetGroup>())
                    .Where(group => group != null),
                pathFindings, profileId);
            var pathError = pathFindings.FirstOrDefault(finding =>
                finding.Severity == AddressablesValidationSeverity.Error);
            if (pathError != null)
            {
                plan.Error = $"Addressables profile '{request.Profile}' is not build-ready. " +
                    pathError.Message;
                plan.Suggestion = pathError.Suggestion;
                return plan;
            }

            string contentStatePath = request.ContentStatePath;
            if (request.BuildKind == AddressablesBuildKind.ContentUpdate)
            {
                contentStatePath = ResolveFilePath(contentStatePath);
                if (!File.Exists(contentStatePath))
                {
                    plan.Error = $"Content-state file does not exist at '{contentStatePath}'.";
                    return plan;
                }
                if (!string.Equals(Path.GetExtension(contentStatePath), ".bin",
                    StringComparison.OrdinalIgnoreCase))
                {
                    plan.Error = "Content-state input must be a .bin file produced by a previous full Addressables build.";
                    return plan;
                }
            }

            plan.BackendState = new BuildState
            {
                Settings = settings,
                ProfileId = profileId,
                OriginalProfileId = settings.activeProfileId,
                ContentStatePath = contentStatePath,
                ActiveBuildTarget = activeTarget
            };
            return plan;
        }

        public AddressablesBuildResult ExecuteBuild(AddressablesBuildPlan plan)
        {
            if (plan?.Request == null || !(plan.BackendState is BuildState state))
                return BuildFailure(plan, "Addressables build plan is missing or invalid.");
            if (!string.IsNullOrEmpty(plan.Error))
                return BuildFailure(plan, plan.Error, plan.Suggestion);
            if (EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode
                || BuildPipeline.isBuildingPlayer)
            {
                return BuildFailure(plan,
                    "Unity became busy before the Addressables content build started.");
            }
            if (_activeBuildTargetProvider() != state.ActiveBuildTarget)
            {
                return BuildFailure(plan,
                    "Unity's active build target changed after validation. Run the build again.");
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            AddressablesPlayerBuildResult apiResult = null;
            string error = null;
            string restorationError = null;
            bool profileRestored = false;
            try
            {
                state.Settings.activeProfileId = state.ProfileId;
                apiResult = plan.Request.BuildKind == AddressablesBuildKind.ContentUpdate
                    ? _contentUpdateBuild(state.Settings, state.ContentStatePath)
                    : _fullBuild(state.Settings);
                if (apiResult == null)
                    error = "The Addressables build API returned no result. The content-state file or build configuration may be incompatible.";
                else if (!string.IsNullOrEmpty(apiResult.Error))
                    error = apiResult.Error;
            }
            catch (Exception exception)
            {
                error = $"Addressables build API failed: {exception.Message}";
            }
            finally
            {
                try
                {
                    state.Settings.activeProfileId = state.OriginalProfileId;
                    profileRestored = string.Equals(
                        state.Settings.activeProfileId, state.OriginalProfileId,
                        StringComparison.Ordinal);
                }
                catch (Exception exception)
                {
                    restorationError = exception.Message;
                }
                stopwatch.Stop();
            }

            var outputPaths = apiResult?.FileRegistry?.GetFilePaths()
                ?.Where(path => !string.IsNullOrEmpty(path))
                .Concat(string.IsNullOrEmpty(apiResult?.OutputPath)
                    ? Array.Empty<string>()
                    : new[] { apiResult.OutputPath })
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray() ?? Array.Empty<string>();
            return new AddressablesBuildResult
            {
                Success = string.IsNullOrEmpty(error) && profileRestored,
                Error = !profileRestored
                    ? "The Addressables profile could not be restored after the build." +
                        (string.IsNullOrEmpty(restorationError)
                            ? string.Empty
                            : $" {restorationError}")
                    : error,
                Suggestion = !profileRestored
                    ? "Restore the intended active profile in Addressables settings before another build."
                    : null,
                BuildKind = plan.Request.BuildKind,
                Profile = plan.Request.Profile,
                BuildTarget = state.ActiveBuildTarget.ToString(),
                DurationSeconds = apiResult != null && apiResult.Duration > 0
                    ? apiResult.Duration
                    : stopwatch.Elapsed.TotalSeconds,
                LocationCount = apiResult?.LocationCount ?? 0,
                ProfileRestored = profileRestored,
                OutputPaths = outputPaths
            };
        }

        public AddressablesMutationPlan PrepareMutation(AddressablesMutationRequest request)
        {
            var capability = GetCapability();
            var plan = new AddressablesMutationPlan
            {
                Capability = capability,
                Request = request
            };
            if (request == null)
            {
                plan.Error = "Addressables mutation request is missing.";
                return plan;
            }
            if (request.Action == AddressablesMutationAction.InitializeSettings)
                return PrepareInitializeSettings(capability, plan);
            if (capability.State != AddressablesState.Ready)
            {
                plan.Error = capability.Message ?? "Addressables is unavailable.";
                return plan;
            }

            var settings = _settingsProvider();
            if (settings == null)
            {
                plan.Error = "Addressables settings became unavailable during validation.";
                return plan;
            }

            if (request.Action == AddressablesMutationAction.CreateGroup)
                return PrepareCreateGroup(settings, request, plan);
            if (request.Action == AddressablesMutationAction.SetDefaultGroup)
                return PrepareSetDefaultGroup(settings, request, plan);

            var requestedGroup = FindExactGroup(settings, request.Group);
            if (requestedGroup == null)
            {
                plan.Error = $"Addressables group '{request.Group}' does not exist.";
                plan.Suggestion = "Run inspect_addressables with scope 'groups' and use an exact group name.";
                return plan;
            }
            if (requestedGroup.ReadOnly)
            {
                plan.Error = $"Addressables group '{request.Group}' is read-only.";
                return plan;
            }

            var entry = settings.FindAssetEntry(request.Guid, true);
            plan.BeforeEntry = entry == null ? null : BuildEntry(entry.parentGroup, entry);
            if (entry != null && entry.IsSubAsset)
            {
                string parentPath = entry.ParentEntry?.AssetPath;
                plan.Error = $"Asset '{request.AssetPath}' is Addressable through parent entry '{parentPath ?? "unknown"}'.";
                plan.Suggestion = "Manage the explicit parent folder entry manually; Xorin does not mutate inherited Addressables entries.";
                return plan;
            }
            if (entry != null && (entry.ReadOnly || entry.parentGroup?.ReadOnly == true))
            {
                plan.Error = $"Addressables entry '{request.AssetPath}' is read-only.";
                plan.Suggestion = "Use a writable entry and group; Xorin does not override Addressables read-only protection.";
                return plan;
            }

            switch (request.Action)
            {
                case AddressablesMutationAction.MakeAddressable:
                    return PrepareMakeAddressable(settings, requestedGroup, entry, request, plan);
                case AddressablesMutationAction.RemoveEntry:
                    return PrepareRemoveEntry(settings, requestedGroup, entry, request, plan);
                case AddressablesMutationAction.MoveEntry:
                    return PrepareMoveEntry(settings, requestedGroup, entry, request, plan);
                case AddressablesMutationAction.AddLabel:
                case AddressablesMutationAction.RemoveLabel:
                    return PrepareLabel(settings, requestedGroup, entry, request, plan);
                default:
                    plan.Error = $"Unsupported Addressables action '{request.Action}'.";
                    return plan;
            }
        }

        public AddressablesMutationResult ExecuteMutation(AddressablesMutationPlan plan)
        {
            if (plan == null || plan.Request == null)
            {
                return MutationFailure("Addressables mutation plan is missing.");
            }
            if (!string.IsNullOrEmpty(plan.Error))
                return MutationFailure(plan.Error, plan.Suggestion);
            if (plan.NoOp)
                return new AddressablesMutationResult { Success = true };
            if (plan.Request.Action == AddressablesMutationAction.InitializeSettings)
                return ExecuteInitializeSettings(plan);

            var settings = _settingsProvider();
            if (settings == null)
                return MutationFailure("Addressables settings became unavailable before execution.");

            var capability = GetCapability();
            if (capability.State != AddressablesState.Ready)
                return MutationFailure(capability.Message ?? "Addressables is unavailable.");

            var request = plan.Request;
            var beforePaths = new HashSet<string>(CollectSettingsAssetInventory(settings),
                StringComparer.Ordinal);
            string undoLabel = UndoLabelPrefix + request.Action;
            int undoGroup = BeginUndoGroup(undoLabel);
            bool saveAttempted = false;
            try
            {
                RegisterUndo(settings, request, undoLabel);

                switch (request.Action)
                {
                    case AddressablesMutationAction.MakeAddressable:
                    {
                        var group = FindExactGroup(settings, request.Group);
                        var entry = settings.CreateOrMoveEntry(request.Guid, group, false, true);
                        entry.SetAddress(request.Address, true);
                        EditorUtility.SetDirty(group);
                        break;
                    }
                    case AddressablesMutationAction.RemoveEntry:
                        settings.RemoveAssetEntry(request.Guid, true);
                        break;
                    case AddressablesMutationAction.MoveEntry:
                    {
                        var group = FindExactGroup(settings, request.Group);
                        settings.CreateOrMoveEntry(request.Guid, group, false, true);
                        EditorUtility.SetDirty(group);
                        break;
                    }
                    case AddressablesMutationAction.AddLabel:
                    {
                        settings.AddLabel(request.Label, true);
                        settings.FindAssetEntry(request.Guid).SetLabel(request.Label, true, false, true);
                        break;
                    }
                    case AddressablesMutationAction.RemoveLabel:
                        settings.FindAssetEntry(request.Guid).SetLabel(request.Label, false, false, true);
                        break;
                    case AddressablesMutationAction.CreateGroup:
                    {
                        // CreateGroup creates project assets and its schema-copy path may
                        // save before the call returns. Treat any failure from this point
                        // as potentially persisted and leave recovery to the checkpoint.
                        saveAttempted = true;
                        AddressableAssetGroup created;
                        if (request.SchemaPreset == AddressablesSchemaPreset.PackedLocal)
                        {
                            created = settings.CreateGroup(
                                request.Group, false, false, true, null,
                                typeof(BundledAssetGroupSchema),
                                typeof(ContentUpdateGroupSchema));
                            ConfigurePackedLocal(settings, created);
                        }
                        else
                        {
                            var template = FindExactGroup(settings, request.TemplateGroup);
                            var schemasToCopy = (template.Schemas
                                ?? new List<AddressableAssetGroupSchema>())
                                .Where(schema => schema != null)
                                .ToList();
                            created = settings.CreateGroup(
                                request.Group, false, false, true, schemasToCopy);
                        }
                        Undo.RegisterCreatedObjectUndo(created, undoLabel);
                        var createdSchemas = (created.Schemas
                            ?? new List<AddressableAssetGroupSchema>()).ToList();
                        foreach (var schema in createdSchemas)
                        {
                            if (schema != null)
                                Undo.RegisterCreatedObjectUndo(schema, undoLabel);
                        }
                        EditorUtility.SetDirty(created);
                        break;
                    }
                    case AddressablesMutationAction.SetDefaultGroup:
                        settings.DefaultGroup = FindExactGroup(settings, request.Group);
                        break;
                }

                EditorUtility.SetDirty(settings);
                saveAttempted = true;
                _saveAssets();

                var afterPaths = CollectSettingsAssetInventory(settings);
                var newPaths = afterPaths
                    .Where(path => !beforePaths.Contains(path))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();
                Undo.CollapseUndoOperations(undoGroup);
                return new AddressablesMutationResult
                {
                    Success = true,
                    SaveAttempted = true,
                    NewAssetPaths = newPaths
                };
            }
            catch (Exception exception)
            {
                if (!saveAttempted)
                    Undo.RevertAllDownToGroup(undoGroup);
                var newPaths = CollectSettingsAssetInventory(settings)
                    .Where(path => !beforePaths.Contains(path))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();
                return new AddressablesMutationResult
                {
                    Success = false,
                    SaveAttempted = saveAttempted,
                    Error = $"Addressables API failed: {exception.Message}",
                    NewAssetPaths = newPaths,
                    Suggestion = saveAttempted
                        ? "Use Undo changes to restore the checkpoint, then inspect Addressables before retrying."
                        : "Unity Undo was applied. Inspect Addressables and use Undo changes if any partial assets remain."
                };
            }
        }

        private static void ValidateEntries(
            IEnumerable<GroupedEntry> entries,
            List<AddressablesValidationFinding> findings)
        {
            foreach (var item in entries)
            {
                var entry = item.Entry;
                if (entry.parentGroup == null || entry.parentGroup != item.Group)
                {
                    AddFinding(findings, "entry_group_mismatch",
                        AddressablesValidationSeverity.Error,
                        $"Addressables entry '{entry.address}' does not reference its containing group.",
                        entry.AssetPath, entry.address, item.Group?.Name,
                        "Restore or move the entry through the Addressables Groups window.");
                }
                if (string.IsNullOrWhiteSpace(entry.address))
                {
                    AddFinding(findings, "empty_address",
                        AddressablesValidationSeverity.Error,
                        "An Addressables entry has an empty runtime address.",
                        entry.AssetPath, group: item.Group?.Name,
                        suggestion: "Assign a unique non-empty address before building content.");
                }
                if (string.IsNullOrEmpty(entry.guid))
                {
                    AddFinding(findings, "missing_entry_guid",
                        AddressablesValidationSeverity.Error,
                        $"Addressables entry '{entry.address}' has no asset GUID.",
                        entry.AssetPath, entry.address, item.Group?.Name,
                        "Remove or recreate the broken entry through Addressables APIs.");
                    continue;
                }

                string canonicalPath = AssetDatabase.GUIDToAssetPath(entry.guid);
                if (string.IsNullOrEmpty(canonicalPath)
                    || AssetDatabase.LoadMainAssetAtPath(canonicalPath) == null)
                {
                    AddFinding(findings, "missing_entry_asset",
                        AddressablesValidationSeverity.Error,
                        $"Addressables entry '{entry.address}' points to a missing asset.",
                        entry.AssetPath, entry.address, item.Group?.Name,
                        "Restore the asset or remove the stale Addressables entry.");
                }
            }
        }

        private static void ValidateDuplicateAddresses(
            IEnumerable<GroupedEntry> entries,
            List<AddressablesValidationFinding> findings)
        {
            foreach (var duplicate in entries
                .Where(item => !string.IsNullOrWhiteSpace(item.Entry.address))
                .GroupBy(item => item.Entry.address, StringComparer.Ordinal)
                .Where(group => group.Count() > 1))
            {
                foreach (var item in duplicate)
                {
                    AddFinding(findings, "duplicate_address",
                        AddressablesValidationSeverity.Error,
                        $"Runtime address '{duplicate.Key}' is used by multiple entries.",
                        item.Entry.AssetPath, duplicate.Key, item.Group?.Name,
                        "Keep one stable key and assign unique addresses to the conflicting entries.");
                }
            }
        }

        private static void ValidateSceneOverlap(
            IEnumerable<GroupedEntry> entries,
            List<AddressablesValidationFinding> findings)
        {
            var enabledBuildScenes = new HashSet<string>(
                (EditorBuildSettings.scenes ?? Array.Empty<EditorBuildSettingsScene>())
                    .Where(scene => scene.enabled)
                    .Select(scene => scene.path),
                StringComparer.Ordinal);
            var groups = entries.Select(item => item.Group)
                .Where(group => group != null).Distinct();
            foreach (var item in GatherAddressableScenes(groups))
            {
                if (!enabledBuildScenes.Contains(item.Entry.AssetPath)) continue;
                AddFinding(findings, "scene_in_build_settings_and_addressables",
                    AddressablesValidationSeverity.Warning,
                    $"Scene '{item.Entry.AssetPath}' is both Addressable and enabled in Build Settings.",
                    item.Entry.AssetPath, item.Entry.address, item.Group?.Name,
                    "Confirm both loading paths are intentional; otherwise remove the duplicate inclusion manually.");
            }
        }

        private static void ValidateGroupPaths(
            AddressableAssetSettings settings,
            IEnumerable<AddressableAssetGroup> groups,
            List<AddressablesValidationFinding> findings,
            string profileId)
        {
            string profileName = settings.profileSettings?.GetProfileName(profileId);
            if (string.IsNullOrEmpty(profileName))
            {
                AddFinding(findings, "missing_active_profile",
                    AddressablesValidationSeverity.Error,
                    "Addressables has no valid selected profile.",
                    suggestion: "Select an existing Addressables profile before validation or building.");
                return;
            }

            foreach (var group in groups)
            {
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema == null) continue;
                string buildName = schema.BuildPath.GetName(settings);
                string loadName = schema.LoadPath.GetName(settings);
                string buildPath = schema.BuildPath.GetValue(
                    settings.profileSettings, profileId);
                string loadPath = schema.LoadPath.GetValue(
                    settings.profileSettings, profileId);

                if (IsUnresolvedProfilePath(settings, profileId,
                    schema.BuildPath, buildName, buildPath))
                {
                    AddFinding(findings, "unresolved_group_build_path",
                        AddressablesValidationSeverity.Error,
                        $"Group '{group.Name}' has an empty or unresolved build path.",
                        group: group.Name,
                        suggestion: $"Assign a valid build-path profile variable for profile '{profileName}'.");
                }
                if (IsUnresolvedProfilePath(settings, profileId,
                    schema.LoadPath, loadName, loadPath))
                {
                    AddFinding(findings, "unresolved_group_load_path",
                        AddressablesValidationSeverity.Error,
                        $"Group '{group.Name}' has an empty or unresolved load path.",
                        group: group.Name,
                        suggestion: $"Assign a valid load-path profile variable for profile '{profileName}'.");
                }
                if (!string.IsNullOrEmpty(buildName) && !string.IsNullOrEmpty(loadName)
                    && IsRemoteVariable(buildName) != IsRemoteVariable(loadName))
                {
                    AddFinding(findings, "mismatched_group_path_pair",
                        AddressablesValidationSeverity.Warning,
                        $"Group '{group.Name}' mixes local and remote build/load path variables.",
                        group: group.Name,
                        suggestion: "Use a matching local/local or remote/remote profile-variable pair.");
                }
            }

            if (settings.BuildRemoteCatalog)
            {
                ValidatePathPair(settings, "remote catalog",
                    settings.RemoteCatalogBuildPath, settings.RemoteCatalogLoadPath,
                    findings, profileId);
            }
        }

        private static void ValidatePathPair(
            AddressableAssetSettings settings,
            string owner,
            ProfileValueReference buildReference,
            ProfileValueReference loadReference,
            List<AddressablesValidationFinding> findings,
            string profileId)
        {
            string buildName = buildReference.GetName(settings);
            string loadName = loadReference.GetName(settings);
            string buildPath = buildReference.GetValue(
                settings.profileSettings, profileId);
            string loadPath = loadReference.GetValue(
                settings.profileSettings, profileId);
            if (IsUnresolvedProfilePath(settings, profileId,
                    buildReference, buildName, buildPath)
                || IsUnresolvedProfilePath(settings, profileId,
                    loadReference, loadName, loadPath))
            {
                AddFinding(findings, "unresolved_remote_catalog_path",
                    AddressablesValidationSeverity.Error,
                    $"The {owner} has an empty or unresolved build/load path.",
                    suggestion: "Assign valid matching build and load profile variables before building.");
                return;
            }
            if (IsRemoteVariable(buildName) != IsRemoteVariable(loadName))
            {
                AddFinding(findings, "mismatched_remote_catalog_path_pair",
                    AddressablesValidationSeverity.Warning,
                    $"The {owner} mixes local and remote build/load path variables.",
                    suggestion: "Use a matching local/local or remote/remote profile-variable pair.");
            }
        }

        private static void ValidateCodeKeys(
            IReadOnlyList<string> codeKeys,
            IEnumerable<GroupedEntry> entries,
            List<AddressablesValidationFinding> findings)
        {
            if (codeKeys == null || codeKeys.Count == 0) return;
            var knownAddresses = new HashSet<string>(
                entries.Select(item => item.Entry.address)
                    .Where(address => !string.IsNullOrEmpty(address)),
                StringComparer.Ordinal);
            foreach (string key in codeKeys)
            {
                if (knownAddresses.Contains(key)) continue;
                AddFinding(findings, "missing_exact_code_key",
                    AddressablesValidationSeverity.Error,
                    $"Exact Addressables key '{key}' was not found in current settings.",
                    key: key,
                    suggestion: "Inspect the call site and replace it only with an exact existing address.");
            }
        }

        private static void ValidateUnresolvedKeyEvidence(
            IReadOnlyList<string> unresolvedEvidence,
            List<AddressablesValidationFinding> findings)
        {
            foreach (string evidence in unresolvedEvidence ?? Array.Empty<string>())
            {
                AddFinding(findings, "dynamic_key_requires_review",
                    AddressablesValidationSeverity.Info,
                    $"Addressables key usage could not be resolved statically: {evidence}",
                    suggestion: "Review the runtime key construction manually; this is not proof of a broken key.");
            }
        }

        private static int ValidateAssetReferences(
            AddressableAssetSettings settings,
            IReadOnlyList<string> requestedPaths,
            List<AddressablesValidationFinding> findings)
        {
            int checkedCount = 0;
            var scannedObjects = new HashSet<int>();
            var openScenePaths = new HashSet<string>(StringComparer.Ordinal);
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.prefabContentsRoot != null)
            {
                foreach (var component in prefabStage.prefabContentsRoot
                    .GetComponentsInChildren<MonoBehaviour>(true))
                {
                    checkedCount += ValidateReferenceFields(
                        settings, component, prefabStage.assetPath, scannedObjects, findings);
                }
            }
            else
            {
                for (int index = 0; index < SceneManager.sceneCount; index++)
                {
                    var scene = SceneManager.GetSceneAt(index);
                    if (!scene.isLoaded) continue;
                    if (!string.IsNullOrEmpty(scene.path)) openScenePaths.Add(scene.path);
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            checkedCount += ValidateReferenceFields(
                                settings, component, scene.path, scannedObjects, findings);
                        }
                    }
                }
            }

            foreach (string path in requestedPaths ?? Array.Empty<string>())
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null)
                {
                    AddFinding(findings, "validation_asset_missing",
                        AddressablesValidationSeverity.Error,
                        $"Requested validation asset '{path}' does not exist.",
                        assetPath: path,
                        suggestion: "Provide an exact existing Assets/ path.");
                    continue;
                }
                if (asset is GameObject prefab)
                {
                    foreach (var component in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                        checkedCount += ValidateReferenceFields(
                            settings, component, path, scannedObjects, findings);
                }
                else if (asset is ScriptableObject scriptableObject)
                {
                    checkedCount += ValidateReferenceFields(
                        settings, scriptableObject, path, scannedObjects, findings);
                }
                else if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    if (openScenePaths.Contains(path)) continue;
                    AddFinding(findings, "scene_reference_scan_requires_open_scene",
                        AddressablesValidationSeverity.Info,
                        $"Scene '{path}' was not scanned because it is not currently open.",
                        assetPath: path,
                        suggestion: "Open the scene and run validation again to inspect its AssetReference fields.");
                }
            }
            return checkedCount;
        }

        private static int ValidateReferenceFields(
            AddressableAssetSettings settings,
            UnityEngine.Object owner,
            string ownerPath,
            HashSet<int> scannedObjects,
            List<AddressablesValidationFinding> findings)
        {
            if (owner == null || !scannedObjects.Add(owner.GetInstanceID())) return 0;
            int checkedCount = 0;
            for (Type type = owner.GetType(); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic
                        || !typeof(AssetReference).IsAssignableFrom(field.FieldType)
                        || (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null))
                    {
                        continue;
                    }
                    checkedCount++;
                    var reference = field.GetValue(owner) as AssetReference;
                    if (reference == null)
                    {
                        AddFinding(findings, "null_asset_reference_field",
                            AddressablesValidationSeverity.Warning,
                            $"AssetReference field '{owner.GetType().Name}.{field.Name}' is not initialized.",
                            ownerPath,
                            suggestion: "Initialize the field before assigning an Addressable asset.");
                        continue;
                    }
                    if (string.IsNullOrEmpty(reference.AssetGUID)) continue;

                    string assetPath = AssetDatabase.GUIDToAssetPath(reference.AssetGUID);
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        AddFinding(findings, "unresolved_asset_reference_guid",
                            AddressablesValidationSeverity.Error,
                            $"AssetReference field '{owner.GetType().Name}.{field.Name}' points to a missing GUID.",
                            ownerPath,
                            suggestion: "Clear the field or assign an existing Addressable asset.");
                        continue;
                    }
                    if (settings.FindAssetEntry(reference.AssetGUID, true) == null)
                    {
                        AddFinding(findings, "asset_reference_not_addressable",
                            AddressablesValidationSeverity.Error,
                            $"AssetReference field '{owner.GetType().Name}.{field.Name}' points to an asset that is not Addressable.",
                            assetPath,
                            suggestion: "Register the asset with manage_addressables or clear the reference.");
                    }
                    var editorAsset = reference.editorAsset;
                    if (editorAsset == null || !reference.ValidateAsset(editorAsset))
                    {
                        AddFinding(findings, "unresolved_asset_reference",
                            AddressablesValidationSeverity.Error,
                            $"AssetReference field '{owner.GetType().Name}.{field.Name}' cannot resolve a compatible asset.",
                            assetPath,
                            suggestion: "Assign an asset compatible with the field's AssetReference type.");
                    }
                    if (!string.IsNullOrEmpty(reference.SubObjectName)
                        && !AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath)
                            .Any(asset => asset != null && string.Equals(
                                asset.name, reference.SubObjectName, StringComparison.Ordinal)))
                    {
                        AddFinding(findings, "missing_asset_reference_sub_object",
                            AddressablesValidationSeverity.Error,
                            $"AssetReference field '{owner.GetType().Name}.{field.Name}' points to missing sub-object '{reference.SubObjectName}'.",
                            assetPath,
                            suggestion: "Assign an exact existing sub-object or clear the sub-object selection.");
                    }
                }
            }
            return checkedCount;
        }

        private static bool IsUnresolvedProfilePath(
            AddressableAssetSettings settings,
            string profileId,
            ProfileValueReference reference,
            string variableName,
            string value)
        {
            return string.IsNullOrWhiteSpace(variableName)
                || string.IsNullOrWhiteSpace(value)
                || value.IndexOf("[", StringComparison.Ordinal) >= 0
                || HasUnresolvedProfileToken(settings, profileId, reference);
        }

        private static bool HasUnresolvedProfileToken(
            AddressableAssetSettings settings,
            string profileId,
            ProfileValueReference reference)
        {
            string rawValue = settings.profileSettings?
                .GetValueById(profileId, reference?.Id);
            if (string.IsNullOrEmpty(rawValue)) return true;

            int searchFrom = 0;
            while (searchFrom < rawValue.Length)
            {
                int start = rawValue.IndexOf('[', searchFrom);
                if (start < 0) return false;
                int end = rawValue.IndexOf(']', start + 1);
                if (end < 0) return true;

                string token = rawValue.Substring(start + 1, end - start - 1);
                string resolved = settings.profileSettings.EvaluateString(
                    profileId, "[" + token + "]");
                if (string.IsNullOrWhiteSpace(token)
                    || string.IsNullOrWhiteSpace(resolved)
                    || string.Equals(token, resolved, StringComparison.Ordinal)
                    || resolved.IndexOf("#ERROR-", StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
                searchFrom = end + 1;
            }
            return false;
        }

        private static bool IsRemoteVariable(string variableName)
        {
            return variableName.IndexOf("Remote", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void AddFinding(
            List<AddressablesValidationFinding> findings,
            string code,
            string severity,
            string message,
            string assetPath = null,
            string key = null,
            string group = null,
            string suggestion = null)
        {
            findings.Add(new AddressablesValidationFinding
            {
                Code = code,
                Severity = severity,
                Message = message,
                AssetPath = assetPath,
                Key = key,
                Group = group,
                Suggestion = suggestion
            });
        }

        private static string ResolveFilePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? Directory.GetCurrentDirectory();
            return Path.GetFullPath(Path.Combine(projectRoot, path));
        }

        private static AddressablesPlayerBuildResult BuildFullContent(
            AddressableAssetSettings settings)
        {
            AddressableAssetSettings.BuildPlayerContent(out var result);
            return result;
        }

        private static AddressablesBuildResult BuildFailure(
            AddressablesBuildPlan plan, string error, string suggestion = null)
        {
            return new AddressablesBuildResult
            {
                Success = false,
                Error = error,
                Suggestion = suggestion,
                BuildKind = plan?.Request?.BuildKind,
                Profile = plan?.Request?.Profile,
                BuildTarget = plan?.Request?.BuildTarget
            };
        }

        private AddressablesMutationPlan PrepareInitializeSettings(
            AddressablesCapability capability,
            AddressablesMutationPlan plan)
        {
            if (!string.IsNullOrWhiteSpace(plan.Request.Group)
                || !string.IsNullOrWhiteSpace(plan.Request.TemplateGroup)
                || !string.IsNullOrWhiteSpace(plan.Request.SchemaPreset))
            {
                plan.Error = "initialize_settings does not accept group or schema-source parameters.";
                return plan;
            }
            var settings = _settingsProvider();
            if (capability.State == AddressablesState.Ready && settings != null)
            {
                plan.BeforeSettings = BuildSettingsInfo(settings);
                plan.ExpectedSettings = BuildSettingsInfo(settings);
                plan.NoOp = true;
                return plan;
            }
            if (capability.State != AddressablesState.SettingsNotCreated)
            {
                plan.Error = capability.Message ?? "Addressables initialization is unavailable.";
                return plan;
            }

            var inventory = CollectCanonicalAssetInventory();
            var conflicts = inventory.Where(path => !string.Equals(
                path, CanonicalSettingsFolder, StringComparison.Ordinal)).ToList();
            if (conflicts.Count > 0)
            {
                plan.Error = "The canonical Addressables location contains unregistered configuration assets.";
                plan.Suggestion = "Move or resolve the conflicting assets manually, then inspect Addressables before retrying.";
                plan.ConflictPaths = conflicts;
                return plan;
            }

            plan.BeforeSettings = new AddressablesSettingsInfo { SettingsExist = false };
            plan.ExpectedSettings = new AddressablesSettingsInfo
            {
                SettingsExist = true,
                SettingsAssetPath = CanonicalSettingsPath,
                ActiveProfile = "Default",
                DefaultGroup = "Default Local Group",
                GroupCount = 1
            };
            plan.CanonicalAssetInventoryBefore = inventory;
            plan.AffectedAssetPaths = new[] { EditorBuildSettingsPath };
            return plan;
        }

        private AddressablesMutationResult ExecuteInitializeSettings(
            AddressablesMutationPlan plan)
        {
            var beforePaths = new HashSet<string>(
                plan.CanonicalAssetInventoryBefore ?? Array.Empty<string>(),
                StringComparer.Ordinal);
            var currentPaths = CollectCanonicalAssetInventory();
            if (!beforePaths.SetEquals(currentPaths))
            {
                return new AddressablesMutationResult
                {
                    Success = false,
                    Error = "The canonical Addressables asset inventory changed after validation.",
                    Suggestion = "Inspect the canonical Addressables folder and retry from a fresh checkpoint."
                };
            }

            bool saveAttempted = false;
            try
            {
                // Create may persist settings, profiles, groups, schemas, and templates
                // before returning, so every exception from this point is recoverable
                // through the pre-recorded checkpoint.
                saveAttempted = true;
                var created = _settingsCreator();
                if (created == null)
                    throw new InvalidOperationException("Addressables settings creation returned null.");
                _settingsRegistrar(created);
                EditorUtility.SetDirty(created);
                _saveAssets();

                var resolved = _settingsProvider();
                if (resolved == null)
                    throw new InvalidOperationException(
                        "The newly created Addressables settings were not registered as the default object.");
                var newPaths = CollectCanonicalAssetInventory()
                    .Where(path => !beforePaths.Contains(path))
                    .OrderBy(path => path.Length)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToList();
                return new AddressablesMutationResult
                {
                    Success = true,
                    SaveAttempted = true,
                    NewAssetPaths = newPaths
                };
            }
            catch (Exception exception)
            {
                var newPaths = CollectCanonicalAssetInventory()
                    .Where(path => !beforePaths.Contains(path))
                    .OrderBy(path => path.Length)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToList();
                return new AddressablesMutationResult
                {
                    Success = false,
                    SaveAttempted = saveAttempted,
                    Error = $"Addressables initialization failed: {exception.Message}",
                    Suggestion = saveAttempted
                        ? "Use Undo changes to restore the checkpoint, then inspect Addressables before retrying."
                        : "Inspect Addressables before retrying.",
                    NewAssetPaths = newPaths
                };
            }
        }

        private static AddressablesMutationPlan PrepareCreateGroup(
            AddressableAssetSettings settings,
            AddressablesMutationRequest request,
            AddressablesMutationPlan plan)
        {
            if (string.IsNullOrWhiteSpace(request.Group))
            {
                plan.Error = "create_group requires an exact non-empty group name.";
                return plan;
            }
            bool hasTemplate = !string.IsNullOrWhiteSpace(request.TemplateGroup);
            bool hasPreset = !string.IsNullOrWhiteSpace(request.SchemaPreset);
            if (hasTemplate == hasPreset)
            {
                plan.Error = "create_group requires exactly one schema source: template_group or schema_preset.";
                plan.Suggestion = "Use an exact existing template group, or schema_preset 'packed_local'.";
                return plan;
            }
            if (hasPreset && request.SchemaPreset != AddressablesSchemaPreset.PackedLocal)
            {
                plan.Error = $"Unsupported Addressables schema preset '{request.SchemaPreset}'.";
                plan.Suggestion = "Use schema_preset 'packed_local'.";
                return plan;
            }

            AddressableAssetGroup template = null;
            if (hasTemplate)
            {
                var templateMatches = FindExactGroups(settings, request.TemplateGroup);
                if (templateMatches.Count > 1)
                {
                    plan.Error = $"Template Addressables group '{request.TemplateGroup}' is ambiguous.";
                    plan.Suggestion = "Remove duplicate group names manually before copying schemas.";
                    return plan;
                }
                template = templateMatches.SingleOrDefault();
                if (template == null)
                {
                    plan.Error = $"Template Addressables group '{request.TemplateGroup}' does not exist.";
                    plan.Suggestion = "Run inspect_addressables with scope 'groups' and choose an exact template group.";
                    return plan;
                }
            }
            var existingMatches = FindExactGroups(settings, request.Group);
            if (existingMatches.Count > 1)
            {
                plan.Error = $"Addressables group '{request.Group}' is ambiguous.";
                plan.Suggestion = "Remove duplicate exact group names manually before retrying.";
                return plan;
            }
            var existing = existingMatches.SingleOrDefault();
            var nameConflict = (settings.groups ?? new List<AddressableAssetGroup>())
                .FirstOrDefault(group => group != null
                    && string.Equals(group.Name, request.Group,
                        StringComparison.OrdinalIgnoreCase));
            if (existing == null && nameConflict != null)
            {
                plan.Error = $"Addressables group name '{request.Group}' conflicts with existing group '{nameConflict.Name}'.";
                plan.Suggestion = "Use the existing group's exact capitalization or choose a distinct name.";
                return plan;
            }
            plan.BeforeGroup = existing == null ? null : BuildGroup(settings, existing);
            var desiredGroup = request.SchemaPreset == AddressablesSchemaPreset.PackedLocal
                ? PackedLocalGroupInfo(request.Group)
                : CloneGroup(BuildGroup(settings, template), request.Group, null, false, 0);
            plan.ExpectedGroup = existing == null
                ? desiredGroup
                : CloneGroup(desiredGroup, request.Group, existing.Guid,
                    existing == settings.DefaultGroup, existing.entries?.Count ?? 0);
            var affectedPaths = new HashSet<string>(
                CollectConfigurationPaths(settings, template, existing),
                StringComparer.Ordinal);
            if (existing == null && settings.IsPersisted)
            {
                affectedPaths.Add($"{settings.GroupFolder}/{request.Group}.asset");
                IEnumerable<string> schemaTypes = request.SchemaPreset
                    == AddressablesSchemaPreset.PackedLocal
                    ? new[] { nameof(BundledAssetGroupSchema), nameof(ContentUpdateGroupSchema) }
                    : (template.Schemas ?? new List<AddressableAssetGroupSchema>())
                        .Where(schema => schema != null)
                        .Select(schema => schema.GetType().Name);
                foreach (string schemaType in schemaTypes)
                {
                    affectedPaths.Add(
                        $"{settings.GroupSchemaFolder}/{request.Group}_{schemaType}.asset");
                }
            }
            plan.AffectedAssetPaths = affectedPaths
                .OrderBy(path => path, StringComparer.Ordinal).ToList();

            if (existing == null) return plan;
            if (GroupSemanticallyEquals(plan.ExpectedGroup, BuildGroup(settings, existing)))
            {
                plan.NoOp = true;
                return plan;
            }

            plan.Error = $"Addressables group '{request.Group}' already exists with different schema configuration.";
            plan.Suggestion = "Use the existing group or choose a new exact group name.";
            return plan;
        }

        private static AddressablesMutationPlan PrepareSetDefaultGroup(
            AddressableAssetSettings settings,
            AddressablesMutationRequest request,
            AddressablesMutationPlan plan)
        {
            if (string.IsNullOrWhiteSpace(request.Group))
            {
                plan.Error = "set_default_group requires an exact non-empty group name.";
                return plan;
            }
            if (!string.IsNullOrWhiteSpace(request.TemplateGroup)
                || !string.IsNullOrWhiteSpace(request.SchemaPreset))
            {
                plan.Error = "set_default_group does not accept schema-source parameters.";
                return plan;
            }
            var matches = FindExactGroups(settings, request.Group);
            if (matches.Count > 1)
            {
                plan.Error = $"Addressables group '{request.Group}' is ambiguous.";
                plan.Suggestion = "Remove duplicate exact group names manually before assigning a default.";
                return plan;
            }
            var requested = matches.SingleOrDefault();
            if (requested == null)
            {
                plan.Error = $"Addressables group '{request.Group}' does not exist.";
                plan.Suggestion = "Run inspect_addressables with scope 'groups' and use an exact group name.";
                return plan;
            }
            if (requested.ReadOnly)
            {
                plan.Error = $"Addressables group '{request.Group}' is read-only.";
                plan.Suggestion = "Choose an exact existing writable group.";
                return plan;
            }

            plan.BeforeSettings = BuildSettingsInfo(settings);
            plan.PreviousDefaultGroup = settings.DefaultGroup == null
                ? null : BuildGroup(settings, settings.DefaultGroup);
            plan.ExpectedGroup = BuildGroup(settings, requested);
            plan.ExpectedSettings = CloneSettingsInfo(plan.BeforeSettings);
            plan.ExpectedSettings.DefaultGroup = requested.Name;
            plan.ExpectedSettings.DefaultGroupGuid = requested.Guid;
            plan.AffectedAssetPaths = CollectConfigurationPaths(settings);
            if (settings.DefaultGroup == requested)
                plan.NoOp = true;
            return plan;
        }

        private static AddressablesMutationPlan PrepareMakeAddressable(
            AddressableAssetSettings settings,
            AddressableAssetGroup requestedGroup,
            AddressableAssetEntry entry,
            AddressablesMutationRequest request,
            AddressablesMutationPlan plan)
        {
            var duplicate = AllAddressableEntries(settings).FirstOrDefault(candidate =>
                !string.Equals(candidate.guid, request.Guid, StringComparison.Ordinal)
                && string.Equals(candidate.address, request.Address, StringComparison.Ordinal));
            if (duplicate != null)
            {
                plan.Error = $"Address '{request.Address}' is already used by '{duplicate.AssetPath}'.";
                plan.Suggestion = "Choose a unique explicit address.";
                return plan;
            }
            if (entry != null)
            {
                if (entry.parentGroup == requestedGroup
                    && string.Equals(entry.address, request.Address, StringComparison.Ordinal))
                {
                    plan.NoOp = true;
                    plan.ExpectedEntry = CloneEntry(plan.BeforeEntry);
                    plan.AffectedAssetPaths = CollectConfigurationPaths(settings, requestedGroup);
                    return plan;
                }

                plan.Error = $"Asset '{request.AssetPath}' is already Addressable as '{entry.address}' in group '{entry.parentGroup?.Name}'.";
                plan.Suggestion = entry.parentGroup == requestedGroup
                    ? "Changing an existing address is deferred; keep the current key."
                    : "Use move_entry to preserve the existing address while changing groups.";
                return plan;
            }

            plan.ExpectedEntry = new AddressablesEntryInfo
            {
                Guid = request.Guid,
                AssetPath = request.AssetPath,
                Address = request.Address,
                Group = requestedGroup.Name,
                AssetKind = ResolveAssetKind(request.AssetPath),
                IsScene = request.AssetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase),
                Labels = Array.Empty<string>()
            };
            plan.AffectedAssetPaths = CollectConfigurationPaths(settings, requestedGroup);
            return plan;
        }

        private static AddressablesMutationPlan PrepareRemoveEntry(
            AddressableAssetSettings settings,
            AddressableAssetGroup requestedGroup,
            AddressableAssetEntry entry,
            AddressablesMutationRequest request,
            AddressablesMutationPlan plan)
        {
            if (entry == null)
            {
                plan.NoOp = true;
                plan.AffectedAssetPaths = CollectConfigurationPaths(settings, requestedGroup);
                return plan;
            }
            if (entry.parentGroup != requestedGroup)
                return WrongCurrentGroup(plan, request, entry);

            plan.ExpectedEntry = null;
            plan.AffectedAssetPaths = CollectConfigurationPaths(settings, requestedGroup);
            return plan;
        }

        private static AddressablesMutationPlan PrepareMoveEntry(
            AddressableAssetSettings settings,
            AddressableAssetGroup requestedGroup,
            AddressableAssetEntry entry,
            AddressablesMutationRequest request,
            AddressablesMutationPlan plan)
        {
            if (entry == null)
            {
                plan.Error = $"Asset '{request.AssetPath}' is not an explicit Addressables entry.";
                plan.Suggestion = "Use make_addressable first.";
                return plan;
            }
            plan.ExpectedEntry = CloneEntry(plan.BeforeEntry);
            plan.ExpectedEntry.Group = requestedGroup.Name;
            plan.NoOp = entry.parentGroup == requestedGroup;
            plan.AffectedAssetPaths = CollectConfigurationPaths(
                settings, entry.parentGroup, requestedGroup);
            return plan;
        }

        private static AddressablesMutationPlan PrepareLabel(
            AddressableAssetSettings settings,
            AddressableAssetGroup requestedGroup,
            AddressableAssetEntry entry,
            AddressablesMutationRequest request,
            AddressablesMutationPlan plan)
        {
            if (entry == null)
            {
                plan.Error = $"Asset '{request.AssetPath}' is not an explicit Addressables entry.";
                plan.Suggestion = "Use make_addressable first.";
                return plan;
            }
            if (entry.parentGroup != requestedGroup)
                return WrongCurrentGroup(plan, request, entry);

            bool add = request.Action == AddressablesMutationAction.AddLabel;
            bool hasLabel = entry.labels != null && entry.labels.Contains(request.Label);
            plan.NoOp = add ? hasLabel : !hasLabel;
            var labels = new HashSet<string>(plan.BeforeEntry.Labels ?? Array.Empty<string>(),
                StringComparer.Ordinal);
            if (add) labels.Add(request.Label); else labels.Remove(request.Label);
            plan.ExpectedEntry = CloneEntry(plan.BeforeEntry);
            plan.ExpectedEntry.Labels = labels.OrderBy(value => value, StringComparer.Ordinal).ToList();
            plan.AffectedAssetPaths = CollectConfigurationPaths(settings, requestedGroup);
            return plan;
        }

        private static AddressablesMutationPlan WrongCurrentGroup(
            AddressablesMutationPlan plan,
            AddressablesMutationRequest request,
            AddressableAssetEntry entry)
        {
            plan.Error = $"Asset '{request.AssetPath}' belongs to group '{entry.parentGroup?.Name}', not '{request.Group}'.";
            plan.Suggestion = "Inspect the entry and provide its exact current group.";
            return plan;
        }

        private static AddressableAssetGroup FindExactGroup(
            AddressableAssetSettings settings, string name)
        {
            return FindExactGroups(settings, name).FirstOrDefault();
        }

        private static List<AddressableAssetGroup> FindExactGroups(
            AddressableAssetSettings settings, string name)
        {
            return (settings.groups ?? new List<AddressableAssetGroup>())
                .Where(group => group != null
                    && string.Equals(group.Name, name, StringComparison.Ordinal))
                .ToList();
        }

        private static void ConfigurePackedLocal(
            AddressableAssetSettings settings, AddressableAssetGroup group)
        {
            var bundled = group.GetSchema<BundledAssetGroupSchema>();
            var contentUpdate = group.GetSchema<ContentUpdateGroupSchema>();
            if (bundled == null || contentUpdate == null)
                throw new InvalidOperationException(
                    "The packed_local preset did not create both required schemas.");
            if (!bundled.BuildPath.SetVariableByName(
                    settings, AddressableAssetSettings.kLocalBuildPath)
                || !bundled.LoadPath.SetVariableByName(
                    settings, AddressableAssetSettings.kLocalLoadPath))
            {
                throw new InvalidOperationException(
                    "The packed_local preset could not resolve Unity's local profile variables.");
            }
            bundled.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            contentUpdate.StaticContent = false;
            EditorUtility.SetDirty(bundled);
            EditorUtility.SetDirty(contentUpdate);
        }

        private static AddressablesGroupInfo PackedLocalGroupInfo(string name)
        {
            return new AddressablesGroupInfo
            {
                Name = name,
                EntryCount = 0,
                Schemas = new[]
                {
                    typeof(BundledAssetGroupSchema).FullName,
                    typeof(ContentUpdateGroupSchema).FullName
                }.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                BuildPathVariable = AddressableAssetSettings.kLocalBuildPath,
                LoadPathVariable = AddressableAssetSettings.kLocalLoadPath,
                BundlePackingMode =
                    BundledAssetGroupSchema.BundlePackingMode.PackTogether.ToString(),
                StaticContent = false
            };
        }

        private static bool GroupSemanticallyEquals(
            AddressablesGroupInfo expected, AddressablesGroupInfo actual)
        {
            if (expected == null || actual == null) return expected == actual;
            return string.Equals(expected.Name, actual.Name, StringComparison.Ordinal)
                && (expected.Schemas ?? Array.Empty<string>()).SequenceEqual(
                    actual.Schemas ?? Array.Empty<string>(), StringComparer.Ordinal)
                && ((expected.SchemaConfigurations?.Count ?? 0) == 0
                    || (expected.SchemaConfigurations ?? Array.Empty<string>()).SequenceEqual(
                        actual.SchemaConfigurations ?? Array.Empty<string>(),
                        StringComparer.Ordinal))
                && string.Equals(expected.BuildPathVariable, actual.BuildPathVariable,
                    StringComparison.Ordinal)
                && string.Equals(expected.LoadPathVariable, actual.LoadPathVariable,
                    StringComparison.Ordinal)
                && string.Equals(expected.BundlePackingMode, actual.BundlePackingMode,
                    StringComparison.Ordinal)
                && expected.StaticContent == actual.StaticContent;
        }

        private static AddressablesGroupInfo CloneGroup(
            AddressablesGroupInfo source, string name, string guid,
            bool isDefault, int entryCount)
        {
            if (source == null) return null;
            return new AddressablesGroupInfo
            {
                Name = name,
                Guid = guid,
                IsDefault = isDefault,
                ReadOnly = false,
                EntryCount = entryCount,
                Schemas = (source.Schemas ?? Array.Empty<string>()).ToList(),
                SchemaConfigurations = (source.SchemaConfigurations
                    ?? Array.Empty<string>()).ToList(),
                BuildPathVariable = source.BuildPathVariable,
                LoadPathVariable = source.LoadPathVariable,
                BundlePackingMode = source.BundlePackingMode,
                StaticContent = source.StaticContent
            };
        }

        private static IEnumerable<AddressableAssetEntry> AllAddressableEntries(
            AddressableAssetSettings settings)
        {
            var entries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(entries, false);
            return entries.Where(entry => entry != null);
        }

        private static IReadOnlyList<string> CollectConfigurationPaths(
            AddressableAssetSettings settings, params AddressableAssetGroup[] groups)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            AddAssetPath(paths, settings);
            foreach (var group in groups.Where(value => value != null).Distinct())
            {
                AddAssetPath(paths, group);
                foreach (var schema in group.Schemas ?? new List<AddressableAssetGroupSchema>())
                    AddAssetPath(paths, schema);
            }
            return paths.OrderBy(path => path, StringComparer.Ordinal).ToList();
        }

        private static IReadOnlyList<string> CollectAllConfigurationPaths(
            AddressableAssetSettings settings)
        {
            return CollectConfigurationPaths(settings,
                (settings.groups ?? new List<AddressableAssetGroup>())
                    .Where(group => group != null).ToArray());
        }

        private static IReadOnlyList<string> CollectCanonicalAssetInventory()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            if (AssetDatabase.IsValidFolder(CanonicalSettingsFolder))
                paths.Add(CanonicalSettingsFolder);
            foreach (string guid in AssetDatabase.FindAssets(
                string.Empty, new[] { CanonicalSettingsFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }
            return paths.OrderBy(path => path.Length)
                .ThenBy(path => path, StringComparer.Ordinal).ToList();
        }

        private static IReadOnlyList<string> CollectSettingsAssetInventory(
            AddressableAssetSettings settings)
        {
            if (settings == null) return Array.Empty<string>();
            string settingsPath = string.IsNullOrEmpty(settings.AssetPath)
                ? AssetDatabase.GetAssetPath(settings) : settings.AssetPath;
            string folder = string.IsNullOrEmpty(settingsPath)
                ? null : Path.GetDirectoryName(settingsPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
                return CollectAllConfigurationPaths(settings);

            var paths = new HashSet<string>(StringComparer.Ordinal) { folder };
            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
            }
            return paths.OrderBy(path => path.Length)
                .ThenBy(path => path, StringComparer.Ordinal).ToList();
        }

        private static AddressableAssetSettings CreateCanonicalSettings()
        {
            return AddressableAssetSettings.Create(
                AddressableAssetSettingsDefaultObject.kDefaultConfigFolder,
                AddressableAssetSettingsDefaultObject.kDefaultConfigAssetName,
                true, true);
        }

        private static AddressablesSettingsInfo BuildSettingsInfo(
            AddressableAssetSettings settings)
        {
            if (settings == null)
                return new AddressablesSettingsInfo { SettingsExist = false };
            string path = string.IsNullOrEmpty(settings.AssetPath)
                ? AssetDatabase.GetAssetPath(settings) : settings.AssetPath;
            var profiles = settings.profileSettings == null
                ? new List<string>()
                : settings.profileSettings.GetAllProfileNames()
                    .Where(name => !string.IsNullOrEmpty(name))
                    .OrderBy(name => name, StringComparer.Ordinal).ToList();
            return new AddressablesSettingsInfo
            {
                SettingsExist = true,
                SettingsAssetPath = path,
                SettingsGuid = AssetDatabase.AssetPathToGUID(path),
                ActiveProfile = settings.profileSettings?.GetProfileName(
                    settings.activeProfileId),
                Profiles = profiles,
                DefaultGroup = settings.DefaultGroup?.Name,
                DefaultGroupGuid = settings.DefaultGroup?.Guid,
                GroupCount = (settings.groups ?? new List<AddressableAssetGroup>())
                    .Count(group => group != null),
                ConfigurationAssetPaths = CollectSettingsAssetInventory(settings)
            };
        }

        private static AddressablesSettingsInfo CloneSettingsInfo(
            AddressablesSettingsInfo source)
        {
            if (source == null) return null;
            return new AddressablesSettingsInfo
            {
                SettingsExist = source.SettingsExist,
                SettingsAssetPath = source.SettingsAssetPath,
                SettingsGuid = source.SettingsGuid,
                ActiveProfile = source.ActiveProfile,
                Profiles = (source.Profiles ?? Array.Empty<string>()).ToList(),
                DefaultGroup = source.DefaultGroup,
                DefaultGroupGuid = source.DefaultGroupGuid,
                GroupCount = source.GroupCount,
                ConfigurationAssetPaths = (source.ConfigurationAssetPaths
                    ?? Array.Empty<string>()).ToList()
            };
        }

        private static void AddAssetPath(HashSet<string> paths, UnityEngine.Object asset)
        {
            if (asset == null) return;
            string path = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(path)) paths.Add(path);
        }

        private static void RegisterUndo(
            AddressableAssetSettings settings,
            AddressablesMutationRequest request,
            string label)
        {
            var objects = new List<UnityEngine.Object> { settings };
            AddressableAssetEntry entry = string.IsNullOrEmpty(request.Guid)
                ? null
                : settings.FindAssetEntry(request.Guid);
            AddGroupAndSchemas(objects, entry?.parentGroup);
            AddGroupAndSchemas(objects, FindExactGroup(settings, request.Group));
            AddGroupAndSchemas(objects, FindExactGroup(settings, request.TemplateGroup));
            Undo.RegisterCompleteObjectUndo(objects.Where(value => value != null).Distinct().ToArray(), label);
        }

        private static void AddGroupAndSchemas(
            List<UnityEngine.Object> objects, AddressableAssetGroup group)
        {
            if (group == null) return;
            objects.Add(group);
            objects.AddRange((group.Schemas ?? new List<AddressableAssetGroupSchema>())
                .Where(schema => schema != null));
        }

        private static int BeginUndoGroup(string label)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(label);
            return group;
        }

        private static AddressablesMutationResult MutationFailure(
            string error, string suggestion = null)
        {
            return new AddressablesMutationResult
            {
                Success = false,
                Error = error,
                Suggestion = suggestion
            };
        }

        private static IReadOnlyList<string> SchemaNames(AddressableAssetGroup group)
        {
            return (group.Schemas ?? new List<AddressableAssetGroupSchema>())
                .Where(schema => schema != null)
                .Select(schema => schema.GetType().FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }

        private static IReadOnlyList<string> SchemaConfigurationSignatures(
            AddressableAssetGroup group)
        {
            return (group.Schemas ?? new List<AddressableAssetGroupSchema>())
                .Where(schema => schema != null)
                .Select(SchemaConfigurationSignature)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
        }

        private static string SchemaConfigurationSignature(
            AddressableAssetGroupSchema schema)
        {
            var values = new List<string> { schema.GetType().FullName };
            var serialized = new SerializedObject(schema);
            SerializedProperty property = serialized.GetIterator();
            bool visitChildren = true;
            while (property.Next(visitChildren))
            {
                visitChildren = true;
                if (IsIgnoredSchemaProperty(property.propertyPath))
                {
                    continue;
                }

                string value;
                try
                {
                    if (property.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        var referenced = property.objectReferenceValue;
                        string path = referenced == null
                            ? null : AssetDatabase.GetAssetPath(referenced);
                        value = referenced == null ? "null"
                            : string.IsNullOrEmpty(path)
                                ? referenced.GetType().FullName + ":" + referenced.name
                                : AssetDatabase.AssetPathToGUID(path) + ":" + path;
                    }
                    else if (property.isArray
                        && property.propertyType != SerializedPropertyType.String)
                    {
                        value = "count=" + property.arraySize;
                    }
                    else
                    {
                        value = Convert.ToString(property.boxedValue,
                            CultureInfo.InvariantCulture) ?? "null";
                    }
                }
                catch
                {
                    value = property.propertyType.ToString();
                }
                values.Add(property.propertyPath + "=" + value);
            }

            string canonical = string.Join("\n", values);
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (char character in canonical)
                {
                    hash ^= character;
                    hash *= 1099511628211UL;
                }
                return schema.GetType().FullName + ":" + hash.ToString("x16");
            }
        }

        private static bool IsIgnoredSchemaProperty(string path)
        {
            return IsPropertyOrChild(path, "m_Script")
                || IsPropertyOrChild(path, "m_Name")
                || IsPropertyOrChild(path, "m_ObjectHideFlags")
                || IsPropertyOrChild(path, "m_EditorClassIdentifier")
                || IsPropertyOrChild(path, "m_Group");
        }

        private static bool IsPropertyOrChild(string path, string property)
        {
            return string.Equals(path, property, StringComparison.Ordinal)
                || path.StartsWith(property + ".", StringComparison.Ordinal);
        }

        private static AddressablesEntryInfo CloneEntry(AddressablesEntryInfo entry)
        {
            if (entry == null) return null;
            return new AddressablesEntryInfo
            {
                Guid = entry.Guid,
                AssetPath = entry.AssetPath,
                Address = entry.Address,
                Group = entry.Group,
                AssetKind = entry.AssetKind,
                IsFolder = entry.IsFolder,
                IsScene = entry.IsScene,
                Labels = (entry.Labels ?? Array.Empty<string>()).ToList()
            };
        }

        private static string ResolveAssetKind(string assetPath)
        {
            return AssetDatabase.GetMainAssetTypeAtPath(assetPath)?.Name ?? "Unknown";
        }

        private static List<GroupedEntry> GatherAddressableScenes(
            IEnumerable<AddressableAssetGroup> groups)
        {
            return GatherAddressableEntries(groups)
                .Where(item => item.Entry.IsScene)
                .GroupBy(item => item.Entry.AssetPath ?? item.Entry.guid, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        private static List<GroupedEntry> GatherAddressableEntries(
            IEnumerable<AddressableAssetGroup> groups)
        {
            var entries = new List<GroupedEntry>();
            foreach (var group in groups ?? Array.Empty<AddressableAssetGroup>())
            {
                if (group == null) continue;
                foreach (var entry in group.entries ?? Array.Empty<AddressableAssetEntry>())
                {
                    if (entry == null) continue;
                    entries.Add(new GroupedEntry(group, entry));

                    var children = new List<AddressableAssetEntry>();
                    entry.GatherAllAssets(children, false, true, false);
                    entries.AddRange(children
                        .Where(child => child != null)
                        .Select(child => new GroupedEntry
                        {
                            Group = child.parentGroup ?? group,
                            Entry = child
                        }));
                }
            }
            return entries;
        }

        private sealed class GroupedEntry
        {
            internal AddressableAssetGroup Group;
            internal AddressableAssetEntry Entry;

            internal GroupedEntry() { }
            internal GroupedEntry(AddressableAssetGroup group, AddressableAssetEntry entry)
            {
                Group = group;
                Entry = entry;
            }
        }

        private sealed class BuildState
        {
            internal AddressableAssetSettings Settings;
            internal string ProfileId;
            internal string OriginalProfileId;
            internal string ContentStatePath;
            internal BuildTarget ActiveBuildTarget;
        }

        private static AddressablesCapability ReadyCapability(string version)
        {
            return new AddressablesCapability
            {
                Installed = true,
                Supported = true,
                State = AddressablesState.Ready,
                PackageVersion = version,
                Message = "Addressables settings are available for inspection and management."
            };
        }

        private static AddressablesGroupInfo BuildGroup(
            AddressableAssetSettings settings,
            AddressableAssetGroup group)
        {
            var bundled = group.GetSchema<BundledAssetGroupSchema>();
            var contentUpdate = group.GetSchema<ContentUpdateGroupSchema>();
            return new AddressablesGroupInfo
            {
                Name = group.Name,
                Guid = group.Guid,
                IsDefault = settings.DefaultGroup == group,
                ReadOnly = group.ReadOnly,
                EntryCount = group.entries?.Count ?? 0,
                Schemas = SchemaNames(group),
                SchemaConfigurations = SchemaConfigurationSignatures(group),
                BuildPathVariable = bundled?.BuildPath.GetName(settings),
                LoadPathVariable = bundled?.LoadPath.GetName(settings),
                BundlePackingMode = bundled?.BundleMode.ToString(),
                StaticContent = contentUpdate == null
                    ? (bool?)null : contentUpdate.StaticContent
            };
        }

        private static AddressablesEntryInfo BuildEntry(
            AddressableAssetGroup group,
            AddressableAssetEntry entry)
        {
            string assetKind;
            try
            {
                assetKind = entry.IsScene ? "Scene" : entry.MainAssetType?.Name;
            }
            catch
            {
                assetKind = null;
            }

            return new AddressablesEntryInfo
            {
                Guid = entry.guid,
                AssetPath = entry.AssetPath,
                Address = entry.address,
                Group = group.Name,
                AssetKind = assetKind ?? "Unknown",
                IsFolder = entry.IsFolder,
                IsScene = entry.IsScene,
                Labels = (entry.labels ?? new HashSet<string>())
                    .OrderBy(label => label, StringComparer.Ordinal)
                    .ToList()
            };
        }

        private static string ResolvePackageVersion()
        {
            try
            {
                return UnityEditor.PackageManager.PackageInfo
                    .FindForAssembly(typeof(AddressableAssetSettings).Assembly)?.version;
            }
            catch
            {
                return null;
            }
        }
    }
}
