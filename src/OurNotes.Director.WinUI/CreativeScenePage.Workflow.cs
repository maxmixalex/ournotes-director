using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    string? replaceAssetClip;
    string? loadedSourceKey;
    MenuFlyout FileMenu()
    {
        var menu=new MenuFlyout();
        menu.Items.Add(StudioMenus.Async("新建空白场景",()=>Safe(()=>NewAsync()),"\uE710"));
        menu.Items.Add(StudioMenus.Async("打开场景…",()=>Safe(()=>OpenAsync()),"\uE8E5"));
        menu.Items.Add(StudioMenus.Async("打开原剧情…",()=>Safe(OpenStoryAsync),"\uE8A5"));
        menu.Items.Add(new MenuFlyoutSeparator());menu.Items.Add(StudioMenus.Async("另存为…",()=>Safe(()=>SaveAsync(true)),"\uE792"));
        menu.Items.Add(StudioMenus.Async("恢复未保存草稿",()=>Safe(()=>OpenAsync(draftPath)),"\uE777",File.Exists(draftPath)));return menu;
    }
    MenuFlyout EditMenu()
    {
        var menu=new MenuFlyout();menu.Items.Add(StudioMenus.Async("撤销",()=>Safe(()=>UndoAsync(false)),"\uE7A7",undo.Count>0,"Ctrl+Z"));
        menu.Items.Add(StudioMenus.Async("重做",()=>Safe(()=>UndoAsync(true)),"\uE7A6",redo.Count>0,"Ctrl+Y"));menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Async("舞台与站位…",()=>Safe(ChooseStageAsync),"\uE91B"));
        menu.Items.Add(StudioMenus.Async("添加停留片段",()=>Safe(()=>EditAsync(()=>{selectedClip=document.AddClip("","hold",playhead,1,new JsonObject{["cmd"]="Delay",["Frames"]=0},"停留");})),"\uE916"));
        menu.Items.Add(StudioMenus.Async("插入演出指令…",()=>Safe(()=>InsertNativeClipAsync(selectedActor,playhead)),"\uE943"));
        menu.Items.Add(StudioMenus.Async("粘贴场景 JSON…",()=>Safe(PasteAsync),"\uE943"));return menu;
    }
    MenuFlyout ViewMenu()
    {
        var menu=new MenuFlyout();menu.Items.Add(StudioMenus.Item("画布适应窗口",viewport.FitCanvas,"\uE9A6",true,"F"));
        var follow = new ToggleMenuFlyoutItem { Text = "单击片段时定位画面", IsChecked = timeline.FollowSelection };
        follow.Click += (_, _) => timeline.FollowSelection = follow.IsChecked; menu.Items.Add(follow);
        menu.Items.Add(StudioMenus.Item("画布设置",ClearSelection,"\uE740"));
        menu.Items.Add(StudioMenus.Async("场景 JSON…",()=>Safe(ShowJsonAsync),"\uE943"));
        menu.Items.Add(StudioMenus.Async("查看原生指令…",()=>Safe(ShowNativeAsync),"\uE8A5"));AddPlacementModeMenu(menu);return menu;
    }
    MenuFlyout ActorMenu()
    {
        var menu=new MenuFlyout();
        if(selectedActor is {Length:>0})
        {
            AddStageMenuItems(menu,selectedActor);
            menu.Items.Add(StudioMenus.Async("在 Live2D 工作台中打开…",()=>Safe(()=>OpenActorInLive2DAsync()),"\uE8A7"));menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(StudioMenus.Item("选择动作",()=>{assetKind.SelectedIndex=0;ShowProjectAssets();_=Safe(LoadAssetsAsync);}));
            menu.Items.Add(StudioMenus.Item("选择表情",()=>{assetKind.SelectedIndex=1;ShowProjectAssets();_=Safe(LoadAssetsAsync);}));
            menu.Items.Add(StudioMenus.Async("添加声音…",()=>Safe(ImportVoiceAsync),"\uE8D6"));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(StudioMenus.Async("移除角色和所属片段",()=>Safe(async()=>
            {
                string? id=selectedActor;if(id is null)return;
                if(!await window.ConfirmAsync("移除角色？","这个角色的动作、表情和声音片段也会移除。可使用撤销恢复。","移除"))return;
                await EditAsync(()=>{foreach(var c in document.Clips.OfType<JsonObject>().Where(c=>S(c,"actor")==id).ToArray())document.Clips.Remove(c);
                    var a=document.Actors.OfType<JsonObject>().FirstOrDefault(a=>S(a,"id")==id);if(a is not null)document.Actors.Remove(a);selectedActor=selectedClip=null;});
            }),"\uE74D"));
        }
        else menu.Items.Add(StudioMenus.Async("添加角色…",()=>Safe(AddActorAsync),"\uE77B"));return menu;
    }
    MenuFlyout ClipMenu(JsonObject clip)
    {
        string id=S(clip,"id");var menu=new MenuFlyout();
        TimelineRange ClipRange()=>RangeForClip(document.Clip(id));
        menu.Items.Add(StudioMenus.Async("导出这一段…",()=>Safe(()=>ExportAsync("clip",ClipRange())),"\uE898"));
        if(S(clip,"actor").Length>0)menu.Items.Add(StudioMenus.Async("在 Live2D 中打开角色区间…",()=>Safe(()=>OpenActorInLive2DAsync(ClipRange())),"\uE8A7"));
        menu.Items.Add(StudioMenus.Async("播放此处",()=>Safe(async()=>{if(await SeekAfterScrubAsync(N(document.Clip(id),"start")))viewport.Play();}),"\uE768"));
        menu.Items.Add(StudioMenus.Async("在播放头分割",()=>Safe(()=>EditAsync(()=>{var result=CreativeClipEditing.Split(document,id,playhead);document=result.Scene;selectedClip=result.Selection;})),"\uE8C6",CreativeClipEditing.CanSplit(clip,playhead),"Ctrl+K"));
        menu.Items.Add(StudioMenus.Async("复制片段",()=>Safe(()=>EditAsync(()=>selectedClip=document.DuplicateClip(id))),"\uE8C8"));
        if(S(clip,"kind") is "motion" or "expression")menu.Items.Add(StudioMenus.Async("替换素材…",()=>Safe(()=>ChooseReplacementAsync(id)),"\uE8D2"));
        menu.Items.Add(StudioMenus.Async(clip["enabled"]?.GetValue<bool>()==false?"启用":"停用",()=>Safe(()=>EditAsync(()=>document.Clip(id)["enabled"]=document.Clip(id)["enabled"]?.GetValue<bool>()==false))));
        menu.Items.Add(StudioMenus.Async("片段 JSON…",()=>Safe(()=>EditClipJsonAsync(id)),"\uE943"));menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Async("删除",()=>Safe(DeleteClipAsync),"\uE74D"));return menu;
    }
    TimelineRange RangeForClip(JsonObject clip)=>TimelineRange.ForClip(N(clip,"start"),
        Math.Max(1d/30,CreativeClipEditing.DisplayEnd(document,clip)-N(clip,"start")),30,document.Duration);
    internal async Task NewAsync(bool original=false)
    {
        if(original){await OpenStoryAsync();return;}if(!await ConfirmReplaceAsync())return;importCancellation?.Cancel();scrubber.Cancel();SaveDraft();viewport.InvalidateSource();previewDirty=true;loadedSourceKey=null;
        document=CreativeScene.Blank(window.Settings.Site,window.Settings.Language);timeline.ResetProjectView();ResetProjectLibrary();savedText=document.ToJson();selectedActor=selectedClip=replaceAssetClip=path=null;
        directory=Environment.CurrentDirectory;playhead=0;undo.Clear();redo.Clear();Refresh();viewport.SetEmptyCanvas(1920,1080);notice.IsOpen=false;
    }
    internal async Task OpenAsync(string? filename=null)
    {
        filename??=await window.PickFileAsync(extensions:[".json"]);if(filename is null)return;
        string text=await File.ReadAllTextAsync(filename);var node=JsonNode.Parse(text) as JsonObject??throw new ArgumentException("文件不是场景 JSON。");SaveDraft();
        if(S(node,"format")!=CreativeScene.Format){await ImportNativeAsync(text,Path.GetDirectoryName(Path.GetFullPath(filename))!);return;}
        var next=new CreativeScene(node);if(!await ConfirmReplaceAsync())return;importCancellation?.Cancel();scrubber.Cancel();viewport.InvalidateSource();document=next;timeline.ResetProjectView();ResetProjectLibrary();path=filename;directory=Path.GetDirectoryName(Path.GetFullPath(filename))!;savedText=next.ToJson();
        selectedActor=selectedClip=replaceAssetClip=null;playhead=0;undo.Clear();redo.Clear();previewDirty=true;loadedSourceKey=null;Refresh();await ReloadAsync();
    }
    async Task OpenStoryAsync()
    {
        var chosen=await ResourcePicker.PickAsync(window,new("adv","原剧情",CommandFieldKind.Text,""){LookupCommandId="story.list"},new Dictionary<string,string?>{["site"]=window.Settings.Site,["lang"]=window.Settings.Language});
        if(chosen is null)return;
        await ImportNativeAsync(SceneTemplates.Create("story-patch",new SceneSource(chosen.Value,window.Settings.Site,window.Settings.Language)).Text,Environment.CurrentDirectory);
    }
    bool importingStory;
    CancellationTokenSource? importCancellation;
    Task<NativePlaybackTiming>? importAnalysis;
    internal JsonObject? LastStoryImportReport { get; private set; }
    internal void CancelStoryImport()=>importCancellation?.Cancel();
    async Task ImportNativeAsync(string json,string baseDirectory)
    {
        if(importingStory)throw new InvalidOperationException("正在打开另一段原剧情，请稍候。");
        if(!Directory.Exists(window.Settings.ArchivePath))throw new DirectoryNotFoundException("请先选择已下载的素材库。");
        if(!await ConfirmReplaceAsync())return;
        if(importingStory)throw new InvalidOperationException("正在打开另一段原剧情，请稍候。");
        if(disposed)throw new OperationCanceledException();
        importingStory=true;
        LastStoryImportReport=null;
        using var cancellation=new CancellationTokenSource();
        importCancellation=cancellation;
        var total=System.Diagnostics.Stopwatch.StartNew();
        var stages=new JsonObject();
        var stage=System.Diagnostics.Stopwatch.StartNew();
        void Mark(string name){stages[name]=stage.Elapsed.TotalMilliseconds;stage.Restart();}
        string? importNotice=null;
        void ShowImportNotice(string message)
        {
            importNotice=message;notice.Severity=InfoBarSeverity.Informational;notice.Message=message;notice.IsOpen=true;
            cancelImport.Visibility=Visibility.Visible;cancelImport.IsEnabled=true;
        }
        void HideImportNotice()
        {
            if(!ReferenceEquals(importCancellation,cancellation))return;
            cancelImport.Visibility=Visibility.Collapsed;
            if(notice.Severity==InfoBarSeverity.Informational&&notice.Message==importNotice)notice.IsOpen=false;
        }
        SceneSnapshot? input=null;
        try
        {
            scrubber.Cancel();await scrubber.FlushAsync();cancellation.Token.ThrowIfCancellationRequested();
            if(disposed||!ReferenceEquals(importCancellation,cancellation))throw new OperationCanceledException();
            SaveDraft();viewport.Pause();
            var owner=document; long ownerVersion=loadVersion;
            string ownerText=owner.ToJson(), archiveSetting=window.Settings.ArchivePath,
                ffmpegSetting=window.Settings.FFmpegPath, coreSetting=window.Settings.NativeLibraryPath,
                sdkSetting=window.Settings.CubismSdkPath;
            bool SameSettings()=>window.Settings.ArchivePath==archiveSetting&&window.Settings.FFmpegPath==ffmpegSetting
                &&window.Settings.NativeLibraryPath==coreSetting&&window.Settings.CubismSdkPath==sdkSetting;
            void RequireOwner()
            { cancellation.Token.ThrowIfCancellationRequested();if(disposed||!ReferenceEquals(importCancellation,cancellation)
                ||!ReferenceEquals(document,owner)||loadVersion!=ownerVersion||owner.ToJson()!=ownerText||!SameSettings())
                throw new OperationCanceledException("原剧情导入的目标项目已更改。"); }
            ShowImportNotice("正在读取原剧情和素材索引…");
            string archivePath=window.Settings.ArchivePath;
            // Parsing, expansion and conversion all operate on private documents.
            // Neither file IO nor prefix-by-prefix JSON validation belongs on UI.
            var expanded=await Task.Run(()=>CreativeSourceImporter.Prepare(json,baseDirectory,new Archive(archivePath),cancellation.Token),cancellation.Token);
            RequireOwner();Mark("parseAndExpandMs");
            var native=expanded.Document;
            input=await Task.Run(()=>native.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory,"creative-preview")),cancellation.Token);
            RequireOwner();Mark("snapshotMs");
            ShowImportNotice("正在解析演出和真实语音时间…");
            var size=PreviewSize(native.Output.Width,native.Output.Height);
            var timingSource=Source(input.FilePath,size.Width,size.Height,36000,PreviewDurationMode.Automatic);
            // Timing does not need a first rendered preview which would immediately
            // be discarded when the editable project is installed. Keep the current
            // monitor intact and initialize only the final editable renderer.
            StoryTimingAnalysisDiagnostics? timingDiagnostics=null;
            importAnalysis=Task.Factory.StartNew(()=>StoryTimingAnalysis.Analyze(timingSource,cancellation.Token,diagnostics:value=>timingDiagnostics=value),
                CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            var timing=await importAnalysis;
            RequireOwner();Mark("nativeTimingMs");
            ShowImportNotice("正在建立可编辑时间轴…");
            var imported=await Task.Run(()=>expanded.CreateScene(timing,cancellation.Token),cancellation.Token);
            RequireOwner();Mark("convertTimelineMs");
            document=imported;timeline.ResetProjectView();ResetProjectLibrary();path=null;directory=baseDirectory;savedText="";
            selectedActor=selectedClip=replaceAssetClip=null;playhead=0;undo.Clear();redo.Clear();previewDirty=true;loadedSourceKey=null;
            Refresh();SaveDraft();Mark("bindTimelineMs");
            long previewVersion=loadVersion+1;string importedText=imported.ToJson();
            await ReloadAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if(disposed||!ReferenceEquals(importCancellation,cancellation)||!ReferenceEquals(document,imported)
                ||loadVersion!=previewVersion||imported.ToJson()!=importedText||!SameSettings())
                throw new OperationCanceledException("原剧情导入的目标项目已更改。");
            if(previewDirty||!viewport.HasCurrentSource)
                throw new InvalidOperationException(notice.Severity==InfoBarSeverity.Error?notice.Message:"原剧情的可编辑预览未能完成。");
            Mark("editablePreviewMs");HideImportNotice();
            LastStoryImportReport=new JsonObject { ["schema"]="ournotes-director/story-import/3",["totalMs"]=total.Elapsed.TotalMilliseconds,["intermediatePreviewCreated"]=false,
                ["commands"]=timing.Commands.Count,["clips"]=imported.Clips.Count,["stages"]=stages,["nativeAnalysis"]=timingDiagnostics?.ToJson() };
            App.TraceStartup("Story import: "+LastStoryImportReport.ToJsonString());
        }
        finally
        {
            input?.Dispose();HideImportNotice();
            if(ReferenceEquals(importCancellation,cancellation)){importingStory=false;importAnalysis=null;importCancellation=null;}
        }
    }
    static SceneDocument ParseNativeProject(string json,string baseDirectory)
    {
        var project=SceneDocument.Parse(json,baseDirectory:baseDirectory);var validation=project.Validate();
        if(validation.HasErrors)throw new ArgumentException(string.Join(Environment.NewLine,
            validation.Issues.Where(issue=>issue.Severity==SceneIssueSeverity.Error).Select(issue=>issue.Path+"："+issue.Message)));
        return project;
    }
    internal async Task SaveAsync(bool saveAs=false)
    {
        string? destination=path;if(saveAs||destination is null)destination=await window.PickFileAsync(true,path??"scene.json",[".json"]);if(destination is null)return;
        SaveTo(destination);
    }
    void SaveTo(string destination)
    {
        foreach(var clip in document.Clips.OfType<JsonObject>())if(clip["data"]?["AudioPath"] is JsonValue file)clip["data"]!["AudioPath"]=Path.GetFullPath(file.ToString(),directory);
        DesktopSettings.AtomicWrite(destination,document.ToJson());path=destination;directory=Path.GetDirectoryName(Path.GetFullPath(destination))!;savedText=document.ToJson();Refresh();
    }
    async Task AddActorAsync()
    {
        if(!Directory.Exists(window.Settings.ArchivePath)){window.ShowResourceManager();return;}
        var chosen=await ResourcePicker.PickAsync(window,new("modelId","角色",CommandFieldKind.Text,""){LookupCommandId="model.list"},new Dictionary<string,string?>{["site"]=document.Site});if(chosen is null)return;
        await AddActorResourceAsync(chosen.Value,chosen.RelatedValues.GetValueOrDefault("displayName")??chosen.Value);
    }
    async Task AddActorResourceAsync(string model,string name)
    {
        await EditAsync(()=>{selectedActor=document.AddActor(model,name);selectedClip=null;});
        viewport.SelectedTargetId=selectedActor;ShowProjectAssets();await LoadAssetsAsync();
    }
    async Task ImportVoiceAsync()
    {string? file=await window.PickFileAsync(extensions:[".wav",".ogg",".mp3",".m4a",".flac",".aac"]);if(file is not null)await InsertVoiceFileAsync(file,Path.GetFileNameWithoutExtension(file),playhead);}
    async Task InsertVoiceFileAsync(string file,string label,double at)
    {
        file=Path.GetFullPath(file);string ffmpeg=Ffmpeg();var cue=await Task.Run(()=>VoiceDecodeCache.Shared.Read(file,ffmpeg));var peaks=VoiceFile.Peaks(cue);
        double duration=cue.Samples/(double)AdvAudio.SampleRate;string actor=selectedActor??"";
        await EditAsync(()=>
        {
            selectedClip=document.AddClip(actor,"voice",at,duration,new JsonObject{["cmd"]="Voice",["AudioPath"]=file,["AudioIn"]=0,["AudioDuration"]=duration,["IgnoreLipSync"]=actor.Length==0},label);
            var clip=document.Clip(selectedClip);clip["sourceDuration"]=duration;clip["waveform"]=new JsonArray(peaks.Select(p=>(JsonNode)JsonValue.Create(p)!).ToArray());
        });
    }
    async Task ChooseReplacementAsync(string id)
    {
        var clip=document.Clip(id);replaceAssetClip=id;selectedActor=S(clip,"actor");selectedClip=id;
        assetKind.SelectedIndex=S(clip,"kind")=="motion"?0:1;ShowProjectAssets();await LoadAssetsAsync();assetHint.Text="选素材后按 Enter 替换；双击在 Live2D 中预览。";
    }
    async Task InsertAssetAsync(ResultRow row,string kind,double at)
    {
        if(kind=="voice")
        {
            var selection=row.Selection("voiceId");string origin=selection.RelatedValues.GetValueOrDefault("adv")??"";
            string key=Archive.Hash(System.Text.Encoding.UTF8.GetBytes(row.Data.ToJsonString()+origin+document.Site));string folder=Path.Combine(DesktopSettings.DataDirectory,"creative-voice-cache");Directory.CreateDirectory(folder);string file=Path.Combine(folder,key+".audio");
            if(!File.Exists(file)){var result=await window.QueryAsync("voice.extract",new Dictionary<string,string?>{["voiceId"]=selection.Value,["adv"]=origin,["site"]=document.Site,["out"]=file});if(!result.Success||!File.Exists(file))throw new IOException(result.Error??"语音提取失败。");}
            await InsertVoiceFileAsync(file,row.Title,at);return;
        }
        if(string.IsNullOrEmpty(selectedActor))throw new InvalidOperationException("先选择使用这个素材的角色。");string actor=selectedActor;
        double duration=row.Data is JsonObject d?N(d,"duration",kind=="expression"?1:3):3;
        string? replacement=replaceAssetClip;replaceAssetClip=null;
        await EditAsync(()=>
        {
            var target=document.Clips.OfType<JsonObject>().FirstOrDefault(c=>S(c,"id")==replacement&&S(c,"kind")==kind&&S(c,"actor")==actor);
            if(target is not null)
            {
                selectedClip=S(target,"id");
                CreativeMaterialEditing.Replace(document,selectedClip,kind,row.Id,row.Title);
            }
            else selectedClip=CreativeMaterialEditing.Insert(document,actor,kind,row.Id,row.Title,at,Math.Max(1d/30,duration));
        });
    }
    async Task DropAssetAsync(string json,double at,string? target=null)
    {
        var data=JsonNode.Parse(json)!.AsObject();selectedActor=string.IsNullOrWhiteSpace(target)?data["actor"]?.ToString():target;replaceAssetClip=null;
        var row=new ResultRow(S(data,"resource"),S(data,"label"),"",data["data"]!.DeepClone()){Context=new Dictionary<string,string?>{["adv"]=data["adv"]?.ToString(),["site"]=document.Site}};
        await InsertAssetAsync(row,S(data,"kind"),at);
    }
    async Task AddToTrackAsync(string actor,string kind,double at)
    {
        selectedActor=actor.Length==0?null:actor;selectedClip=replaceAssetClip=null;playhead=at;
        if(kind is "motion" or "expression" or "voice"){assetKind.SelectedIndex=kind=="motion"?0:kind=="expression"?1:2;ShowProjectAssets();RefreshActors();BuildInspector();await LoadAssetsAsync();return;}
        if(kind=="focus"){await FocusActorAsync(actor);return;}
        if(kind=="depth"){await EditAsync(()=>selectedClip=CreativeStaging.AddDepth(document,actor,at,-.5,.5));return;}
        if(kind=="transform"){var state=ActorTransform(actor);await AddTransformAsync(actor,state.X,state.Y,state.Height);return;}
        await EditAsync(()=>
        {
            var values=kind=="camera"?new JsonObject{["framing"]=new JsonObject{["x"]=0,["y"]=0,["zoom"]=1,["roll"]=0}}:new JsonObject{["grade"]=new JsonObject{["exposure"]=0,["contrast"]=1,["saturation"]=1,["vignette"]=0}};
            selectedClip=document.AddClip("",kind,at,1,new JsonObject{["cmd"]=kind=="camera"?"Director.Camera":"Director.Scene",["Values"]=values,["Duration"]=1},ClipTimeline.KindName(kind));
        });
    }
    static (int Width,int Height) PreviewSize(int width,int height){double scale=Math.Min(1,Math.Min(960d/width,720d/height));return(Math.Max(16,(int)Math.Round(width*scale)),Math.Max(16,(int)Math.Round(height*scale)));}
    static string? Empty(string text)=>string.IsNullOrWhiteSpace(text)?null:text;
    string Ffmpeg()=>NativeDependencies.MediaTool("ffmpeg",Empty(window.Settings.FFmpegPath));
    ScenePreviewSource Source(string file,int width,int height,double duration,PreviewDurationMode mode=PreviewDurationMode.Fixed)=>new(window.Settings.ArchivePath,file,NativeDependencies.CoreLibrary(Empty(window.Settings.NativeLibraryPath),Empty(window.Settings.CubismSdkPath)),Ffmpeg(),width,height,duration,mode);
    JsonObject Compiled()
    {
        var compiled=document.Compile();foreach(var row in compiled["commands"]!.AsArray().OfType<JsonObject>())if(row["AudioPath"] is JsonValue file)row["AudioPath"]=Path.GetFullPath(file.ToString(),directory);return compiled;
    }
    async Task ReloadAsync()=>await UpdatePreviewAsync(false);
    async Task ReloadAsync(CancellationToken cancellationToken)=>await UpdatePreviewAsync(false,cancellationToken);
    async Task CommitLiveAsync()=>await UpdatePreviewAsync(true);
    async Task UpdatePreviewAsync(bool preservePose,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(disposed)return;long version=++loadVersion;await loadGate.WaitAsync(cancellationToken);
        try
        {
            if(version!=loadVersion||disposed)return;
            if(document.Actors.Count==0&&document.Clips.Count==0&&document.Root["staging"] is null){viewport.SetEmptyCanvas((int)N(document.Canvas,"width"),(int)N(document.Canvas,"height"));previewDirty=false;loadedSourceKey=null;return;}
            if(!Directory.Exists(window.Settings.ArchivePath)){ShowError("请先选择素材库；当前场景已经保留。");return;}
            var owner=document;string ownerText=owner.ToJson();double duration=owner.Duration,restore=playhead;
            string archiveSetting=window.Settings.ArchivePath,ffmpegSetting=window.Settings.FFmpegPath,
                coreSetting=window.Settings.NativeLibraryPath,sdkSetting=window.Settings.CubismSdkPath;
            bool Current()=>!cancellationToken.IsCancellationRequested&&!disposed&&version==loadVersion&&ReferenceEquals(document,owner)&&owner.ToJson()==ownerText
                &&window.Settings.ArchivePath==archiveSetting&&window.Settings.FFmpegPath==ffmpegSetting
                &&window.Settings.NativeLibraryPath==coreSetting&&window.Settings.CubismSdkPath==sdkSetting;
            var parsed=SceneDocument.Parse(Compiled().ToJsonString(),baseDirectory:directory);var input=parsed.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory,"creative-preview"));
            string sourceKey=parsed.Root["source"]!.ToJsonString()+parsed.Root["output"]!.ToJsonString()+window.Settings.ArchivePath;
            bool installed=false;
            try
            {
                var size=PreviewSize(parsed.Output.Width,parsed.Output.Height);var source=Source(input.FilePath,size.Width,size.Height,duration+1d/30);
                bool same=viewport.HasCurrentSource&&loadedSourceKey==sourceKey;
                if(same)await viewport.CommitSceneProjectAsync(source);else await viewport.LoadSceneAsync(source);
                if(!Current())return;
                previewInput?.Dispose();previewInput=input;installed=true;loadedSourceKey=sourceKey;previewDirty=false;
                // Live placement has already updated the native pose. Mouse release must not
                // immediately replay history or re-upload all models and textures.
                double target=Math.Min(restore,duration);
                // A new source already renders and presents frame zero in LoadScene.
                // Nonzero restores and existing-source commits still need their seek.
                if(!(preservePose&&same)&&(same||target>0))await viewport.SeekAsync(target);
                if(!Current())return;
                viewport.SelectedTargetId=selectedActor;
            }
            finally{if(!installed)input.Dispose();}
        }
        finally{loadGate.Release();}
    }
    async Task SeekAsync(double seconds,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = document;
        viewport.Pause();
        if(previewDirty)await ReloadAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if(disposed || !ReferenceEquals(owner, document)) return;
        double target=Math.Clamp(seconds,0,document.Duration);
        playhead=target;timeline.Position=target;timeline.IsPlaying=false;
        if(viewport.HasCurrentSource)
        {
            var last = viewport.LastFrame;
            double quality = SceneInteractionPreview.Scale(timeline.IsScrubbing, last?.Device ?? "",
                last?.Performance?.SceneMilliseconds ?? 0, last?.ScenePreviewScale ?? 1);
            if (document.Root["nativeOutput"]?["isolateActor"] is not null
                || SceneContent.Read(document.Canvas["content"]) == "characters") quality = 1;
            await viewport.SetScenePreviewScaleAsync(quality, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if(disposed || !ReferenceEquals(owner, document)) return;
            await viewport.SeekAsync(target,cancellationToken);
        }
    }
    async Task<bool> SeekAfterScrubAsync(double seconds)
    {
        var owner=document;
        await scrubber.FlushAsync();
        if(disposed||!ReferenceEquals(owner,document))return false;
        await SeekAsync(seconds);
        return !disposed&&ReferenceEquals(owner,document);
    }
    async Task TogglePlayAsync()
    {
        var owner=document;
        await scrubber.FlushAsync();
        if(disposed||!ReferenceEquals(owner,document))return;
        if(viewport.IsPlaying){viewport.Pause();timeline.IsPlaying=false;return;}if(previewDirty||!viewport.HasCurrentSource)await ReloadAsync();if(!viewport.HasCurrentSource)return;
        if(disposed||!ReferenceEquals(owner,document))return;
        if(playhead<timeline.PlaybackStart||playhead>=timeline.PlaybackLastTime-1d/60)await SeekAsync(timeline.PlaybackStart);
        if(disposed||!ReferenceEquals(owner,document))return;
        viewport.Play(timeline.WorkRange?.End);timeline.IsPlaying=true;
    }
    async Task ExportAsync(string kind,TimelineRange? rangeOverride=null)
    {
        var owner=document;
        await scrubber.FlushAsync();
        if(disposed||!ReferenceEquals(owner,document))return;
        viewport.Pause();
        var native=SceneDocument.Parse(Compiled().ToJsonString(),baseDirectory:directory);
        // This read-only view supplies shared export settings without invoking the old
        // sparse-keyframe compiler. AtFrame rows must reach the renderer unchanged.
        var editor=StudioSceneDocument.CreateExportView(native,document.Duration+1d/30);
        await StudioExportDialog.ShowSceneAsync(window,editor,playhead,initialKind:kind,
            preserveTransparency:document.Root["imported"]?.GetValue<bool>()!=true&&document.Canvas["transparent"]?.GetValue<bool>()!=false, initialRange:rangeOverride??timeline.WorkRange);
    }
}
