// The graphics suite needs Mesa/EGL on Linux; the package suite loads the actual
// shipped Core/MotionSync libraries and does not need a game archive or model.
try
{
    if (await StoryTimingAnalysisTests.HandleDecoderWorkerAsync(args) is {} workerExit) return workerExit;
    if (args.Length == 3 && args[0] == "--model-cache") return ModelResourceCacheNativeTests.Run(args[1], args[2]);
    if (args.Length == 3 && args[0] == "--millsage-blur") return await MillsageBlurNativeTests.RunAsync(args[1], args[2]);
    if (args.Length == 5 && args[0] == "--specific-antialiasing")
        return SpecificAntialiasingNativeTests.Run(args[4], args[1], args[2], args[3]);
    if (args.Length == 3 && args[0] == "--story-analysis")
    {
        int timing = await StoryTimingAnalysisTests.RunAsync(args[1], args[2]);
        return timing != 0 ? timing : StoryTimingResourceTests.Run(args[1], args[2]);
    }
    if (args.Length == 2 && args[0] == "--preview-quality") return PreviewQualityNativeTests.RunGraphics(args[1]);
    if (args.Length == 3 && args[0] == "--live2d")
    {
        int live2d = await Live2DNativeTests.RunAsync(args[1], args[2]);
        return live2d != 0 ? live2d : GraphicsQualityNativeTests.Run(args[2],
            Path.Combine(NativeTestGraphics.PackageDirectory, "tools", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"));
    }
    if(args.Length==3&&args[0]=="--stage-authoring")
    {
        int staged = await CreativeStagingNativeTests.RunAsync(args[1],args[2]);
        if (staged != 0) return staged;
        int interaction = await SceneInteractionNativeTests.RunAsync(args[1],args[2]);
        return interaction != 0 ? interaction : await FocusStateNativeTests.RunAsync(args[1],args[2]);
    }
    if (args.Length == 1 && args[0] == "--preview-editing")
    {
        int editing = await PreviewEditingTests.RunAsync();
        if (editing != 0) return editing;
        int home = await HomePreviewResolutionTests.RunAsync();
        return home != 0 ? home : await ScenePreviewResolutionTests.RunAsync();
    }
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
