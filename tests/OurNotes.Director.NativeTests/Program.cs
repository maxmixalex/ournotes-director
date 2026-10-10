// The graphics suite needs Mesa/EGL on Linux; the package suite loads the actual
// shipped Core/MotionSync libraries and does not need a game archive or model.
try
{
    if (args.Length == 2 && args[0] == "--runtime-package") return RuntimeDependencyTests.Run(args[1]);
    int result = MaterialStateTests.Run();
    return result != 0 ? result : RenderLanguageTests.Run();
}
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
