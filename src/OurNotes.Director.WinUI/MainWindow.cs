using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace OurNotes.Director.WinUI;

public sealed class MainWindow : Window
{
    internal new static MainWindow? Current { get; private set; }
    internal DesktopSettings Settings { get; }
    internal TaskQueue Jobs { get; private set; }
    internal EngineRunner Engine => new(Settings.ResolveEngine());
    readonly Grid root = new();
    readonly ContentControl content = new() { HorizontalContentAlignment=HorizontalAlignment.Stretch, VerticalContentAlignment=VerticalAlignment.Stretch };
    readonly Dictionary<string,FrameworkElement> tools = new(StringComparer.Ordinal);
    readonly ToggleButton sceneTab = new() { Content="场景工作台", Padding=new(18,7,18,7) };
    readonly ToggleButton homeTab = new() { Content="主页工作台", Padding=new(18,7,18,7) };
    readonly TextBlock status = Ui.Text("准备就绪",12,true);
    readonly TextBlock archiveStatus = Ui.Text("",12,true);
    readonly ProgressBar progress = new() { Width=100,Height=4,Minimum=0,Maximum=1,Visibility=Visibility.Collapsed };
    readonly Border drawer = new() { Visibility=Visibility.Collapsed,HorizontalAlignment=HorizontalAlignment.Right,BorderThickness=new(1,0,0,0),Padding=new(16),BorderBrush=new SolidColorBrush(Color.FromArgb(55,128,128,128)) };
    readonly ContentControl drawerContent = new() { HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch };
    readonly TextBlock drawerTitle = new() { FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold };
    string? drawerKey;
    CreativeScenePage? scene;
    HomeStudioPage? home;
    bool closing,closeApproved,dialogOpen;
    internal string ActivePage { get; private set; }="scenes";
    internal CreativeScenePage Scene => scene ??= new(this);
    internal HomeStudioPage Home => home ??= new(this);
    internal XamlRoot RootXaml => root.XamlRoot;

    public MainWindow()
    {
        Current=this;Title="OurNotes Director";
        root.Style=(Style)Application.Current.Resources["WorkspaceRootStyle"];
        Settings=DesktopSettings.Load();Jobs=new TaskQueue(Engine);Jobs.Changed+=TaskChanged;
        try
        {
            var area=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Nearest);
            if(area?.WorkArea is {Width:>0,Height:>0} work)
            { int w=Math.Min(1520,work.Width),h=Math.Min(980,work.Height);AppWindow.MoveAndResize(new(work.X+(work.Width-w)/2,work.Y+(work.Height-h)/2,w,h)); }
            else AppWindow.Resize(new(1100,740));
        }
        catch(Exception error)when(error is COMException or InvalidOperationException or NotSupportedException or InvalidCastException)
        { App.TraceStartup("Placement: "+error.Message);AppWindow.Resize(new(1100,740)); }
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var header=new Grid{Padding=new(14,7,14,7),ColumnSpacing=16};
        header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});header.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.Children.Add(new TextBlock{Text="OURNOTES",FontSize=14,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        var tabs=Ui.Row(4,sceneTab,homeTab);tabs.HorizontalAlignment=HorizontalAlignment.Center;header.Children.Add(tabs);Grid.SetColumn(tabs,1);
        sceneTab.Click+=(_,_)=>Navigate("scenes");homeTab.Click+=(_,_)=>Navigate("home");
        var actions=Ui.Row(6,StudioLayout.Tool("素材库",ShowResourceManager,"\uE8B7"),StudioLayout.Tool("",()=>Navigate("tasks"),"\uE9F9"),StudioLayout.Tool("",()=>Navigate("settings"),"\uE713"));
        ToolTipService.SetToolTip(actions.Children[1],"导出任务");ToolTipService.SetToolTip(actions.Children[2],"设置");header.Children.Add(actions);Grid.SetColumn(actions,2);root.Children.Add(header);
        root.Children.Add(content);Grid.SetRow(content,1);
        var footer=new Grid{Padding=new(14,5,14,6),ColumnSpacing=10};footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});footer.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        footer.Children.Add(progress);footer.Children.Add(status);Grid.SetColumn(status,1);footer.Children.Add(archiveStatus);Grid.SetColumn(archiveStatus,2);root.Children.Add(footer);Grid.SetRow(footer,2);
        var panel=new Grid{RowSpacing=14};panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});
        var panelHeader=new Grid();panelHeader.Children.Add(drawerTitle);var close=StudioLayout.Tool("关闭",CloseDrawer,"\uE711");close.HorizontalAlignment=HorizontalAlignment.Right;panelHeader.Children.Add(close);
        panel.Children.Add(panelHeader);panel.Children.Add(drawerContent);Grid.SetRow(drawerContent,1);drawer.Child=panel;root.Children.Add(drawer);Grid.SetRow(drawer,1);
        root.SizeChanged+=(_,_)=>drawer.Width=Math.Min(820,Math.Max(320,root.ActualWidth-90));
        root.ActualThemeChanged+=(_,_)=>StyleDrawer();
        Content=root;ApplySettings();Navigate("scenes");
        AppWindow.Closing+=async(_,args)=>
        {
            if(closeApproved)return;args.Cancel=true;if(closing)return;closing=true;
            try
            {
                bool dirty=scene?.HasUnsavedChanges==true||home?.HasUnsavedChanges==true;
                if((Jobs.IsBusy||dirty)&&!await ConfirmAsync("关闭工作台？",Jobs.IsBusy?"还有导出任务。关闭会停止任务并保存场景草稿。":"未保存的修改会保留为草稿。","关闭"))return;
                scene?.SaveDraft();home?.SaveDraft();scene?.Deactivate();home?.Deactivate();
                foreach(var tool in tools.Values.OfType<CommandPage>())tool.SaveForm();
                SaveSettings();await Jobs.DisposeAsync();if(scene is not null)await scene.DisposeAsync();if(home is not null)await home.DisposeAsync();
                closeApproved=true;Close();
            }
            catch(Exception error){await ShowErrorAsync("关闭失败："+error.Message);}
            finally{closing=false;}
        };
    }
    void StyleDrawer()=>drawer.Background=new SolidColorBrush(root.ActualTheme==ElementTheme.Dark?Color.FromArgb(255,32,34,39):Color.FromArgb(255,248,248,250));
    internal void SetStatus(string text)=>status.Text=text;
    internal void SaveSettings(){try{Settings.Save();}catch(Exception e)when(e is IOException or UnauthorizedAccessException){status.Text="设置未保存："+e.Message;}}
    internal void ApplySettings()
    {
        root.RequestedTheme=Settings.Theme switch{"Light"=>ElementTheme.Light,"Dark"=>ElementTheme.Dark,_=>ElementTheme.Default};
        archiveStatus.Text=string.IsNullOrWhiteSpace(Settings.ArchivePath)?"未选择素材库":Path.GetFileName(Settings.ArchivePath.TrimEnd(Path.DirectorySeparatorChar));
    }
    internal async Task ReconnectEngineAsync()
    {
        if(Jobs.IsBusy)throw new InvalidOperationException("导出期间不能更换引擎。");Jobs.Changed-=TaskChanged;await Jobs.DisposeAsync();Jobs=new TaskQueue(Engine);Jobs.Changed+=TaskChanged;
    }
    internal void Navigate(string key)
    {
        if(key=="start")key="scenes";
        if(key is "scenes" or "home")
        {
            if(ActivePage!=key){scene?.Deactivate();home?.Deactivate();}
            ActivePage=key;CloseDrawer();content.Content=key=="scenes"?Scene:Home;sceneTab.IsChecked=key=="scenes";homeTab.IsChecked=key=="home";return;
        }
        var page=GetToolPage(key);if(page is SettingsPage settings)settings.Refresh();if(page is TasksPage tasks)tasks.Refresh();
        ShowDrawer(key,key switch{"tasks"=>"导出任务","settings"=>"设置","archive"=>"下载素材",_=>"高级素材工具"},page);
    }
    FrameworkElement GetToolPage(string key)
    {
        if(tools.TryGetValue(key,out var page))return page;
        page=key switch{"tasks"=>new TasksPage(this),"settings"=>new SettingsPage(this),"scene-tools"=>new ScenePage(this),
            "archive"=>new CommandPage(this,CommandGroup.Archive),"home-tools"=>new CommandPage(this,CommandGroup.Home),"stories"=>new CommandPage(this,CommandGroup.Stories),
            "models"=>new CommandPage(this,CommandGroup.Models),"assets"=>new CommandPage(this,CommandGroup.Assets),"media"=>new CommandPage(this,CommandGroup.Media),
            "diagnostics"=>new CommandPage(this,CommandGroup.Diagnostics),_=>throw new ArgumentException("未知工具："+key)};tools[key]=page;return page;
    }
    internal void ShowDrawer(string key,string title,FrameworkElement child)
    {drawerKey=key;drawerTitle.Text=title;drawerContent.Content=child;StyleDrawer();drawer.Width=Math.Min(820,Math.Max(320,root.ActualWidth-90));drawer.Visibility=Visibility.Visible;}
    internal void CloseDrawer(){drawer.Visibility=Visibility.Collapsed;drawerContent.Content=null;drawerKey=null;}
    internal void ShowResourceManager()
    {
        var path=Ui.Text(string.IsNullOrWhiteSpace(Settings.ArchivePath)?"选择已有素材，或下载需要的乐队。":Settings.ArchivePath,13,true);
        var panel=Ui.Stack(16,path,Ui.Row(8,StudioLayout.AsyncTool("选择素材文件夹",async()=>
        {if(await PickFolderAsync() is not {} folder)return;Settings.ArchivePath=folder;SaveSettings();ApplySettings();path.Text=folder;},"\uE8B7"),
            StudioLayout.Tool("下载 / 更新素材",()=>Navigate("archive"),"\uE896")),
            new Expander{Header="高级素材工具",HorizontalAlignment=HorizontalAlignment.Stretch,Content=Ui.Stack(8,
                StudioLayout.Tool("剧情与对白",()=>Navigate("stories")),StudioLayout.Tool("模型与动作",()=>Navigate("models")),
                StudioLayout.Tool("原生场景检查",()=>Navigate("scene-tools")),StudioLayout.Tool("主页批量处理",()=>Navigate("home-tools")),
                StudioLayout.Tool("媒体处理",()=>Navigate("media")),StudioLayout.Tool("诊断",()=>Navigate("diagnostics")))});
        ShowDrawer("resources","素材库",panel);
    }
    internal void OpenCommand(string id,IReadOnlyDictionary<string,string?>? values=null)
    {
        var definition=CommandCatalog.Get(id);string key=definition.Group switch{CommandGroup.Archive=>"archive",CommandGroup.Home=>"home-tools",CommandGroup.Stories=>"stories",CommandGroup.Models=>"models",CommandGroup.Assets=>"assets",CommandGroup.Media=>"media",CommandGroup.Scenes=>"scene-tools",_=>"diagnostics"};
        var page=GetToolPage(key);ShowDrawer(key,definition.Label,page);if(page is CommandPage command)command.SelectCommand(id,values);else if(page is ScenePage legacy)legacy.SelectOperation(id,values);
    }
    internal async Task<EngineResult> RunAsync(string id,IReadOnlyDictionary<string,string?> values,CommandContext? context=null)
    {
        var definition=CommandCatalog.Get(id);var build=CommandLineBuilder.Build(id,values,context??Settings.Context());
        if(!build.IsValid)throw new ArgumentException(string.Join(Environment.NewLine,build.Errors.Select(e=>e.Message)));
        var task=Jobs.Enqueue(new EngineRequest(id,definition.Label,build.Arguments,context?.WorkingDirectory??Environment.CurrentDirectory,build.OutputPath,build.OutputKind));status.Text=definition.Label+"已加入任务";
        var result=await task.Completion;if(id=="archive"&&result.Success&&result.OutputPath is {} archive){Settings.ArchivePath=archive;SaveSettings();ApplySettings();}return result;
    }
    internal async Task<EngineResult> QueryAsync(string id,IReadOnlyDictionary<string,string?>? values=null,CancellationToken cancellationToken=default)
    {
        var build=CommandLineBuilder.Build(id,values??new Dictionary<string,string?>(),Settings.Context());
        if(!build.IsValid)throw new ArgumentException(string.Join(Environment.NewLine,build.Errors.Select(e=>e.Message)));
        return await Engine.RunAsync(new EngineRequest(id,CommandCatalog.Get(id).Label,build.Arguments,Environment.CurrentDirectory),cancellationToken:cancellationToken);
    }
    void TaskChanged(DesktopTask task)=>DispatcherQueue.TryEnqueue(()=>
    {
        var running=Jobs.Tasks.FirstOrDefault(t=>t.Status==DesktopTaskStatus.Running);progress.Visibility=running is null?Visibility.Collapsed:Visibility.Visible;
        progress.IsIndeterminate=running?.Fraction is null;progress.Value=running?.Fraction??0;status.Text=running is null?task.Request.Title+" · "+task.StatusLabel:running.Request.Title+" · "+running.Message;
        if(drawerKey=="tasks"&&tools.TryGetValue("tasks",out var page))((TasksPage)page).RefreshProgress(task);
    });
    internal async Task<string?> PickFileAsync(bool save=false,string? suggestedName=null,IReadOnlyList<string>? extensions=null)
    {
        if(save)
        {
            var picker=new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id){SuggestedFileName=Path.GetFileName(suggestedName??"output")};
            if(extensions is {Count:>0}){picker.FileTypeChoices.Add("输出文件",extensions.ToList());string ext=Path.GetExtension(picker.SuggestedFileName);ext=extensions.Contains(ext,StringComparer.OrdinalIgnoreCase)?ext:extensions[0];if(ext.StartsWith('.')&&ext!=".*"){picker.DefaultFileExtension=ext;picker.SuggestedFileName=Path.GetFileNameWithoutExtension(picker.SuggestedFileName);}}
            return (await picker.PickSaveFileAsync())?.Path;
        }
        var open=new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);if(extensions is not null)foreach(var ext in extensions)open.FileTypeFilter.Add(ext);return(await open.PickSingleFileAsync())?.Path;
    }
    internal async Task<string?> PickFolderAsync()=> (await new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id).PickSingleFolderAsync())?.Path;
    internal Task ShowErrorAsync(string message)=>ShowMessageAsync("需要调整",message);
    internal async Task ShowMessageAsync(string title,string message)
    {
        if(dialogOpen){status.Text=message;return;}dialogOpen=true;
        try{await new ContentDialog{XamlRoot=root.XamlRoot,Title=title,Content=Ui.Text(message),CloseButtonText="知道了",RequestedTheme=root.ActualTheme}.ShowAsync();}finally{dialogOpen=false;}
    }
    internal async Task<bool> ConfirmAsync(string title,string description,string primary)
    {
        if(dialogOpen)return false;dialogOpen=true;
        try{return await new ContentDialog{XamlRoot=root.XamlRoot,Title=title,Content=Ui.Text(description),PrimaryButtonText=primary,CloseButtonText="返回",DefaultButton=ContentDialogButton.Close,RequestedTheme=root.ActualTheme}.ShowAsync()==ContentDialogResult.Primary;}finally{dialogOpen=false;}
    }
    internal static void Copy(string text){var data=new DataPackage();data.SetText(text);Clipboard.SetContent(data);Clipboard.Flush();}
    internal static void OpenPath(string path)
    {if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("文件不存在。",path);Process.Start(new ProcessStartInfo{FileName=Path.GetFullPath(path),UseShellExecute=true});}
    public async Task RunSmokeTestAsync(string reportPath)
    {
        var checks=new JsonArray();var pages=new JsonArray();
        void Save(bool ok=false,string? failed=null)=>DesktopSettings.AtomicWrite(reportPath,new JsonObject{["schema"]="ournotes-director/desktop-smoke/1",["ok"]=ok,["status"]=ok?"passed":failed is null?"running":"failed",["checks"]=checks.DeepClone(),["pages"]=pages.DeepClone(),["failedCheck"]=failed,["commands"]=CommandCatalog.All.Count}.ToJsonString(DesktopSettings.JsonOptions));
        void Check(bool condition,string name){if(condition)checks.Add(name);Save(failed:condition?null:name);if(!condition)throw new InvalidOperationException(name);}
        Save();foreach(var key in new[]{"scenes","home"}){Navigate(key);root.UpdateLayout();await Task.Delay(100);Check(content.Content is FrameworkElement{ActualWidth:>0,ActualHeight:>0},key+"工作台完成原生布局");pages.Add(key);}
        Navigate("scenes");root.UpdateLayout();await Task.Delay(100);await Scene.RunSmokeTestAsync(Check);
        Navigate("home");root.UpdateLayout();await Task.Delay(100);await Home.RunSmokeTestAsync(Check);
        if(Environment.GetEnvironmentVariable("OURNOTES_REAL_SCENE_FIXTURE") is {Length:>0}){Navigate("scenes");root.UpdateLayout();await Task.Delay(100);await Scene.RunRealSmokeTestAsync(Check);}
        var help=await Engine.RunAsync(new EngineRequest("help","引擎检查",["help"],Path.GetDirectoryName(reportPath)!));Check(help.Success&&help.Json?["commands"] is JsonArray,"原生导出执行器可用");
        CloseDrawer();root.UpdateLayout();Save(true);
    }
}
