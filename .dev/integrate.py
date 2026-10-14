from pathlib import Path

def edit(path, old, new, count=1):
    p=Path(path); text=p.read_text(encoding='utf-8-sig')
    found=text.count(old)
    if found != count: raise RuntimeError(f'{path}: expected {count} anchors, got {found}: {old[:100]!r}')
    p.write_text(text.replace(old,new),encoding='utf-8')

# The requested product is Windows. Keep compatibility source, not an extra release matrix.
edit('.github/workflows/build.yml','          - os: ubuntu-24.04\n            rid: linux-x64\n            executable: advkit\n','')
edit('.github/workflows/build.yml','name: Build native executables','name: Build Windows editor')

p='src/OurNotes.Director.WinUI/WindowsPreviewAudio.cs'
edit(p,'internal sealed unsafe class WindowsPreviewAudio','internal sealed class WindowsPreviewAudio')
edit(p,'    void Pump()','    unsafe void Pump()')
edit(p,'    static void CloseDevice(ref nint device, WaveHeader** headers)','    static unsafe void CloseDevice(ref nint device, WaveHeader** headers)')
edit(p,'if (headers[i] is null)','if (headers[i] == null)')
for method in ['waveOutPrepareHeader','waveOutUnprepareHeader','waveOutWrite']:
    edit(p,'static extern uint '+method,'static extern unsafe uint '+method)

p='src/OurNotes.Director.Core/NativeMotionSync.cs'
edit(p,'nint context;','nint context, mappingMemory, configurationMemory; int mappingCount;')
edit(p,'nint mapMemory=Alloc(native.Length*sizeof(Mapping)),cfg=Alloc(8);','nint mapMemory=mappingMemory=Alloc(native.Length*sizeof(Mapping)),cfg=configurationMemory=Alloc(8);mappingCount=native.Length;')
edit(p,'public void ResetInput()=>pendingCount=0;', '''public void ResetInput()
    {
        if(context==0)throw new ObjectDisposedException(nameof(NativeMotionSync));
        engine.Delete(context);context=engine.Create(configurationMemory,mappingMemory,mappingCount);
        if(context==0)throw new InvalidOperationException("Cannot reset MotionSync context");
        pendingCount=0;Array.Clear(result);
    }''')
edit(p,'while(offset+required<pendingCount)','while(offset+required<=pendingCount)')

p='src/OurNotes.Director.Core/AdvAudio.cs'
edit(p,'PcmSince(AdvSound sound,ref long previousSample)','PcmSince(AdvSound sound,ref long previousSample,int lookAheadSamples=0)')
edit(p,'long now=Math.Max(0,Position(sound)-sound.StartSample);int count=', 'long now=Math.Max(0,Position(sound)-sound.StartSample+lookAheadSamples);int count=')

p='src/OurNotes.Director.Core/AdvUi.cs'
edit(p,'int[] PlayVoices(AdvRow r,bool lipSync=false){StopVoices();','int[] PlayVoices(AdvRow r,bool lipSync=false){r=ResolveExternalVoice(r);StopVoices();')
p='src/OurNotes.Director.Core/AdvStoryRenderer.cs'
edit(p,'Ui.ResolveCharacter=Scene.Character;', 'Ui.AudioProjectDirectory=script is null?Environment.CurrentDirectory:Path.GetDirectoryName(Path.GetFullPath(script))!;Ui.AudioFfmpegPath=ffmpeg;\n            Ui.ResolveCharacter=Scene.Character;')

# Opt-in time-positioned clips. Original scripts without AtFrame retain their exact flow.
p='src/OurNotes.Director.Core/AdvPlayback.cs'
edit(p,'        ListIndex=0;\n        while(ListIndex<program.Rows.Length)', '''        if(program.Rows.Any(row=>row.Data["AtFrame"] is not null))
        {
            foreach(var wait in PlayScheduled())yield return wait;
            OnStop?.Invoke();yield break;
        }
        ListIndex=0;
        while(ListIndex<program.Rows.Length)''')
edit(p,'    private IEnumerable<AdvWait> Play()','''    private IEnumerable<AdvWait> PlayScheduled()
    {
        int previous=-1;
        for(int i=0;i<program.Rows.Length;i++)
        {
            var row=program.Rows[i];if(row.Ignored)continue;
            if(row.Data["AtFrame"] is not JsonValue value||!value.TryGetValue<int>(out int at)||at<previous||at<0||at>1080000)
                throw new ArgumentException("Scheduled clips require ordered nonnegative AtFrame values");
            if(row.Command is "GoTo" or "Wait" or "ForceAuto")throw new ArgumentException("Interactive flow cannot be a scheduled clip");
            previous=at;
            if(at>Loop.Frame)yield return Loop.DelayFrames(at-Loop.Frame);
            if(Cancelled)yield break;
            ListIndex=i;detached.Add(Execute(row,"commands",i));
        }
    }
    private IEnumerable<AdvWait> Play()''')
p='src/OurNotes.Director.Core/DirectorValidation.cs'
edit(p,'"Value","Key"','"Value","Key","AtFrame"')
p='src/OurNotes.Director.Core/DirectorProject.cs'
edit(p,'var node=Archive.ReadJson(path);return node is JsonObject obj', 'var node=Archive.ReadJson(path);if(node is JsonObject creative&&Archive.S(creative,"format")==CreativeScene.Format)return new CreativeScene(creative).Compile();return node is JsonObject obj')

p='src/OurNotes.Director.Core/RealtimePreviewSession.cs'
edit(p,'    public event EventHandler<PreviewFrame>? FrameReady;','    public IPreviewPlaybackClock? PlaybackClock { get; set; }\n    public event EventHandler<PreviewFrame>? FrameReady;')
edit(p,'playing=true;playFrom=frame;playClock=Stopwatch.GetTimestamp();ResetRate();','playing=true;playFrom=frame;playClock=Stopwatch.GetTimestamp();PlaybackClock?.Start(frame/(double)renderer!.Fps);ResetRate();')
edit(p,'    public void Pause()\n    {','    public void Pause()\n    {\n        PlaybackClock?.Pause();')
edit(p,'        playing=false;Interlocked.Exchange(ref playRequested,0);','        PlaybackClock?.Pause();playing=false;Interlocked.Exchange(ref playRequested,0);',3)
edit(p,'if(playing){playFrom=frame;playClock=Stopwatch.GetTimestamp();}', 'if(playing){playFrom=frame;playClock=Stopwatch.GetTimestamp();PlaybackClock?.Start(frame/(double)renderer!.Fps);}')
edit(p,'int target=FrameAt(playFrom/(double)renderer.Fps+elapsed);', 'double? audioPosition=PlaybackClock?.PositionSeconds;\n                    int target=FrameAt(audioPosition??(playFrom/(double)renderer.Fps+elapsed));')
edit(p,'if(frame>=renderer.EndFrame)\n                    {', 'if(frame>=renderer.EndFrame&&(audioPosition is null||audioPosition.Value>=(renderer.EndFrame+1)/(double)renderer.Fps-0.00001))\n                    {\n                        PlaybackClock?.Pause();')
edit(p,'                    double remaining=(frame-playFrom+1)', '                    if(audioPosition is not null){wake.WaitOne(5);continue;}\n                    double remaining=(frame-playFrom+1)')
edit(p,'Timing?.EndFrame??30*36000:base.EndFrame;', 'Timing?.EndFrame??30*36000:Math.Max(0,checked((int)Math.Ceiling(Duration*Fps))-1);')

p='src/OurNotes.Director.WinUI/NativeViewport.cs'
edit(p,'InitializeWorkspaceNavigation();InitializeGpuPresentation();','InitializeWorkspaceNavigation();InitializeGpuPresentation();InitializeAudioPresentation();')
edit(p,'await session.LoadSceneAsync(source with{GpuPresentation=true});await AwaitPresentationAsync();','await session.LoadSceneAsync(source with{GpuPresentation=true});await AwaitPresentationAsync();\n            if(version==sourceVersion)ConfigureAudio(source);')
edit(p,'long BeginLoad(string message)\n    {','long BeginLoad(string message)\n    {\n        previewAudio.Clear();')
edit(p,'session.Pause();HasCurrentSource=false;loading=false;sourceVersion++;','previewAudio.Clear();session.Pause();HasCurrentSource=false;loading=false;sourceVersion++;')
edit(p,'public Task CommitSceneProjectAsync(ScenePreviewSource source)=>session.CommitSceneProjectAsync(source with{GpuPresentation=true});','public async Task CommitSceneProjectAsync(ScenePreviewSource source){await session.CommitSceneProjectAsync(source with{GpuPresentation=true});ConfigureAudio(source);}')
edit(p,'DetachGpu();await session.DisposeAsync();','DetachGpu();await session.DisposeAsync();await previewAudio.DisposeAsync();')
p='src/OurNotes.Director.WinUI/NativeViewport.Navigation.cs'
edit(p,'showingEmptyCanvas = true;', 'previewAudio.Clear(); showingEmptyCanvas = true;')

p='src/OurNotes.Director.Core/CreativeScene.cs'
edit(p,'if (t.Command == "Character" && actor.Length > 0 && !scene.Actors', 'if (t.Command == "Character" && actor.Length > 0 && Text(row, "Model").Length > 0 && !scene.Actors')
print('Integrated Windows audio-clocked playback, exact-frame clips and authored lip mappings.')
