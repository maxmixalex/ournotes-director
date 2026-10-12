// The graphics suite needs Mesa/EGL on Linux; the package suite loads the actual
// shipped Core/MotionSync libraries and does not need a game archive or model.
try
{
    if (args.Length == 2 && args[0] == "--runtime-package") return RuntimeDependencyTests.Run(args[1]);
    if (args.Length == 2 && args[0] == "--home-fixture") return HomeRenderingTests.WriteFixture(args[1]);
    if (args.Length == 2 && args[0] == "--scene-fixture") return RealtimePreviewTests.WriteFixture(args[1]);
    int result = MaterialStateTests.Run();
    if (result != 0) return result;
    result = SpineTests.RunGraphics();
    if (result != 0) return result;
    result = HomeRenderingTests.RunGraphics();
    if (result != 0) return result;
    result = HomeProjectRenderingTests.Run();
    if (result != 0) return result;
    result = RealtimePreviewTests.Run();
    return result != 0 ? result : RenderLanguageTests.Run();
}
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
