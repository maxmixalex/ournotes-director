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
        menu.Items.Add(StudioMenus.Item("画布设置",()=>{selectedActor=selectedClip=null;BuildInspector();},"\uE740"));
        menu.Items.Add(StudioMenus.Async("场景 JSON…",()=>Safe(ShowJsonAsync),"\uE943"));
        menu.Items.Add(StudioMenus.Async("查看原生指令…",()=>Safe(ShowNativeAsync),"\uE8A5"));AddPlacementModeMenu(menu);return menu;
    }
    MenuFlyout ActorMenu()
    {
        var menu=new MenuFlyout();
        if(selectedActor is {Length:>0})
        {
            AddStageMenuItems(menu,selectedActor);
            menu.Items.Add(StudioMenus.Async("导出当前姿态图片…",()=>Safe(()=>ExportAsync("frame",true)),"\uEB9F"));
            menu.Items.Add(StudioMenus.Async("导出角色演出…",()=>Safe(()=>ExportAsync("clip",true)),"\uE714"));menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(StudioMenus.Item("选择动作",()=>{assetKind.SelectedIndex=0;_=Safe(LoadAssetsAsync);}));
            menu.Items.Add(StudioMenus.Item("选择表情",()=>{assetKind.SelectedIndex=1;_=Safe(LoadAssetsAsync);}));
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
        TimelineRange ClipRange()=>TimelineRange.ForClip(N(clip,"start"),N(clip,"duration"),30,document.Duration);
        menu.Items.Add(StudioMenus.Async("导出这一段…",()=>Safe(()=>ExportAsync("clip",false,ClipRange())),"\uE898"));
        if(S(clip,"actor").Length>0)menu.Items.Add(StudioMenus.Async("仅导出这个角色的这一段…",()=>Safe(()=>ExportAsync("clip",true,ClipRange())),"\uE77B"));
        menu.Items.Add(StudioMenus.Async("播放此处",()=>Safe(async()=>{await SeekAsync(N(document.Clip(id),"start"));viewport.Play();}),"\uE768"));
        menu.Items.Add(StudioMenus.Async("在播放头分割",()=>Safe(()=>EditAsync(()=>{var result=CreativeClipEditing.Split(document,id,playhead);document=result.Scene;selectedClip=result.Selection;})),"\uE8C6",CreativeClipEditing.CanSplit(clip,playhead)));
        menu.Items.Add(StudioMenus.Async("复制片段",()=>Safe(()=>EditAsync(()=>selectedClip=document.DuplicateClip(id))),"\uE8C8"));
        if(S(clip,"kind") is "motion" or "expression")menu.Items.Add(StudioMenus.Async("替换素材…",()=>Safe(()=>ChooseReplacementAsync(id)),"\uE8D2"));
        menu.Items.Add(StudioMenus.Async(clip["enabled"]?.GetValue<bool>()==false?"启用":"停用",()=>Safe(()=>EditAsync(()=>document.Clip(id)["enabled"]=document.Clip(id)["enabled"]?.GetValue<bool>()==false))));
        menu.Items.Add(StudioMenus.Async("片段 JSON…",()=>Safe(()=>EditClipJsonAsync(id)),"\uE943"));menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Async("删除",()=>Safe(DeleteClipAsync),"\uE74D"));return menu;
    }
    internal async Task NewAsync(bool original=false)
    {
        if(original){await OpenStoryAsync();return;}if(!await ConfirmReplaceAsync())return;scrubber.Cancel();SaveDraft();viewport.InvalidateSource();previewDirty=true;loadedSourceKey=null;
        document=CreativeScene.Blank(window.Settings.Site,window.Settings.Language);timeline.ClearRange();savedText=document.ToJson();selectedActor=selectedClip=replaceAssetClip=path=null;
        directory=Environment.CurrentDirectory;playhead=0;undo.Clear();redo.Clear();Refresh();viewport.SetEmptyCanvas(1920,1080);notice.IsOpen=false;
    }
    internal async Task OpenAsync(string? filename=null)
    {
        filename??=await window.PickFileAsync(extensions:[".json"]);if(filename is null)return;
        string text=await File.ReadAllTextAsync(filename);var node=JsonNode.Parse(text) as JsonObject??throw new ArgumentException("文件不是场景 JSON。");SaveDraft();
        if(S(node,"format")!=CreativeScene.Format){await ImportNativeAsync(text,Path.GetDirectoryName(Path.GetFullPath(filename))!);return;}
        var next=new CreativeScene(node);if(!await ConfirmReplaceAsync())return;scrubber.Cancel();document=next;path=filename;directory=Path.GetDirectoryName(Path.GetFullPath(filename))!;savedText=next.ToJson();
        selectedActor=selectedClip=replaceAssetClip=null;playhead=0;undo.Clear();redo.Clear();previewDirty=true;loadedSourceKey=null;Refresh();await ReloadAsync();
    }
    async Task OpenStoryAsync()
    {
        var chosen=await ResourcePicker.PickAsync(window,new("adv","原剧情",CommandFieldKind.Text,""){LookupCommandId="story.list"},new Dictionary<string,string?>{["site"]=window.Settings.Site,["lang"]=window.Settings.Language});
        if(chosen is null)return;
        await ImportNativeAsync(SceneTemplates.Create("story-patch",new SceneSource(chosen.Value,window.Settings.Site,window.Settings.Language)).Text,Environment.CurrentDirectory);
    }
    async Task ImportNativeAsync(string json,string baseDirectory)
    {
        if(!Directory.Exists(window.Settings.ArchivePath))throw new DirectoryNotFoundException("请先选择已下载的素材库。");
        if(!await ConfirmReplaceAsync())return;scrubber.Cancel();SaveDraft();viewport.Pause();notice.Severity=InfoBarSeverity.Informational;notice.Message="正在解析原剧情的演出和语音时间…";notice.IsOpen=true;
        var expanded=StudioSceneDocument.FromSceneDocument(SceneDocument.Parse(json,baseDirectory:baseDirectory));
        await Task.Run(()=>{var archive=new Archive(window.Settings.ArchivePath);expanded.ExpandInheritedSections(archive);expanded.ResolveSourceModels(archive);});
        var native=expanded.ToSceneDocument();var input=native.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory,"creative-preview"));
        try
        {
            var size=PreviewSize(native.Output.Width,native.Output.Height);await viewport.LoadSceneAsync(Source(input.FilePath,size.Width,size.Height,36000,PreviewDurationMode.Automatic));
            var timing=await viewport.ResolveSceneTimingAsync();var imported=CreativeScene.FromPerformance(native.Root,timing);
            foreach(var a in expanded.Actors)
                if(!string.IsNullOrWhiteSpace(a.Model)&&!imported.Actors.OfType<JsonObject>().Any(actor=>S(actor,"id")==a.Id))
                    imported.Actors.Add((JsonNode)new JsonObject{["id"]=a.Id,["name"]=a.Id,["model"]=a.Model,["fromSource"]=true});
            document=imported;path=null;directory=baseDirectory;savedText="";selectedActor=selectedClip=replaceAssetClip=null;playhead=0;undo.Clear();redo.Clear();previewDirty=true;loadedSourceKey=null;
            Refresh();SaveDraft();notice.IsOpen=false;
        }
        finally{input.Dispose();}
        await ReloadAsync();
    }
    internal async Task SaveAsync(bool saveAs=false)
    {
        string? destination=path;if(saveAs||destination is null)destination=await window.PickFileAsync(true,path??"scene.json",[".json"]);if(destination is null)return;
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
        viewport.SelectedTargetId=selectedActor;await LoadAssetsAsync();
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
        assetKind.SelectedIndex=S(clip,"kind")=="motion"?0:1;await LoadAssetsAsync();assetHint.Text="双击一个素材，直接替换所选片段；开始位置不变。";
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
            var data=new JsonObject{["cmd"]=kind=="expression"?"Expression":"Motion",["TargetName"]=actor,[kind=="expression"?"ExpressionName":"MotionName"]=row.Id,["MotionFadeIn"]=-1};
            var target=document.Clips.OfType<JsonObject>().FirstOrDefault(c=>S(c,"id")==replacement&&S(c,"kind")==kind&&S(c,"actor")==actor);
            if(target is not null){target["data"]=data;target["label"]=row.Title;target["sourcePerformance"]=false;target["duration"]=Math.Max(1d/30,Math.Round(duration*30)/30);selectedClip=S(target,"id");}
            else selectedClip=document.AddClip(actor,kind,at,Math.Max(1d/30,duration),data,row.Title);
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
        if(kind is "motion" or "expression" or "voice"){assetKind.SelectedIndex=kind=="motion"?0:kind=="expression"?1:2;RefreshActors();BuildInspector();await LoadAssetsAsync();return;}
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
    async Task CommitLiveAsync()=>await UpdatePreviewAsync(true);
    async Task UpdatePreviewAsync(bool preservePose)
    {
        if(disposed)return;long version=++loadVersion;await loadGate.WaitAsync();
        try
        {
            if(version!=loadVersion||disposed)return;
            if(document.Actors.Count==0&&document.Clips.Count==0&&document.Root["staging"] is null){viewport.SetEmptyCanvas((int)N(document.Canvas,"width"),(int)N(document.Canvas,"height"));previewDirty=false;loadedSourceKey=null;return;}
            if(!Directory.Exists(window.Settings.ArchivePath)){ShowError("请先选择素材库；当前场景已经保留。");return;}
            double restore=playhead;var parsed=SceneDocument.Parse(Compiled().ToJsonString(),baseDirectory:directory);var input=parsed.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory,"creative-preview"));
            string sourceKey=parsed.Root["source"]!.ToJsonString()+parsed.Root["output"]!.ToJsonString()+window.Settings.ArchivePath;
            try
            {
                var size=PreviewSize(parsed.Output.Width,parsed.Output.Height);var source=Source(input.FilePath,size.Width,size.Height,document.Duration+1d/30);
                bool same=viewport.HasCurrentSource&&loadedSourceKey==sourceKey;
                if(same)await viewport.CommitSceneProjectAsync(source);else await viewport.LoadSceneAsync(source);
                previewInput?.Dispose();previewInput=input;loadedSourceKey=sourceKey;previewDirty=false;
                // Live placement has already updated the native pose. Mouse release must not
                // immediately replay history or re-upload all models and textures.
                if(!(preservePose&&same))await viewport.SeekAsync(Math.Min(restore,document.Duration));
                viewport.SelectedTargetId=selectedActor;
            }
            catch{if(!ReferenceEquals(previewInput,input))input.Dispose();throw;}
        }
        finally{loadGate.Release();}
    }
    async Task SeekAsync(double seconds,CancellationToken cancellationToken=default)
    {
        viewport.Pause();if(previewDirty)await ReloadAsync();playhead=Math.Clamp(seconds,0,document.Duration);timeline.Position=playhead;timeline.IsPlaying=false;
        cancellationToken.ThrowIfCancellationRequested();
        if(viewport.HasCurrentSource)await viewport.SeekAsync(playhead,cancellationToken);
    }
    async Task TogglePlayAsync()
    {
        await scrubber.FlushAsync();
        if(viewport.IsPlaying){viewport.Pause();timeline.IsPlaying=false;return;}if(previewDirty||!viewport.HasCurrentSource)await ReloadAsync();if(!viewport.HasCurrentSource)return;
        if(playhead<timeline.PlaybackStart||playhead>=timeline.PlaybackLastTime-1d/60)await SeekAsync(timeline.PlaybackStart);viewport.Play(timeline.WorkRange?.End);timeline.IsPlaying=true;
    }
    async Task ExportAsync(string kind,bool actorOnly=false,TimelineRange? rangeOverride=null)
    {
        await scrubber.FlushAsync();
        viewport.Pause();
        var native=SceneDocument.Parse(Compiled().ToJsonString(),baseDirectory:directory);
        // This read-only view supplies shared export settings without invoking the old
        // sparse-keyframe compiler. AtFrame rows must reach the renderer unchanged.
        var editor=StudioSceneDocument.CreateExportView(native,document.Duration+1d/30);
        string? actor=actorOnly?selectedActor:null;JsonNode? metadata=null;
        if(actor is not null)
        {
            string? model=viewport.LastFrame?.ActorAnimations?.FirstOrDefault(a=>a.TargetId==actor)?.Model??document.Actors.OfType<JsonObject>().FirstOrDefault(a=>S(a,"id")==actor)?["model"]?.ToString();
            if(model is null)throw new InvalidOperationException("请先载入所选角色。");metadata=await Task.Run(()=>new Archive(window.Settings.ArchivePath).Model(document.Site,model));
        }
        await StudioExportDialog.ShowSceneAsync(window,editor,playhead,initialKind:kind,actorId:actor,actorMetadata:metadata,
            preserveTransparency:document.Root["imported"]?.GetValue<bool>()!=true&&document.Canvas["transparent"]?.GetValue<bool>()!=false, initialRange:rangeOverride??timeline.WorkRange);
    }
}
