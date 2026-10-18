// The graphics suite needs Mesa/EGL on Linux; the package suite loads the actual
// shipped Core/MotionSync libraries and does not need a game archive or model.
try
{
    if (args.Length == 2 && args[0] == "--preview-quality") return PreviewQualityNativeTests.RunGraphics(args[1]);
    if (args.Length == 3 && args[0] == "--live2d") return await Live2DNativeTests.RunAsync(args[1], args[2]);
    if(args.Length==3&&args[0]=="--stage-authoring")return await CreativeStagingNativeTests.RunAsync(args[1],args[2]);
    if (args.Length == 1 && args[0] == "--preview-editing") return await PreviewEditingTests.RunAsync();
    if (args.Length == 3 && args[0] == "--creative-audio") return await CreativeAudioTests.RunAsync(args[1],args[2]);
    if (args.Length == 2 && args[0] == "--runtime-package") return RuntimeDependencyTests.Run(args[1]);
    if (args.Length == 2 && args[0] == "--home-fixture") return HomeRenderingTests.WriteFixture(args[1]);
    if (args.Length == 2 && args[0] == "--scene-fixture") return RealtimePreviewTests.WriteFixture(args[1]);
    if (args.Length == 3 && args[0] == "--studio-scene") return await StudioSceneRealTests.RunAsync(args[1], args[2]);
    if (args.Length == 3 && args[0] == "--studio-home") return StudioHomeRealTests.Run(args[1], args[2]);
    int result = StudioWorkflowNativeTests.Run();
    if (result != 0) return result;
    result = MaterialStateTests.Run();
    if (result != 0) return result;
    result = SpineTests.RunGraphics();
    if (result != 0) return result;
    result = HomeRenderingTests.RunGraphics();
    if (result != 0) return result;
    result = HomeProjectRenderingTests.Run();
    if (result != 0) return result;
    result = HomeSpineRecoveryTests.Run();
    if (result != 0) return result;
    result = RealtimePreviewTests.Run();
    return result != 0 ? result : RenderLanguageTests.Run();
}
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
