using OurNotes.Director.Desktop.Tests;

if (await EngineRunnerTests.HandleWorkerAsync(args) is { } fixtureExit) return fixtureExit;

var count = 0;
void Check(bool passed, string message)
{
    if (!passed) throw new InvalidOperationException("FAIL: " + message);
    count++;
}
CommandCatalogTests.Run(Check);
SceneDocumentTests.Run(Check);
StudioSceneDocumentTests.Run(Check);
HomeStudioDocumentTests.Run(Check);
ResultRowTests.Run(Check);
await EngineRunnerTests.RunAsync(Check);
Console.WriteLine($"Passed {count} desktop command, JSON document, result and task execution checks.");
return 0;
