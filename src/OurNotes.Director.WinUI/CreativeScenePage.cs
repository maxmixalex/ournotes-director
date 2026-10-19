using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage : UserControl, IAsyncDisposable
{
    readonly MainWindow window;
    readonly NativeViewport viewport=new();
    readonly ClipTimeline timeline=new();
    readonly TimelineScrubber scrubber;
    readonly ListView actors=new(){SelectionMode=ListViewSelectionMode.Single};
    readonly ListView assets=new(){SelectionMode=ListViewSelectionMode.Single,CanDragItems=true};
    readonly ComboBox assetKind=new(){HorizontalAlignment=HorizontalAlignment.Stretch};
    readonly TextBox search=new(){PlaceholderText="筛选动作或表情…"};
    readonly StackPanel inspector=new(){Spacing=8};
    readonly TextBlock title=new(){Text="未命名场景",FontSize=13,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
    readonly TextBlock assetHint=new(){Text="选择角色后添加动作、表情或声音。",FontSize=11,TextWrapping=TextWrapping.Wrap,Opacity=.7};
    readonly InfoBar notice=new(){IsOpen=false,IsClosable=true};
    readonly SemaphoreSlim loadGate=new(1);
    readonly Stack<string> undo=new(),redo=new();
    readonly List<ResultRow> assetRows=[];
    CreativeScene document;
    IDisposable? previewInput;
    string? selectedActor,selectedClip,path;
    string directory=Environment.CurrentDirectory;
    string savedText;
    double playhead;
    bool refreshing,disposed,playingLast;
    long loadVersion,assetVersion;
    CancellationTokenSource? assetRequest;
    bool previewDirty=true;
    string? dragActor;
    double dragX,dragY,dragHeight;
    readonly string draftPath=Path.Combine(DesktopSettings.DataDirectory,"creative-scene-draft.json");
    internal bool HasUnsavedChanges=>document.ToJson()!=savedText;
    static string S(JsonObject value,string key)=>CreativeScene.Text(value,key);
    static double N(JsonObject value,string key,double fallback=0)=>CreativeScene.Number(value,key,fallback);
    internal CreativeScenePage(MainWindow window)
    {
        this.window=window;scrubber=new((time,token)=>SeekAsync(time,token),error=>ShowError(error.Message));document=CreativeScene.Blank(window.Settings.Site,window.Settings.Language);savedText=document.ToJson();
        HorizontalContentAlignment=HorizontalAlignment.Stretch;VerticalContentAlignment=VerticalAlignment.Stretch;
        var toolbar=new Grid{ColumnSpacing=16};toolbar.ColumnDefinitions.Add(new(){Width=GridLength.Auto});toolbar.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});toolbar.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        toolbar.Children.Add(Ui.Row(4,StudioMenus.Button("文件",FileMenu),StudioMenus.Button("编辑",EditMenu),StudioMenus.Button("视图",ViewMenu)));
        toolbar.Children.Add(title);Grid.SetColumn(title,1);var publish=Ui.Row(8,Button("保存",()=>SaveAsync(),"\uE74E"),Button("导出…",()=>ExportAsync("clip"),"\uE898",true));toolbar.Children.Add(publish);Grid.SetColumn(publish,2);
        foreach(var(key,label)in new[]{("motion","动作"),("expression","表情"),("voice","语音台词")})assetKind.Items.Add(new ComboBoxItem{Content=label,Tag=key});
        assetKind.SelectedIndex=0;var library=BuildProjectLibrary();
        var center=new Grid();center.Children.Add(viewport);notice.VerticalAlignment=VerticalAlignment.Top;notice.Margin=new(12);center.Children.Add(notice);Content=new StudioLayout(library,center,Ui.Scroll(inspector),timeline,toolbar,workspaceKey:"scenes");
        actors.SelectionChanged+=(_,_)=>
        {if(refreshing)return;selectedActor=(actors.SelectedItem as ListViewItem)?.Tag as string;selectedClip=replaceAssetClip=null;timeline.Select(null);timeline.RevealActor(selectedActor);viewport.SelectedTargetId=selectedActor;BuildInspector();_=Safe(LoadAssetsAsync);};
        viewport.SelectionChanged+=(_,id)=>
        {if(refreshing||id==selectedActor)return;selectedActor=id;selectedClip=replaceAssetClip=null;timeline.Select(null);timeline.RevealActor(selectedActor);RefreshActors();BuildInspector();_=Safe(LoadAssetsAsync);};
        viewport.ObjectDragged+=(_,drag)=>_=Safe(()=>DragAsync(drag));viewport.ObjectScaled+=(_,scale)=>_=Safe(()=>ScaleAsync(scale));
        viewport.FramePresented+=(_,frame)=>
        {
            if(disposed||!viewport.HasCurrentSource)return;if(!scrubber.IsBusy){playhead=frame.Time;timeline.Position=playhead;}timeline.IsPlaying=frame.IsPlaying;
            bool ended=playingLast&&!frame.IsPlaying;playingLast=frame.IsPlaying;
            if(ended&&timeline.Repeat&&playhead>=timeline.PlaybackLastTime-.04)_=Safe(async()=>{await SeekAsync(timeline.PlaybackStart);viewport.Play(timeline.WorkRange?.End);});
        };
        viewport.TargetLabel=target=>
        {
            var actor=document.Actors.OfType<JsonObject>().FirstOrDefault(a=>S(a,"id")==target.Id);
            return actor is null?target.Label:DisplayNames.Character(CreativeScene.Text(actor,"name",target.Label));
        };
        viewport.ObjectWheelHint=target=>nativePlacement&&IsSourceActor(target.Id)
            ?"滚轮前后移动所选人物":"滚轮缩放所选人物";
        viewport.Failed+=(_,error)=>ShowError(error.Message);viewport.ContextMenuFactory=ActorMenu;
        timeline.PlayRequested+=()=>_=Safe(TogglePlayAsync);timeline.SeekRequested+=seconds=>{viewport.Pause();timeline.Position=seconds;scrubber.Request(seconds);};
        timeline.ClipSelected+=id=>
        {
            string? previousActor=selectedActor;selectedClip=id;selectedActor=S(document.Clip(id),"actor");replaceAssetClip=null;viewport.Pause();
            refreshing=true;try{viewport.SelectedTargetId=selectedActor;}finally{refreshing=false;}
            RefreshActors();BuildInspector();if(previousActor!=selectedActor)_=Safe(LoadAssetsAsync);
        };
        timeline.ClipEdited+=(id,seconds,resize)=>_=Safe(()=>EditAsync(()=>{if(resize)document.ResizeClip(id,seconds);else document.MoveClip(id,seconds);}));
        timeline.AddRequested+=(actor,kind,at)=>_=Safe(()=>AddToTrackAsync(actor,kind,at));timeline.ContextMenuFactory=id=>ClipMenu(document.Clip(id));timeline.AssetDropped+=(data,at,target)=>_=Safe(()=>DropAssetAsync(data,at,target));
        timeline.TrackMenuFactory=TrackMenu;
        timeline.ClipTrimmed+=(id,time)=>_=Safe(()=>EditAsync(()=>{var edit=CreativeClipEditing.TrimStart(document,id,time);document=edit.Scene;selectedClip=edit.Selection;}));
        assetKind.SelectionChanged+=(_,_)=>{search.PlaceholderText=AssetKind=="voice"?"输入台词后按 Enter 搜索…":"筛选名称…";ShowProjectAssets();_=Safe(LoadAssetsAsync);};
        search.TextChanged+=(_,_)=>{if(AssetKind!="voice")FilterAssets();};search.KeyDown+=(_,args)=>{if(args.Key==VirtualKey.Enter){_=Safe(LoadAssetsAsync);args.Handled=true;}};
        assets.DoubleTapped+=(_,_)=>_=Safe(PreviewSelectedAssetAsync);
        // ListViewItem consumes Enter during bubbling; handle the editor command first.
        assets.PreviewKeyDown+=(_,args)=>{if(args.Key==VirtualKey.Enter){args.Handled=true;_=Safe(InsertSelectedAssetAsync);}};
        actors.DoubleTapped+=(_,_)=>_=Safe(()=>OpenActorInLive2DAsync());
        assets.DragItemsStarting+=(_,args)=>
        {
            if(args.Items.FirstOrDefault() is not ListViewItem{Tag:ResultRow row}){args.Cancel=true;return;}
            var data=new JsonObject{["kind"]=AssetKind,["actor"]=selectedActor,["resource"]=row.Id,["label"]=row.Title,["data"]=row.Data.DeepClone(),["adv"]=row.Context.GetValueOrDefault("adv")};
            args.Data.SetData("OurNotes.SceneAsset",data.ToJsonString());args.Data.RequestedOperation=DataPackageOperation.Copy;
        };
        KeyDown+=(_,args)=>HandleWorkspaceKey(args);
        Loaded+=(_,_)=>{Refresh();if(document.Actors.Count==0&&document.Clips.Count==0&&document.Root["staging"] is null)viewport.SetEmptyCanvas((int)N(document.Canvas,"width"),(int)N(document.Canvas,"height"));else if(!viewport.HasCurrentSource)_=Safe(ReloadAsync);};
        Unloaded+=(_,_)=>Deactivate();Refresh();
    }
    internal void HandleWorkspaceKey(KeyRoutedEventArgs args)
    {
        if(args.Handled)return;bool ctrl=(Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;
        bool shift=(Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;
        if(ctrl&&args.Key==VirtualKey.S){_=Safe(()=>SaveAsync(shift));args.Handled=true;return;}
        if(ctrl&&args.Key==VirtualKey.O){_=Safe(()=>OpenAsync());args.Handled=true;return;}
        if(ctrl&&args.Key==VirtualKey.N){_=Safe(()=>NewAsync());args.Handled=true;return;}
        if(IsTextInput(args.OriginalSource as DependencyObject))return;
        if(ctrl&&args.Key==VirtualKey.Z){_=Safe(()=>UndoAsync(shift));args.Handled=true;}
        else if(ctrl&&args.Key==VirtualKey.Y){_=Safe(()=>UndoAsync(true));args.Handled=true;}
        else if(ctrl&&args.Key==VirtualKey.D&&selectedClip is {} id){_=Safe(()=>EditAsync(()=>selectedClip=document.DuplicateClip(id)));args.Handled=true;}
        else if(ctrl&&args.Key==VirtualKey.K&&selectedClip is {} split&&CreativeClipEditing.CanSplit(document.Clip(split),playhead))
        {_=Safe(()=>EditAsync(()=>{var result=CreativeClipEditing.Split(document,split,playhead);document=result.Scene;selectedClip=result.Selection;}));args.Handled=true;}
        else if(args.Key==VirtualKey.Delete&&selectedClip is not null){_=Safe(DeleteClipAsync);args.Handled=true;}
        else if(!ctrl&&args.Key==VirtualKey.Space){_=Safe(TogglePlayAsync);args.Handled=true;}
        else if(!ctrl&&args.Key==VirtualKey.I){timeline.MarkIn();args.Handled=true;}
        else if(!ctrl&&args.Key==VirtualKey.O){timeline.MarkOut();args.Handled=true;}
        else if(!ctrl&&args.Key==VirtualKey.F){viewport.FitCanvas();args.Handled=true;}
    }
    static bool IsTextInput(DependencyObject? item)
    {for(var current=item;current is not null;current=VisualTreeHelper.GetParent(current))if(current is TextBox or NumberBox or PasswordBox)return true;return false;}
    string AssetKind=>(assetKind.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"motion";
    Button Button(string text,Func<Task> action,string glyph,bool primary=false)=>StudioLayout.AsyncTool(text,()=>Safe(action),glyph,primary);
    async Task Safe(Func<Task> action){try{if(!disposed)await action();}catch(OperationCanceledException){}catch(Exception error){if(!disposed)ShowError(error.Message);}}
    void ShowError(string message){notice.Severity=InfoBarSeverity.Error;notice.Message=message;notice.IsOpen=true;}
    void ClearSelection()
    {
        selectedActor=selectedClip=replaceAssetClip=null;
        refreshing=true;try{viewport.SelectedTargetId=null;}finally{refreshing=false;}
        timeline.Select(null);RefreshActors();BuildInspector();_=Safe(LoadAssetsAsync);
    }
    void Refresh(){RefreshActors();timeline.Refresh(document,selectedClip);timeline.Position=playhead;BuildInspector();UpdateProjectContext();title.Text=(path is null?"未命名场景":Path.GetFileNameWithoutExtension(path))+(HasUnsavedChanges?" ●":"");}
    void RefreshActors()
    {
        refreshing=true;
        try
        {
            var existing=actors.Items.OfType<ListViewItem>().ToDictionary(item=>item.Tag!.ToString()!,StringComparer.Ordinal);
            var ids=document.Actors.OfType<JsonObject>().Select(a=>S(a,"id")).ToHashSet(StringComparer.Ordinal);
            foreach(var old in existing.Where(pair=>!ids.Contains(pair.Key)).ToArray())actors.Items.Remove(old.Value);
            foreach(var actor in document.Actors.OfType<JsonObject>())
            {
                string id=S(actor,"id"),name=DisplayNames.Character(CreativeScene.Text(actor,"name",id));
                if(!existing.TryGetValue(id,out var item))
                {
                    item=new ListViewItem{Tag=id,HorizontalContentAlignment=HorizontalAlignment.Stretch};
                    item.ContextRequested+=(_,args)=>{selectedActor=id;selectedClip=null;actors.SelectedItem=item;BuildInspector();ActorMenu().ShowAt(item);args.Handled=true;};actors.Items.Add(item);
                }
                if(item.Content is not TextBlock block||block.Text!=name)item.Content=new TextBlock{Text=name,TextTrimming=TextTrimming.CharacterEllipsis,IsTextSelectionEnabled=false};
            }
            actors.SelectedItem=string.IsNullOrEmpty(selectedActor)?null:actors.Items.OfType<ListViewItem>().FirstOrDefault(item=>item.Tag?.ToString()==selectedActor);
        }
        finally{refreshing=false;}
        UpdateProjectContext();
    }
    async Task EditAsync(Action action,bool reload=true)
    {
        viewport.Pause();string before=document.ToJson();string? previousSelection=selectedClip;
        try{action();document.Validate();}catch{document=CreativeScene.Parse(before);selectedClip=previousSelection;Refresh();throw;}
        if(document.ToJson()==before)return;undo.Push(before);redo.Clear();previewDirty=true;SaveDraft();Refresh();
        if(selectedClip is not null&&selectedClip!=previousSelection)timeline.RevealSelection();
        if(reload)await ReloadAsync();
    }
    async Task UndoAsync(bool forward)
    {
        var from=forward?redo:undo;var to=forward?undo:redo;if(from.Count==0)return;viewport.Pause();to.Push(document.ToJson());document=CreativeScene.Parse(from.Pop());selectedClip=null;previewDirty=true;Refresh();SaveDraft();await ReloadAsync();
    }
    async Task DeleteClipAsync(){if(selectedClip is {} id)await EditAsync(()=>{document.RemoveClip(id);selectedClip=null;});}
    async Task LoadAssetsAsync()
    {
        if(disposed||!IsLoaded)return;assetRequest?.Cancel();assetRequest?.Dispose();assetRequest=new();var token=assetRequest.Token;long version=++assetVersion;string kind=AssetKind;assetRows.Clear();FilterAssets();
        if(!Directory.Exists(window.Settings.ArchivePath)){assetHint.Text="先选择素材库，即可添加真实角色。";return;}
        var actor=document.Actors.OfType<JsonObject>().FirstOrDefault(a=>S(a,"id")==selectedActor);if(actor is null&&kind!="voice"){assetHint.Text="先选择一个角色。";return;}
        var request=new Dictionary<string,string?>{["site"]=document.Site,["limit"]="200"};
        if(kind=="voice"){if(search.Text.Trim().Length==0){assetHint.Text="输入台词搜索语音，或导入声音文件。";return;}request["query"]=search.Text.Trim();}else request["modelId"]=S(actor!,"model");
        string command=kind=="voice"?"voice.search":kind+".list";var allowed=CommandCatalog.Get(command).Fields.Select(field=>field.Key).ToHashSet(StringComparer.Ordinal);
        foreach(var key in request.Keys.Where(key=>!allowed.Contains(key)).ToArray())request.Remove(key);
        assetHint.Text="正在读取素材…";var result=await window.QueryAsync(command,request,token);if(version!=assetVersion||disposed)return;if(!result.Success)throw new IOException(result.Error??"读取素材失败。");
        assetRows.AddRange(ResultRow.ForPicker(result.Json,command,kind=="voice"?"voiceId":kind,request));FilterAssets();UpdateProjectContext();assetHint.Text=projectShowsActors?"双击角色在 Live2D 中打开":assetRows.Count==0?"没有匹配素材。":replaceAssetClip is not null?"Enter 替换；双击在 Live2D 中预览。":kind=="voice"?"Enter 插入；拖到轨道安排时间。":"双击预览；Enter 插入；拖到时间轴编排。";
    }
    void FilterAssets()
    {
        assets.Items.Clear();string query=AssetKind=="voice"?"":search.Text.Trim();
        foreach(var row in assetRows.Where(row=>row.SearchText.Contains(query,StringComparison.OrdinalIgnoreCase)))
        {var item=new ListViewItem{Tag=row,Content=new TextBlock{Text=row.Title,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=false},HorizontalContentAlignment=HorizontalAlignment.Stretch};ToolTipService.SetToolTip(item,row.Subtitle);assets.Items.Add(item);}
    }
    public void Deactivate(){importCancellation?.Cancel();scrubber.Cancel();viewport.Pause();SaveDraft();}
    public void SaveDraft()
    {
        if(disposed||!HasUnsavedChanges)return;
        try{Directory.CreateDirectory(DesktopSettings.DataDirectory);DesktopSettings.AtomicWrite(draftPath,document.ToJson());}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){ShowError("草稿未保存："+error.Message);}
    }
    public async ValueTask DisposeAsync()
    {if(disposed)return;importCancellation?.Cancel();scrubber.Cancel();SaveDraft();disposed=true;assetRequest?.Cancel();loadVersion++;
        // The import owns its snapshot until the dedicated native worker exits.
        // Its caller observes any analysis error; shutdown must still release the viewport.
        if(importAnalysis is {} analysis)try{await analysis;}catch(Exception){ }
        await loadGate.WaitAsync();try{await viewport.DisposeAsync();previewInput?.Dispose();}finally{loadGate.Release();assetRequest?.Dispose();}}
}
