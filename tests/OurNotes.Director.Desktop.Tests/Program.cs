using OurNotes.Director.Desktop.Tests;

if (args.Length == 4 && args[0] == "--audio-output")
    return await WindowsPreviewAudioTests.RunRealVoiceAsync(args[1], args[2], args[3]);

if (await EngineRunnerTests.HandleWorkerAsync(args) is { } fixtureExit) return fixtureExit;

var count = 0;
void Check(bool passed, string message)
{
    if (!passed) throw new InvalidOperationException("FAIL: " + message);
    count++;
}
CommandCatalogTests.Run(Check);
AtomicWriteTests.Run(Check);
SceneDocumentTests.Run(Check);
StudioSceneDocumentTests.Run(Check);
HomeStudioDocumentTests.Run(Check);
Live2DDocumentTests.Run(Check);
ActorCanvasTests.Run(Check);
StudioWorkflowTests.Run(Check);
CreativeExportTests.Run(Check);
CreativeStagingTests.Run(Check);
CreativeMaterialTests.Run(Check);
CreativeMaterialFrameTests.Run(Check);
StagePlacementTests.Run(Check);
ViewportNavigationTests.Run(Check);
PreviewResolutionTests.Run(Check);
WorkspaceLayoutSettingsTests.Run(Check);
await TimelineEditingTests.RunAsync(Check);
EditorPerformanceTests.Run(Check);
GpuPreferenceTests.Run(Check);
CreativeSourceImporterTests.Run(Check);
SceneInteractionPreviewTests.Run(Check);
ResultRowTests.Run(Check);
await EngineRunnerTests.RunAsync(Check);
await WindowsPreviewAudioTests.RunAsync(Check);
Console.WriteLine($"Passed {count} desktop command, JSON document, result and task execution checks.");
return 0;
