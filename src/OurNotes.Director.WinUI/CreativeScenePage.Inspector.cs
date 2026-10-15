using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    long inspectorVersion;
    readonly CheckBox recordTransform=new(){Content="在播放头记录位置变化",IsChecked=true};
    bool IsSourceActor(string id)=>document.Actors.OfType<JsonObject>().FirstOrDefault(a=>S(a,"id")==id)?["fromSource"]?.GetValue<bool>()==true;
    void BuildInspector()
    {
        long version=++inspectorVersion;inspector.Children.Clear();var current=document;
        bool Current()=>!disposed&&version==inspectorVersion&&ReferenceEquals(current,document);
        void Heading(string text)=>inspector.Children.Add(new TextBlock{Text=text,FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,Margin=new(0,2,0,4)});
        void Help(string text)=>inspector.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=11,Opacity=.7});
        NumberBox Field(string label,double value,double min,double max,Func<double,Task> changed)
        {
            var input=new NumberBox{Header=label,Value=value,Minimum=min,Maximum=max,SmallChange=.1,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};double original=value;
            void Apply(){if(!Current()||!double.IsFinite(input.Value)||Math.Abs(input.Value-original)<.00001)return;double next=input.Value;original=next;_=Safe(()=>changed(next));}
            input.LostFocus+=(_,_)=>Apply();input.KeyDown+=(_,args)=>{if(args.Key==Windows.System.VirtualKey.Enter){Apply();args.Handled=true;}};
            inspector.Children.Add(input);return input;
        }
        var clip=document.Clips.OfType<JsonObject>().FirstOrDefault(c=>S(c,"id")==selectedClip);
        if(clip is not null)
        {
            string id=S(clip,"id"),kind=S(clip,"kind");var data=clip["data"]!.AsObject();Heading(ClipTimeline.KindName(kind));
            var label=new TextBox{Header="名称",Text=CreativeScene.Text(clip,"label",ClipTimeline.KindName(kind))};
            label.LostFocus+=(_,_)=>{if(Current()&&label.Text!=S(clip,"label"))_=Safe(()=>EditAsync(()=>document.Clip(id)["label"]=label.Text,false));};if(S(data,"cmd")!="Talk")inspector.Children.Add(label);
            var start=Field("开始（秒）",N(clip,"start"),0,36000,v=>EditAsync(()=>document.MoveClip(id,v)));
            if(document.CanResize(clip))
            {var duration=Field("持续（秒）",N(clip,"duration"),1d/30,36000,v=>EditAsync(()=>document.ResizeClip(id,v)));StudioLayout.PairFields(inspector,start,duration);}
            else Help($"持续区间 {N(clip,"duration"):0.###} 秒。表情保持到下一次切换，原有动作保留原剧情演出。");
            if(kind=="voice")
            {
                if(S(data,"cmd")=="Talk")
                {
                    string originalText=CreativeDialogue.Read(document,id);
                    var dialogue=new TextBox{Header="台词",Text=originalText,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=80,MaxHeight=180};
                    dialogue.LostFocus+=(_,_)=>{if(Current()&&dialogue.Text!=originalText)_=Safe(()=>EditAsync(()=>CreativeDialogue.Write(document,id,dialogue.Text)));};
                    inspector.Children.Add(dialogue);
                    Help("这里修改画面中的台词；原语音不变，不会自动生成配音。");
                }
                var binding=new ComboBox{Header="说话角色",HorizontalAlignment=HorizontalAlignment.Stretch};binding.Items.Add(new ComboBoxItem{Content="旁白",Tag=""});
                foreach(var a in document.Actors.OfType<JsonObject>())binding.Items.Add(new ComboBoxItem{Content=CreativeScene.Text(a,"name",S(a,"id")),Tag=S(a,"id")});
                binding.SelectedItem=binding.Items.OfType<ComboBoxItem>().FirstOrDefault(item=>item.Tag?.ToString()==S(clip,"actor"))??binding.Items[0];
                binding.SelectionChanged+=(_,_)=>{if(Current()){string actor=(binding.SelectedItem as ComboBoxItem)?.Tag?.ToString()??"";_=Safe(()=>EditAsync(()=>{var c=document.Clip(id);c["actor"]=actor;c["data"]!["IgnoreLipSync"]=actor.Length==0;}));}};
                inspector.Children.Add(binding);
                var lips=new CheckBox{Content="语音驱动口型",IsChecked=data["IgnoreLipSync"]?.GetValue<bool>()!=true&&S(clip,"actor").Length>0};
                lips.Click+=(_,_)=>{if(Current())_=Safe(()=>EditAsync(()=>document.Clip(id)["data"]!["IgnoreLipSync"]=lips.IsChecked!=true));};inspector.Children.Add(lips);
                if(data["AudioPath"] is not null)
                {Field("文件入点（秒）",N(data,"AudioIn"),0,N(clip,"sourceDuration",36000),v=>EditAsync(()=>document.Clip(id)["data"]!["AudioIn"]=v));Help(Path.GetFileName(data["AudioPath"]!.ToString()));}
                Help("预览音量只影响扬声器，不改变导出的声音和口型。");
            }
            else if(kind is "motion" or "expression")
            {
                Help("素材："+data[kind=="motion"?"MotionName":"ExpressionName"]);
                inspector.Children.Add(Button("替换素材…",()=>ChooseReplacementAsync(id),"\uE8D2"));
                Field("切换渐变（秒）",N(data,"MotionFadeIn"),0,10,v=>EditAsync(()=>document.Clip(id)["data"]!["MotionFadeIn"]=v));
                if(kind=="motion"&&clip["sourcePerformance"]?.GetValue<bool>()!=true)Help("片段结束时保持动作姿态；下一段动作开始时继续。声音和口型不受影响。");
            }
            else if(kind=="transform"&&data["Values"] is JsonObject values)
            {
                if(values["overlay"] is JsonObject overlay)
                {
                    var x=Field("水平偏移（像素）",N(overlay,"x")*N(document.Canvas,"width"),-100000,100000,v=>EditAsync(()=>document.Clip(id)["data"]!["Values"]!["overlay"]!["x"]=v/N(document.Canvas,"width")));
                    var y=Field("垂直偏移（像素）",N(overlay,"y")*N(document.Canvas,"height"),-100000,100000,v=>EditAsync(()=>document.Clip(id)["data"]!["Values"]!["overlay"]!["y"]=v/N(document.Canvas,"height")));StudioLayout.PairFields(inspector,x,y);
                    Field("缩放（百分比）",N(overlay,"scale",1)*100,1,2000,v=>EditAsync(()=>document.Clip(id)["data"]!["Values"]!["overlay"]!["scale"]=v/100));
                }
                else if(values["position"] is JsonArray p&&p.Count==2)
                {
                    double width=N(document.Canvas,"width"),height=N(document.Canvas,"height");
                    var x=Field("水平位置（像素）",double.Parse(p[0]!.ToString(),CultureInfo.InvariantCulture)*width,-100000,100000,v=>EditAsync(()=>document.Clip(id)["data"]!["Values"]!["position"]![0]=v/width));
                    var y=Field("垂直位置（像素）",double.Parse(p[1]!.ToString(),CultureInfo.InvariantCulture)*height,-100000,100000,v=>EditAsync(()=>document.Clip(id)["data"]!["Values"]!["position"]![1]=v/height));StudioLayout.PairFields(inspector,x,y);
                    Field("角色高度（百分比）",N(values,"height",.95)*100,1,2000,v=>EditAsync(()=>document.Clip(id)["data"]!["Values"]!["height"]=v/100));
                }
            }
            else if(kind is "camera" or "grade"&&data["Values"] is JsonObject visual)
            {
                string group=kind=="camera"?"framing":"grade";
                if(visual[group] is JsonObject settings)
                {
                    var fields=kind=="camera"?new[]{("x","水平位移",0d),("y","垂直位移",0d),("zoom","缩放",1d),("roll","旋转（度）",0d)}:new[]{("exposure","曝光",0d),("contrast","对比度",1d),("saturation","饱和度",1d),("vignette","暗角",0d)};
                    foreach(var(key,name,fallback)in fields)Field(name,N(settings,key,fallback),key=="zoom"?.01:key is "contrast" or "saturation" or "vignette"?0:-100,key=="vignette"?1:100,v=>EditAsync(()=>document.Clip(id)["data"]!["Values"]![group]![key]=v));
                }
            }
            if(S(data,"cmd").StartsWith("Director.",StringComparison.Ordinal))
            {
                var easing=new ComboBox{Header="变化方式",HorizontalAlignment=HorizontalAlignment.Stretch};
                foreach(var(key,name)in new[]{("Linear","匀速"),("InOutQuad","柔和进出")})easing.Items.Add(new ComboBoxItem{Tag=key,Content=name});
                easing.SelectedItem=easing.Items.OfType<ComboBoxItem>().FirstOrDefault(item=>item.Tag?.ToString()==data["Ease"]?.ToString())??easing.Items[0];
                easing.SelectionChanged+=(_,_)=>{if(Current())_=Safe(()=>EditAsync(()=>document.Clip(id)["data"]!["Ease"]=(easing.SelectedItem as ComboBoxItem)!.Tag!.ToString()));};inspector.Children.Add(easing);
            }
            inspector.Children.Add(new Expander{Header="高级",HorizontalAlignment=HorizontalAlignment.Stretch,Content=Button("编辑片段 JSON…",()=>EditClipJsonAsync(id),"\uE943")});return;
        }
        var actorInfo=document.Actors.OfType<JsonObject>().FirstOrDefault(a=>S(a,"id")==selectedActor);
        if(actorInfo is not null)
        {
            string id=S(actorInfo,"id");bool original=IsSourceActor(id);Heading(CreativeScene.Text(actorInfo,"name",id));var pose=ActorTransform(id);double w=N(document.Canvas,"width"),h=N(document.Canvas,"height");
            var x=Field(original?"水平偏移（像素）":"水平位置（像素）",pose.X*w,-100000,100000,v=>AddTransformAsync(id,v/w,pose.Y,pose.Height));
            var y=Field(original?"垂直偏移（像素）":"垂直位置（像素）",pose.Y*h,-100000,100000,v=>AddTransformAsync(id,pose.X,v/h,pose.Height));StudioLayout.PairFields(inspector,x,y);
            Field(original?"缩放（百分比）":"角色高度（百分比）",pose.Height*100,1,2000,v=>AddTransformAsync(id,pose.X,pose.Y,v/100));inspector.Children.Add(recordTransform);
            Help(original?"偏移叠加在原有演出之上，不重置原剧情位置、动作或镜头。":"直接拖动角色摆放。开启记录后，在播放头创建位置片段。");
            inspector.Children.Add(Ui.Row(6,Button("当前姿态图片",()=>ExportAsync("frame",true),"\uEB9F"),Button("角色动画",()=>ExportAsync("clip",true),"\uE714")));
            return;
        }
        Heading("场景");Help("添加角色，把动作、表情或语音拖到时间轴。场景长度随片段延伸。");
        var cw=Field("画布宽度",N(document.Canvas,"width",1920),16,32768,v=>EditAsync(()=>document.Canvas["width"]=(int)Math.Round(v)));
        var ch=Field("画布高度",N(document.Canvas,"height",1080),16,32768,v=>EditAsync(()=>document.Canvas["height"]=(int)Math.Round(v)));StudioLayout.PairFields(inspector,cw,ch);
        var presets=new ComboBox{PlaceholderText="常用画布",HorizontalAlignment=HorizontalAlignment.Stretch};
        foreach(var(name,w,h)in new[]{("横屏 1920 × 1080",1920,1080),("竖屏 1080 × 1920",1080,1920),("正方形 1080 × 1080",1080,1080)})presets.Items.Add(new ComboBoxItem{Content=name,Tag=(w,h)});
        presets.SelectionChanged+=(_,_)=>{if(Current()&&(presets.SelectedItem as ComboBoxItem)?.Tag is ValueTuple<int,int> size)_=Safe(()=>EditAsync(()=>{document.Canvas["width"]=size.Item1;document.Canvas["height"]=size.Item2;}));};inspector.Children.Add(presets);
        var content=new ComboBox{Header="画面内容",HorizontalAlignment=HorizontalAlignment.Stretch};
        content.Items.Add(new ComboBoxItem{Content="完整场景",Tag="scene"});
        content.Items.Add(new ComboBoxItem{Content="仅人物 · 透明背景",Tag="characters"});
        content.SelectedIndex=SceneContent.Read(document.Canvas["content"])=="characters"?1:0;
        content.SelectionChanged+=(_,_)=>{if(Current())_=Safe(()=>EditAsync(()=>document.Canvas["content"]=(content.SelectedItem as ComboBoxItem)!.Tag!.ToString()));};
        inspector.Children.Add(content);
        if(document.Root["imported"]?.GetValue<bool>()!=true&&content.SelectedIndex==0)
        {
            var transparent=new CheckBox{Content="画布底色透明",IsChecked=document.Canvas["transparent"]?.GetValue<bool>()!=false};
            transparent.Click+=(_,_)=>{if(Current())_=Safe(()=>EditAsync(()=>document.Canvas["transparent"]=transparent.IsChecked==true));};inspector.Children.Add(transparent);
        }
        else Help(content.SelectedIndex==1?"保留人物原站位、相机、动作和语音；背景、台词和装饰不进入预览或导出。":"背景和台词沿用原剧情；选择“仅人物”可生成同构图的透明画面。");
        inspector.Children.Add(Button("添加镜头变化",()=>AddToTrackAsync("","camera",playhead),"\uE722"));inspector.Children.Add(Button("添加画面调整",()=>AddToTrackAsync("","grade",playhead),"\uE790"));
        inspector.Children.Add(Button("导出当前画面",()=>ExportAsync("frame"),"\uEB9F"));
    }
    static double Blend(JsonObject data,double time,double start)
    {
        double duration=N(data,"Duration"),t=duration<=0?1:Math.Clamp((time-start)/duration,0,1);
        return S(data,"Ease")=="InOutQuad"?(t<.5?2*t*t:1-Math.Pow(-2*t+2,2)/2):t;
    }
    (double X,double Y,double Height) ActorTransform(string id)
    {
        var a=document.Actors.OfType<JsonObject>().FirstOrDefault(a=>S(a,"id")==id);bool original=IsSourceActor(id);
        double x=original?0:a is null?.5:N(a,"x",.5),y=original?0:a is null?.5:N(a,"y",.5),height=original?1:a is null?.95:N(a,"height",.95);
        foreach(var c in document.Clips.OfType<JsonObject>().Where(c=>S(c,"actor")==id&&S(c,"kind")=="transform"&&c["enabled"]?.GetValue<bool>()!=false&&N(c,"start")<=playhead).OrderBy(c=>N(c,"start")))
        {
            if(c["data"] is not JsonObject data||data["Values"] is not JsonObject v)continue;double t=Blend(data,playhead,N(c,"start"));
            if(original&&v["overlay"] is JsonObject overlay){x+=(N(overlay,"x",x)-x)*t;y+=(N(overlay,"y",y)-y)*t;height+=(N(overlay,"scale",height)-height)*t;}
            else if(!original&&v["position"] is JsonArray p&&p.Count==2){x+=(double.Parse(p[0]!.ToString(),CultureInfo.InvariantCulture)-x)*t;y+=(double.Parse(p[1]!.ToString(),CultureInfo.InvariantCulture)-y)*t;height+=(N(v,"height",height)-height)*t;}
        }
        return(x,y,height);
    }
    JsonObject TransformRow(string id,double x,double y,double height)=>IsSourceActor(id)?new(){["cmd"]="Director.Actor",["TargetName"]=id,["Values"]=new JsonObject{["overlay"]=new JsonObject{["x"]=x,["y"]=y,["scale"]=height}}}:new(){["cmd"]="Director.Actor",["TargetName"]=id,["Space"]="screen",["Values"]=new JsonObject{["position"]=new JsonArray(x,y),["height"]=height}};
    async Task AddTransformAsync(string id,double x,double y,double height)
    {
        var row=TransformRow(id,x,y,height);if(viewport.HasCurrentSource)await viewport.ApplySceneEditsAsync([row]);
        await EditAsync(()=>
        {
            var a=document.Actors.OfType<JsonObject>().FirstOrDefault(a=>S(a,"id")==id);
            if(a is not null&&!IsSourceActor(id)&&(playhead<1d/30||recordTransform.IsChecked!=true)){a["x"]=x;a["y"]=y;a["height"]=height;return;}
            double at=recordTransform.IsChecked==true?playhead:0;
            var existing=document.Clips.OfType<JsonObject>().LastOrDefault(c=>S(c,"actor")==id&&S(c,"kind")=="transform"&&c["sourcePerformance"]?.GetValue<bool>()!=true&&Math.Abs(N(c,"start")-at)<.5/30);
            if(existing is not null){existing["data"]=row;selectedClip=S(existing,"id");}else selectedClip=document.AddClip(id,"transform",at,1d/30,row,"位置");
        },false);
        await CommitLiveAsync();
    }
    (double X,double Y) DragDelta(string id,double dx,double dy)
    {
        var output=document.Root["nativeOutput"] as JsonObject;double w=viewport.LastFrame?.Pixels.Width??N(document.Canvas,"width"),h=viewport.LastFrame?.Pixels.Height??N(document.Canvas,"height");
        double active=output is null||S(output,"letterbox")=="none"?h:Math.Min(h,Math.Floor(w/(w>h?2.1666667:1.7777778)+.5));dy*=h/active;
        if(IsSourceActor(id))return(dx,dy);
        double zoom=1,roll=0;
        foreach(var c in document.Clips.OfType<JsonObject>().Where(c=>S(c,"kind")=="camera"&&N(c,"start")<=playhead&&c["enabled"]?.GetValue<bool>()!=false).OrderBy(c=>N(c,"start")))
            if(c["data"] is JsonObject data&&data["Values"]?["framing"] is JsonObject f){double t=Blend(data,playhead,N(c,"start"));zoom+=(N(f,"zoom",zoom)-zoom)*t;roll+=(N(f,"roll",roll)-roll)*t;}
        double radians=roll*Math.PI/180,aspect=w/active;
        return((dx*aspect*Math.Cos(radians)-dy*Math.Sin(radians))/(zoom*aspect),(dx*aspect*Math.Sin(radians)+dy*Math.Cos(radians))/zoom);
    }
    async Task DragAsync(PreviewDrag drag)
    {
        if(dragActor!=drag.TargetId){dragActor=drag.TargetId;var pose=ActorTransform(drag.TargetId);dragX=pose.X;dragY=pose.Y;dragHeight=pose.Height;}
        var d=DragDelta(drag.TargetId,drag.DeltaX,drag.DeltaY);double x=dragX+d.X,y=dragY+d.Y;
        if(drag.IsCompleted){dragActor=null;await AddTransformAsync(drag.TargetId,x,y,dragHeight);}
        else if(viewport.HasCurrentSource)await viewport.ApplySceneEditsAsync([TransformRow(drag.TargetId,x,y,dragHeight)]);
    }
    async Task ScaleAsync(PreviewScale scale){var p=ActorTransform(scale.TargetId);await AddTransformAsync(scale.TargetId,p.X,p.Y,Math.Clamp(p.Height*scale.Factor,.01,20));}
    async Task<string?> EditJsonDialogAsync(string heading,string text,Action<string> validate)
    {
        var input=new TextBox{Text=text,AcceptsReturn=true,FontFamily=new FontFamily("Consolas"),FontSize=12,MinWidth=460,Height=400,TextWrapping=TextWrapping.NoWrap};
        ScrollViewer.SetVerticalScrollBarVisibility(input,ScrollBarVisibility.Auto);ScrollViewer.SetHorizontalScrollBarVisibility(input,ScrollBarVisibility.Auto);var error=new InfoBar{IsOpen=false,Severity=InfoBarSeverity.Error};
        var dialog=new ContentDialog{XamlRoot=window.RootXaml,Title=heading,PrimaryButtonText="应用",CloseButtonText="取消",Content=Ui.Stack(8,input,error)};
        dialog.PrimaryButtonClick+=(_,args)=>{try{validate(input.Text);}catch(Exception failure){error.Message=failure.Message;error.IsOpen=true;args.Cancel=true;}};
        return await dialog.ShowAsync()==ContentDialogResult.Primary?input.Text:null;
    }
    async Task ShowJsonAsync(){var result=await EditJsonDialogAsync("场景 JSON",document.ToJson(),text=>CreativeScene.Parse(text));if(result is not null)await EditAsync(()=>{document=CreativeScene.Parse(result);selectedActor=selectedClip=null;});}
    async Task PasteAsync()
    {
        var clipboard=Clipboard.GetContent();if(!clipboard.Contains(StandardDataFormats.Text))throw new ArgumentException("剪贴板没有 JSON。");string text=CreativeScene.UnwrapJson(await clipboard.GetTextAsync());
        bool creative=JsonNode.Parse(text)?["format"]?.ToString()==CreativeScene.Format;
        var result=await EditJsonDialogAsync(creative?"粘贴场景":"导入原生项目",text,value=>{if(creative)CreativeScene.Parse(value);else SceneDocument.Parse(value,baseDirectory:directory);});
        if(result is null)return;if(creative)await EditAsync(()=>{document=CreativeScene.Parse(result);selectedActor=selectedClip=null;});else await ImportNativeAsync(result,directory);
    }
    async Task EditClipJsonAsync(string id)
    {
        var result=await EditJsonDialogAsync("片段高级参数",document.Clip(id)["data"]!.ToJsonString(DesktopSettings.JsonOptions),text=>{var copy=CreativeScene.Parse(document.ToJson());copy.Clip(id)["data"]=JsonNode.Parse(text)!.AsObject();copy.Validate();});
        if(result is not null)await EditAsync(()=>document.Clip(id)["data"]=JsonNode.Parse(result)!.AsObject());
    }
    async Task ShowNativeAsync()
    {
        var input=new TextBox{Text=Compiled().ToJsonString(DesktopSettings.JsonOptions),AcceptsReturn=true,IsReadOnly=true,Height=420,MinWidth=460,FontFamily=new FontFamily("Consolas"),FontSize=11,TextWrapping=TextWrapping.NoWrap};ScrollViewer.SetVerticalScrollBarVisibility(input,ScrollBarVisibility.Auto);
        await new ContentDialog{XamlRoot=window.RootXaml,Title="原生指令（由片段编译）",Content=input,CloseButtonText="关闭"}.ShowAsync();
    }
    internal Task SetSourceAsync(string adv,string? site=null)=>ImportNativeAsync(SceneTemplates.Create("story-patch",new SceneSource(adv,site??window.Settings.Site,window.Settings.Language)).Text,Environment.CurrentDirectory);
    internal Task OpenPathAsync(string filename)=>OpenAsync(filename);
    internal Task RunSmokeTestAsync(Action<bool,string> check)
    {
        check(document.Source["adv"] is null&&viewport.LastFrame is null,"空白画布不需要原剧情");var sample=CreativeScene.Blank("intl","zh-Hans");
        string id=sample.AddClip("","grade",1,1,new JsonObject{["cmd"]="Director.Scene",["Values"]=new JsonObject{["grade"]=new JsonObject{["exposure"]=0}},["Duration"]=1},"画面变化");sample.MoveClip(id,2);
        check(sample.Duration==3&&sample.Compile()["commands"]!.AsArray().OfType<JsonObject>().Any(r=>r["AtFrame"]?.ToString()=="60"),"拖动片段改变编译时序");
        check(timeline.ActualWidth>0&&actors.IsLoaded&&inspector.IsLoaded,"真实窗口显示片段时间轴和素材栏");return Task.CompletedTask;
    }
    internal async Task RunRealSmokeTestAsync(Action<bool,string> check)
    {
        string? fixture=Environment.GetEnvironmentVariable("OURNOTES_REAL_SCENE_FIXTURE");if(string.IsNullOrEmpty(fixture))return;
        window.Settings.ArchivePath=fixture;await ImportNativeAsync(SceneTemplates.Create("story-patch",new SceneSource("10462","intl","zh-Hans")).Text,fixture);await SeekAsync(3.1);
        check(document.Clips.OfType<JsonObject>().Any(c=>S(c,"kind")=="voice"),"原剧情对白成为可见语音片段");check(viewport.LastFrame is {Frame:93},"新工作台可定位真实剧情第93帧");
        var full=viewport.LastFrame!;var targets=full.Targets.ToArray();var lips=full.LipSync?.ToArray()??[];
        var dialogue=document.Clips.OfType<JsonObject>().First(c=>S(c,"kind")=="voice"&&N(c,"start")<=3.1&&N(c,"start")+N(c,"duration")>=3.1);
        timeline.Select(S(dialogue,"id"),true);timeline.FilterActor(S(dialogue,"actor"));timeline.FitSelection();
        string unchanged=document.ToJson();
        timeline.Position=3.1;timeline.MarkIn();timeline.Position=4.1;timeline.MarkOut();timeline.Position=playhead;
        check(timeline.WorkRange is {StartFrame:93,EndFrameExclusive:124,FrameCount:31}&&document.ToJson()==unchanged,
            "在时间轴标记短镜头区间不裁掉源指令；入出点包括目标31帧");
        await App.CaptureCheckpointAsync("shot-range");
        await App.CaptureCheckpointAsync("dialogue-editor");
        await EditAsync(()=>document.Canvas["content"]="characters");
        var cutout=viewport.LastFrame!;
        check(cutout.Frame==93&&cutout.Targets.SequenceEqual(targets),"仅人物画面保留同帧的原相机和角色站位");
        check(cutout.Pixels.Rgba.Length>0&&cutout.Pixels.Rgba.Where((_,i)=>i%4==3).Any(alpha=>alpha==0)
            &&cutout.Pixels.Rgba.Where((_,i)=>i%4==3).Any(alpha=>alpha>0),"仅人物输出有真实透明区域且角色仍存在");
        check((cutout.LipSync??[]).Select(v=>(v.TargetId,v.MouthOpen)).SequenceEqual(lips.Select(v=>(v.TargetId,v.MouthOpen))),"移除背景不改变角色口型采样时间");
        selectedClip=null;selectedActor=null;Refresh();
        await App.CaptureCheckpointAsync("transparent-scene");
    }
}
