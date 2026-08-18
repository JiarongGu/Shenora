# Namespace moves — v0.10.0 → v0.11.0

Generated: `node devtools/dev.mjs namespace-moves v0.10.0 v0.11.0`. Matched by TYPE NAME, so a
type that moved AND was renamed shows as gone — that is `devtools/retired-names.txt`'s half.

| was | is |
|---|---|
| `Shenora.Core.AppCallback` | `Shenora.AppCallback` |
| `Shenora.Core.AppRootArgument` | `Shenora.AppRootArgument` |
| `Shenora.Core.ClaimMode` | `Shenora.Engine.Missions.ClaimMode` |
| `Shenora.Core.EventBus` | `Shenora.Core.Events.EventBus` |
| `Shenora.Core.EventMessage` | `Shenora.Core.Events.EventMessage` |
| `Shenora.Core.FileDialogFilter` | `Shenora.Modules.FileDialog.FileDialogFilter` |
| `Shenora.Core.FileDialogOptions` | `Shenora.Modules.FileDialog.FileDialogOptions` |
| `Shenora.Core.FileDialogResult` | `Shenora.Modules.FileDialog.FileDialogResult` |
| `Shenora.Core.FileLockHolder` | `Shenora.Core.Shell.FileLockHolder` |
| `Shenora.Core.FileReplacement` | `Shenora.Engine.Files.FileReplacement` |
| `Shenora.Core.FileWriteMode` | `Shenora.Engine.Files.FileWriteMode` |
| `Shenora.Core.Files` | `Shenora.Engine.Files.Files` |
| `Shenora.Core.FlatClaimScope` | `Shenora.Engine.Missions.FlatClaimScope` |
| `Shenora.Core.HeadlessHostExtensions` | `Shenora.HeadlessHostExtensions` |
| `Shenora.Core.HeadlessRunnerOptions` | `Shenora.HeadlessRunnerOptions` |
| `Shenora.Core.IClaimScope` | `Shenora.Engine.Missions.IClaimScope` |
| `Shenora.Core.IClipboardService` | `Shenora.Core.Shell.IClipboardService` |
| `Shenora.Core.IEventBus` | `Shenora.Core.Events.IEventBus` |
| `Shenora.Core.IFileDialogPathStore` | `Shenora.Modules.FileDialog.IFileDialogPathStore` |
| `Shenora.Core.IFileDialogs` | `Shenora.Modules.FileDialog.IFileDialogs` |
| `Shenora.Core.IFileLockInspector` | `Shenora.Core.Shell.IFileLockInspector` |
| `Shenora.Core.ILane` | `Shenora.Engine.Missions.ILane` |
| `Shenora.Core.ILiveActivities` | `Shenora.Modules.Platform.ILiveActivities` |
| `Shenora.Core.IMissionChainContext` | `Shenora.Engine.Missions.IMissionChainContext` |
| `Shenora.Core.IMissionObserver` | `Shenora.Engine.Missions.IMissionObserver` |
| `Shenora.Core.IMissionPolicy` | `Shenora.Engine.Missions.IMissionPolicy` |
| `Shenora.Core.IMissionQueueStore` | `Shenora.Engine.Missions.IMissionQueueStore` |
| `Shenora.Core.IMissionScheduler` | `Shenora.Engine.Missions.IMissionScheduler` |
| `Shenora.Core.IPlaybackSession` | `Shenora.Modules.Platform.IPlaybackSession` |
| `Shenora.Core.IShenoraLifecycleHook` | `Shenora.IShenoraLifecycleHook` |
| `Shenora.Core.IShenoraRunner` | `Shenora.IShenoraRunner` |
| `Shenora.Core.IUiDispatcher` | `Shenora.Core.Shell.IUiDispatcher` |
| `Shenora.Core.IUiInteraction` | `Shenora.Core.Shell.IUiInteraction` |
| `Shenora.Core.IUrlLauncher` | `Shenora.Core.Shell.IUrlLauncher` |
| `Shenora.Core.IWebViewInterceptor` | `Shenora.Core.WebView.IWebViewInterceptor` |
| `Shenora.Core.LiveActivityState` | `Shenora.Modules.Platform.LiveActivityState` |
| `Shenora.Core.MissionChain` | `Shenora.Engine.Missions.MissionChain` |
| `Shenora.Core.MissionClaim` | `Shenora.Engine.Missions.MissionClaim` |
| `Shenora.Core.MissionDefinition` | `Shenora.Engine.Missions.MissionDefinition` |
| `Shenora.Core.MissionExecution` | `Shenora.Engine.Missions.MissionExecution` |
| `Shenora.Core.MissionKey` | `Shenora.Engine.Missions.MissionKey` |
| `Shenora.Core.MissionLane` | `Shenora.Engine.Missions.MissionLane` |
| `Shenora.Core.MissionOutcome` | `Shenora.Engine.Missions.MissionOutcome` |
| `Shenora.Core.MissionRecord` | `Shenora.Engine.Missions.MissionRecord` |
| `Shenora.Core.MissionResult` | `Shenora.Engine.Missions.MissionResult` |
| `Shenora.Core.MissionScheduler` | `Shenora.Engine.Missions.MissionScheduler` |
| `Shenora.Core.MissionSchedulerOptions` | `Shenora.Engine.Missions.MissionSchedulerOptions` |
| `Shenora.Core.MissionSchedulerState` | `Shenora.Engine.Missions.MissionSchedulerState` |
| `Shenora.Core.MissionState` | `Shenora.Engine.Missions.MissionState` |
| `Shenora.Core.MissionStep` | `Shenora.Engine.Missions.MissionStep` |
| `Shenora.Core.NestedClaimScope` | `Shenora.Engine.Missions.NestedClaimScope` |
| `Shenora.Core.OpenFileOptions` | `Shenora.Modules.FileDialog.OpenFileOptions` |
| `Shenora.Core.OpenFolderOptions` | `Shenora.Modules.FileDialog.OpenFolderOptions` |
| `Shenora.Core.PathClaims` | `Shenora.Engine.Files.PathClaims` |
| `Shenora.Core.PlaybackCommand` | `Shenora.Modules.Platform.PlaybackCommand` |
| `Shenora.Core.PlaybackCommandRequest` | `Shenora.Modules.Platform.PlaybackCommandRequest` |
| `Shenora.Core.PlaybackInfo` | `Shenora.Modules.Platform.PlaybackInfo` |
| `Shenora.Core.PlaybackProgress` | `Shenora.Modules.Platform.PlaybackProgress` |
| `Shenora.Core.PlaybackState` | `Shenora.Modules.Platform.PlaybackState` |
| `Shenora.Core.PriorityMissionPolicy` | `Shenora.Engine.Missions.PriorityMissionPolicy` |
| `Shenora.Core.RecoveryPolicy` | `Shenora.Engine.Missions.RecoveryPolicy` |
| `Shenora.Core.RetryPolicy` | `Shenora.Engine.RetryPolicy` |
| `Shenora.Core.SafeAreaInsets` | `Shenora.Modules.Platform.SafeAreaInsets` |
| `Shenora.Core.SafeAreaOptions` | `Shenora.Modules.Platform.SafeAreaOptions` |
| `Shenora.Core.SafeAreaScript` | `Shenora.Modules.Platform.SafeAreaScript` |
| `Shenora.Core.SaveFileOptions` | `Shenora.Modules.FileDialog.SaveFileOptions` |
| `Shenora.Core.ShellCapability` | `Shenora.Core.Shell.ShellCapability` |
| `Shenora.Core.ShenoraApplication` | `Shenora.ShenoraApplication` |
| `Shenora.Core.ShenoraApplicationBuilder` | `Shenora.ShenoraApplicationBuilder` |
| `Shenora.Core.ShenoraApplicationOptions` | `Shenora.ShenoraApplicationOptions` |
| `Shenora.Core.ShenoraEnvironment` | `Shenora.ShenoraEnvironment` |
| `Shenora.Core.ShenoraPaths` | `Shenora.ShenoraPaths` |
| `Shenora.Core.ShenoraPathsOptions` | `Shenora.ShenoraPathsOptions` |
| `Shenora.Core.UiTargetState` | `Shenora.Core.Shell.UiTargetState` |
| `Shenora.Core.WebViewByteRange` | `Shenora.Core.WebView.WebViewByteRange` |
| `Shenora.Core.WebViewContentTypes` | `Shenora.Core.WebView.WebViewContentTypes` |
| `Shenora.Core.WebViewFileOptions` | `Shenora.Core.WebView.WebViewFileOptions` |
| `Shenora.Core.WebViewFiles` | `Shenora.Core.WebView.WebViewFiles` |
| `Shenora.Core.WebViewInterceptorExtensions` | `Shenora.Core.WebView.WebViewInterceptorExtensions` |
| `Shenora.Core.WebViewRangeDelivery` | `Shenora.Core.WebView.WebViewRangeDelivery` |
| `Shenora.Core.WebViewResourceHandler` | `Shenora.Core.WebView.WebViewResourceHandler` |
| `Shenora.Core.WebViewResourceMiddleware` | `Shenora.Core.WebView.WebViewResourceMiddleware` |
| `Shenora.Core.WebViewResourcePipeline` | `Shenora.Core.WebView.WebViewResourcePipeline` |
| `Shenora.Core.WebViewResourceRequest` | `Shenora.Core.WebView.WebViewResourceRequest` |
| `Shenora.Core.WebViewResourceResponse` | `Shenora.Core.WebView.WebViewResourceResponse` |
| `Shenora.IO.Compression.ExtractionLimits` | `Shenora.Engine.Compression.ExtractionLimits` |
| `Shenora.IO.Compression.ExtractionResult` | `Shenora.Engine.Compression.ExtractionResult` |
| `Shenora.IO.Compression.ZipExtraction` | `Shenora.Engine.Compression.ZipExtraction` |
| `Shenora.IO.Compression.ZipUpdateSource` | `Shenora.Engine.Update.ZipUpdateSource` |
| `Shenora.IO.FileAtomicity` | `Shenora.Engine.Files.FileAtomicity` |
| `Shenora.IO.FileChange` | `Shenora.Engine.Files.FileChange` |
| `Shenora.IO.FilePathLocker` | `Shenora.Engine.Files.FilePathLocker` |
| `Shenora.IO.FilePathLockerOptions` | `Shenora.Engine.Files.FilePathLockerOptions` |
| `Shenora.IO.FileUndoKind` | `Shenora.Engine.Files.FileUndoKind` |
| `Shenora.IO.FileUndoStep` | `Shenora.Engine.Files.FileUndoStep` |
| `Shenora.IO.FileUpdate` | `Shenora.Engine.Files.FileUpdate` |
| `Shenora.IO.FileUpdateJournal` | `Shenora.Engine.Files.FileUpdateJournal` |
| `Shenora.IO.FileUpdateJournalEntry` | `Shenora.Engine.Files.FileUpdateJournalEntry` |
| `Shenora.IO.FileUpdateJournalOptions` | `Shenora.Engine.Files.FileUpdateJournalOptions` |
| `Shenora.IO.FileUpdateQueue` | `Shenora.Engine.Files.FileUpdateQueue` |
| `Shenora.IO.FileUpdateQueueOptions` | `Shenora.Engine.Files.FileUpdateQueueOptions` |
| `Shenora.IO.FileUpdateResult` | `Shenora.Engine.Files.FileUpdateResult` |
| `Shenora.IO.FileUpdateStage` | `Shenora.Engine.Files.FileUpdateStage` |
| `Shenora.IO.IFileUpdateJournal` | `Shenora.Engine.Files.IFileUpdateJournal` |
| `Shenora.IO.IFileUpdateQueue` | `Shenora.Engine.Files.IFileUpdateQueue` |
| `Shenora.IO.IPathLease` | `Shenora.Engine.Files.IPathLease` |
| `Shenora.IO.IPathLocker` | `Shenora.Engine.Files.IPathLocker` |
| `Shenora.IO.IUpdateSource` | `Shenora.Engine.Update.IUpdateSource` |
| `Shenora.IO.ManifestDiff` | `Shenora.Engine.Update.ManifestDiff` |
| `Shenora.IO.ManifestFile` | `Shenora.Engine.Update.ManifestFile` |
| `Shenora.IO.UpdateManifest` | `Shenora.Engine.Update.UpdateManifest` |
| `Shenora.IO.UpdateOutcome` | `Shenora.Engine.Update.UpdateOutcome` |
| `Shenora.IO.UpdateStage` | `Shenora.Engine.Update.UpdateStage` |
| `Shenora.IO.UpdateStageOptions` | `Shenora.Engine.Update.UpdateStageOptions` |
| `Shenora.IO.UpdateStageStatus` | `Shenora.Engine.Update.UpdateStageStatus` |
| `Shenora.Ipc.FileDialogServiceCollectionExtensions` | `Shenora.FileDialogServiceCollectionExtensions` |
| `Shenora.Ipc.IMessageDispatcher` | `Shenora.Core.Ipc.IMessageDispatcher` |
| `Shenora.Ipc.IModuleContext` | `Shenora.Core.Ipc.IModuleContext` |
| `Shenora.Ipc.IModuleRegistry` | `Shenora.Core.Ipc.IModuleRegistry` |
| `Shenora.Ipc.IpcCategories` | `Shenora.Core.Ipc.IpcCategories` |
| `Shenora.Ipc.IpcError` | `Shenora.Core.Ipc.IpcError` |
| `Shenora.Ipc.IpcErrorCodes` | `Shenora.Core.Ipc.IpcErrorCodes` |
| `Shenora.Ipc.IpcErrorMapping` | `Shenora.Core.Ipc.IpcErrorMapping` |
| `Shenora.Ipc.IpcHostBridge` | `Shenora.Core.Ipc.IpcHostBridge` |
| `Shenora.Ipc.IpcHostBridgeOptions` | `Shenora.Core.Ipc.IpcHostBridgeOptions` |
| `Shenora.Ipc.IpcJson` | `Shenora.Core.Ipc.IpcJson` |
| `Shenora.Ipc.IpcNotification` | `Shenora.Core.Ipc.IpcNotification` |
| `Shenora.Ipc.IpcNotificationBatch` | `Shenora.Core.Ipc.IpcNotificationBatch` |
| `Shenora.Ipc.IpcRequest` | `Shenora.Core.Ipc.IpcRequest` |
| `Shenora.Ipc.IpcResponse` | `Shenora.Core.Ipc.IpcResponse` |
| `Shenora.Ipc.IpcServiceCollectionExtensions` | `Shenora.IpcServiceCollectionExtensions` |
| `Shenora.Ipc.MessageDispatcher` | `Shenora.Core.Ipc.MessageDispatcher` |
| `Shenora.Ipc.MessageDispatcherExtensions` | `Shenora.Core.Ipc.MessageDispatcherExtensions` |
| `Shenora.Ipc.MessageMiddleware` | `Shenora.Core.Ipc.MessageMiddleware` |
| `Shenora.Ipc.ModuleRouteBuilder` | `Shenora.Core.Ipc.ModuleRouteBuilder` |
| `Shenora.Ipc.NotificationPump` | `Shenora.Core.Ipc.NotificationPump` |
| `Shenora.Ipc.NotificationPumpOptions` | `Shenora.Core.Ipc.NotificationPumpOptions` |
| `Shenora.Ipc.PayloadHelper` | `Shenora.Core.Ipc.PayloadHelper` |
| `Shenora.Ipc.ScopedContainerRouter` | `Shenora.Core.Ipc.ScopedContainerRouter` |
| `Shenora.Ipc.ScopedContainerRouterExtensions` | `Shenora.Core.Ipc.ScopedContainerRouterExtensions` |
| `Shenora.Ipc.ScopedContainerRouterOptions` | `Shenora.Core.Ipc.ScopedContainerRouterOptions` |
| `Shenora.Ipc.ShellInfo` | `Shenora.Core.Ipc.ShellInfo` |
| `Shenora.Media.MediaConversionEvents` | `Shenora.Modules.Media.MediaConversionEvents` |
| `Shenora.Media.MediaConversionExtensions` | `Shenora.Modules.Media.MediaConversionExtensions` |
| `Shenora.Media.MediaConversionOptions` | `Shenora.Modules.Media.MediaConversionOptions` |
| `Shenora.Media.MediaConversionRequest` | `Shenora.Modules.Media.MediaConversionRequest` |
| `Shenora.Media.MediaPlaybackAction` | `Shenora.Modules.Media.MediaPlaybackAction` |
| `Shenora.Media.MediaPlaybackPlan` | `Shenora.Modules.Media.MediaPlaybackPlan` |
| `Shenora.Media.MediaPlaybackPlanner` | `Shenora.Modules.Media.MediaPlaybackPlanner` |
| `Shenora.Media.MediaPlaybackPolicy` | `Shenora.Modules.Media.MediaPlaybackPolicy` |
| `Shenora.Media.MediaProbeResult` | `Shenora.Modules.Media.MediaProbeResult` |
| `Shenora.Media.MediaStreamInfo` | `Shenora.Modules.Media.MediaStreamInfo` |
| `Shenora.Media.MediaStreamKind` | `Shenora.Modules.Media.MediaStreamKind` |
| `Shenora.Media.MediaStreamPlan` | `Shenora.Modules.Media.MediaStreamPlan` |

Gone from the public surface (renamed, made internal, or removed) — 30:

- `Shenora.Core.DerivedCacheKey`
- `Shenora.Core.IShenoraModule`
- `Shenora.Ipc.BaseFacade`
- `Shenora.Ipc.FileDialogFacade`
- `Shenora.Ipc.IModuleFacade`
- `Shenora.Ipc.IOperation`
- `Shenora.Ipc.IOperationRegistry`
- `Shenora.Ipc.OperationEvents`
- `Shenora.Ipc.OperationException`
- `Shenora.Ipc.OperationInfo`
- `Shenora.Ipc.OperationLabel`
- `Shenora.Ipc.OperationOptions`
- `Shenora.Ipc.OperationProgress`
- `Shenora.Ipc.OperationRegistry`
- `Shenora.Ipc.OperationRegistryOptions`
- `Shenora.Ipc.OperationServiceCollectionExtensions`
- `Shenora.Ipc.OperationStatus`
- `Shenora.Ipc.OperationsFacade`
- `Shenora.Windows.DropZoneFacade`
- `Shenora.Windows.SessionApiCall`
- `Shenora.Windows.SessionBrowser`
- `Shenora.Windows.SessionEndReason`
- `Shenora.Windows.SessionEnded`
- `Shenora.Windows.SessionErrorCodes`
- `Shenora.Windows.SessionFrame`
- `Shenora.Windows.SessionResult`
- `Shenora.Windows.WebView2Interceptor`
- `Shenora.Windows.WinFormsHostExtensions`
- `Shenora.Windows.WinFormsHostOptions`
- `Shenora.Windows.WindowCommandFacade`

154 moved, 30 gone, 335 public type(s) at v0.11.0.
