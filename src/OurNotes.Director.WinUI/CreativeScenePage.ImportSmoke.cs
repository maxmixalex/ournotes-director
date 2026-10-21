using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    // Invoked at the real candidate-frame boundary, on the UI thread and before
    // acceptance. Only the packaged regression route assigns this hook.
    Action? storyImportPreviewReadyForSmoke;

    internal async Task RunStoryImportTransactionSmokeAsync(Action<bool,string> check)
    {
        await scrubber.FlushAsync();await viewport.PauseForEditingAsync();
        if(importingStory||!viewport.HasCurrentSource)throw new InvalidOperationException("导入事务检查需要已载入的场景。");
        string originalText=document.ToJson(),originalSaved=savedText,originalDirectory=directory;
        string? originalPath=path,originalActor=selectedActor,originalClip=selectedClip,originalReplacement=replaceAssetClip;
        string[] originalUndo=undo.ToArray(),originalRedo=redo.ToArray();
        double originalTime=playhead;var originalRange=timeline.WorkRange;
        bool originalNotice=notice.IsOpen;var originalSeverity=notice.Severity;string originalMessage=notice.Message;
        byte[]? originalDraft=File.Exists(draftPath)?await File.ReadAllBytesAsync(draftPath):null;
        var cases=new JsonArray();
        Task? replacement=null;
        try
        {
            // Normal editor operations give both history directions real values;
            // an accidentally cleared empty stack would not detect the regression.
            string marker="";
            await EditAsync(()=>marker=document.AddClip("","hold",0,1,new JsonObject{["cmd"]="Delay",["Frames"]=0},"取消导入历史"),false);
            await EditAsync(()=>document.Clip(marker)["label"]="重做历史",false);
            await UndoAsync(false);
            timeline.SetRange(new TimelineRange(5,20,30));
            check(undo.Count>0&&redo.Count>0,"导入事务检查使用真实编辑/撤销建立非空的撤销与重做历史");

            var owner=document;string before=document.ToJson();
            string[] beforeUndo=undo.ToArray(),beforeRedo=redo.ToArray();
            string? beforeActor=selectedActor,beforeClip=selectedClip,beforePath=path;
            string beforeSaved=savedText,beforeDirectory=directory;
            var beforeRange=timeline.WorkRange;var beforePreview=viewport.LastFrame!;int beforeFrame=beforePreview.Frame;
            string[] beforeTargets=beforePreview.Targets.Select(target=>target.Id).ToArray();
            byte[] beforeDraft=await File.ReadAllBytesAsync(draftPath);

            var cancelled=await Attempt(()=>
            {
                var close=MainWindow.Descendants<Button>(notice).Single(button=>button.Name=="CloseButton");
                check(notice.ActionButton is null&&close.IsEnabled&&notice.IsOpen,
                    "解析提示仅保留右侧关闭按钮，没有额外取消解析按钮");
                new ButtonAutomationPeer(close).Invoke();
                check(importCancellation?.IsCancellationRequested==true&&!notice.IsOpen,
                    "点击解析提示的真实关闭按钮立即取消正在载入的原剧情");
            });
            check(cancelled is OperationCanceledException&&ReferenceEquals(document,owner)&&document.ToJson()==before
                &&undo.SequenceEqual(beforeUndo)&&redo.SequenceEqual(beforeRedo)
                &&selectedActor==beforeActor&&selectedClip==beforeClip&&path==beforePath&&savedText==beforeSaved
                &&directory==beforeDirectory&&timeline.WorkRange==beforeRange
                &&(await File.ReadAllBytesAsync(draftPath)).SequenceEqual(beforeDraft),
                "最终原生预览已呈现才取消导入，原文档、双向历史、选择、路径、区间与草稿仍完整保留");
            var restored=viewport.LastFrame;
            check(viewport.HasCurrentSource&&!previewDirty&&restored is not null&&restored.Frame==beforeFrame
                &&restored.Targets.Select(target=>target.Id).SequenceEqual(beforeTargets)
                &&previewInput is SceneSnapshot recovered&&File.Exists(recovered.FilePath),
                "取消候选预览后重新载入原项目的真实帧，当前源有效且持有仍存在的输入快照");
            cases.Add(new JsonObject{["case"]="cancel-after-native-frame",["cancelled"]=true,["restoredFrame"]=beforeFrame});

            replacement=null;
            var edited=await Attempt(()=>replacement=EditAsync(()=>document.Clip(marker)["label"]="载入期间的新修改"));
            if(replacement is not null)await replacement;
            check(edited is OperationCanceledException&&replacement is not null&&ReferenceEquals(document,owner)
                &&S(document.Clip(marker),"label")=="载入期间的新修改"&&undo.Count==beforeUndo.Length+1&&redo.Count==0
                &&viewport.HasCurrentSource&&!previewDirty
                &&File.ReadAllText(draftPath)==document.ToJson(),
                "候选帧完成时继续编辑，旧导入退出；新修改、历史、草稿和等待中的新预览不会被恢复逻辑覆盖");
            cases.Add(new JsonObject{["case"]="edit-after-native-frame",["cancelled"]=true,["newEditPreserved"]=true});

            // Use the normal New handler without a confirmation dialog: this
            // fixture has first been saved through the real save operation.
            SaveTo(App.SmokeEvidencePath("import-transaction-owner.scene.json"));
            replacement=null;
            var replaced=await Attempt(()=>replacement=NewAsync());
            if(replacement is not null)await replacement;
            check(replaced is OperationCanceledException&&replacement is not null&&document.Actors.Count==0&&document.Clips.Count==0
                &&path is null&&undo.Count==0&&redo.Count==0&&!viewport.HasCurrentSource&&viewport.LastFrame is null,
                "候选帧完成时新建空白场景，旧导入不会重新激活已失效的源，也不会覆盖新空白画布");
            cases.Add(new JsonObject{["case"]="new-after-native-frame",["cancelled"]=true,["blankCanvasPreserved"]=true});
        }
        finally
        {
            storyImportPreviewReadyForSmoke=null;importCancellation=null;importingStory=false;
            if(replacement is not null)try{await replacement;}catch(OperationCanceledException){ }
            document=CreativeScene.Parse(originalText);savedText=originalSaved;directory=originalDirectory;path=originalPath;
            selectedActor=originalActor;selectedClip=originalClip;replaceAssetClip=originalReplacement;playhead=originalTime;
            RestoreStack(undo,originalUndo);RestoreStack(redo,originalRedo);
            previewDirty=true;loadedSourceKey=null;Refresh();
            if(originalRange is {} range)timeline.SetRange(range);else timeline.ClearRange();
            await ReloadAsync();
            if(originalDraft is null){if(File.Exists(draftPath))File.Delete(draftPath);}
            else await File.WriteAllBytesAsync(draftPath,originalDraft);
            notice.Severity=originalSeverity;notice.Message=originalMessage;notice.IsOpen=originalNotice;
            DesktopSettings.AtomicWrite(App.SmokeEvidencePath("story-import-transaction.json"),new JsonObject
            { ["boundary"]="presented native candidate frame before source acceptance",["cases"]=cases }.ToJsonString(DesktopSettings.JsonOptions));
        }

        async Task<Exception?> Attempt(Action whenReady)
        {
            var owner=document;string text=owner.ToJson();long version=loadVersion,oldSession=viewport.LastFrame?.SessionId??0;
            using var cancellation=new CancellationTokenSource();
            importingStory=true;importCancellation=cancellation;
            notice.Message="正在载入可编辑预览…";notice.Severity=InfoBarSeverity.Informational;
            notice.IsOpen=true;notice.ApplyTemplate();UpdateLayout();
            notice.IsOpen=false;
            check(!cancellation.IsCancellationRequested,"程序关闭解析提示不触发用户取消事件");
            notice.IsOpen=true;UpdateLayout();
            var candidate=CreativeScene.Blank(document.Site,document.Language);
            candidate.Canvas["width"]=64;candidate.Canvas["height"]=64;
            candidate.AddClip("","hold",0,1,new JsonObject{["cmd"]="Delay",["Frames"]=0},"候选场景");
            bool reached=false;
            storyImportPreviewReadyForSmoke=()=>
            {
                reached=true;
                check(DispatcherQueue.HasThreadAccess&&!viewport.HasCurrentSource&&viewport.LastFrame is {Frame:0} frame
                    &&frame.SessionId!=oldSession&&ReferenceEquals(document,owner)&&owner.ToJson()==text,
                    "导入提交点位于真实候选原生帧呈现后的UI线程；提交前仍保留旧文档且候选源不可编辑");
                whenReady();
            };
            void RequireOwner()
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if(disposed||!ReferenceEquals(document,owner)||owner.ToJson()!=text||loadVersion!=version)
                    throw new OperationCanceledException();
            }
            try
            {
                await InstallImportedSceneAsync(candidate,directory,RequireOwner,cancellation.Token);
                return null;
            }
            catch(Exception error)
            {
                check(reached,"取消或替换发生在实际候选预览完成后，未用解析前取消代替迟到结果检查");
                return error;
            }
            finally{storyImportPreviewReadyForSmoke=null;importCancellation=null;importingStory=false;}
        }

        static void RestoreStack(Stack<string> stack,string[] entries)
        {stack.Clear();for(int i=entries.Length-1;i>=0;i--)stack.Push(entries[i]);}
    }
}
