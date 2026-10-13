using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OurNotes.Director.Desktop.Core;

/// <summary>Presentation-only Chinese labels. The archive identifiers are never changed.</summary>
public static class DisplayNames
{
    static readonly JsonObject Names = ReadNames();
    static readonly Dictionary<string, string> Costume = new(StringComparer.OrdinalIgnoreCase)
    {
        ["casual"] = "私服", ["spring"] = "春季", ["summer"] = "夏季", ["winter"] = "冬季", ["school"] = "制服",
        ["hs"] = "高中", ["jhs"] = "初中", ["1st"] = "一年级", ["2nd"] = "二年级", ["3rd"] = "三年级",
        ["live"] = "演出服", ["roomwear"] = "居家服", ["arbeit"] = "打工", ["livehouse"] = "Live House", ["ring"] = "RiNG",
        ["caretaker"] = "看护", ["child"] = "童年", ["detective"] = "侦探", ["idol"] = "偶像", ["virtual"] = "虚拟形象",
        ["soundonly"] = "仅语音", ["suits"] = "西装", ["sweat"] = "运动服", ["maid"] = "女仆", ["still"] = "静态",
        ["mask"] = "面具", ["silhouette"] = "剪影", ["glasses"] = "眼镜", ["noseglasses"] = "搞笑眼镜", ["sunglasses"] = "墨镜",
        ["hat"] = "帽子", ["hairdown"] = "散发", ["twintails"] = "双马尾", ["low"] = "低画质"
    };
    static readonly Dictionary<string, string> AnimationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["idle"] = "待机", ["default"] = "默认", ["normal"] = "普通", ["neutral"] = "平静", ["base"] = "基础", ["stand"] = "站立",
        ["smile"] = "微笑", ["smiling"] = "微笑", ["laugh"] = "笑", ["happy"] = "开心", ["joy"] = "喜悦", ["angry"] = "生气",
        ["anger"] = "生气", ["sad"] = "难过", ["cry"] = "哭泣", ["crying"] = "哭泣", ["surprise"] = "惊讶", ["surprised"] = "惊讶",
        ["shy"] = "害羞", ["blush"] = "脸红", ["embarrassed"] = "害羞", ["troubled"] = "困扰", ["worry"] = "担心", ["worried"] = "担心",
        ["confused"] = "疑惑", ["thinking"] = "思考", ["think"] = "思考", ["serious"] = "认真", ["sigh"] = "叹气", ["sleep"] = "睡觉",
        ["sleepy"] = "困倦", ["nod"] = "点头", ["shake"] = "摇动", ["deny"] = "否认", ["yes"] = "肯定", ["no"] = "否定",
        ["talk"] = "说话", ["listen"] = "倾听", ["wave"] = "挥手", ["point"] = "指向", ["look"] = "看向", ["turn"] = "转身",
        ["walk"] = "行走", ["run"] = "奔跑", ["sit"] = "坐下", ["jump"] = "跳跃", ["bow"] = "鞠躬", ["greet"] = "招呼",
        ["greeting"] = "招呼", ["peace"] = "比耶", ["wink"] = "眨眼", ["eye"] = "眼睛", ["eyes"] = "眼睛", ["mouth"] = "嘴部",
        ["open"] = "张开", ["close"] = "闭合", ["closed"] = "闭合", ["hand"] = "手", ["head"] = "头部", ["body"] = "身体",
        ["left"] = "左", ["right"] = "右", ["up"] = "上", ["down"] = "下", ["front"] = "正面", ["back"] = "后层", ["side"] = "侧面",
        ["loop"] = "循环", ["start"] = "开始", ["end"] = "结束", ["in"] = "进入", ["out"] = "退出", ["on"] = "开启", ["off"] = "关闭",
        ["small"] = "轻微", ["big"] = "大幅", ["strong"] = "强烈", ["weak"] = "轻微", ["fast"] = "快速", ["slow"] = "缓慢",
        ["pose"] = "姿势", ["action"] = "动作", ["motion"] = "动作", ["expression"] = "表情", ["tap"] = "点击", ["touch"] = "触碰",
        ["kime"] = "亮相", ["nervous"] = "紧张", ["question"] = "疑问", ["check"] = "确认", ["note"] = "察觉",
        ["denial"] = "摇头否认", ["bye"] = "挥手告别", ["join"] = "合手", ["bsmile"] = "苦笑", ["grinning"] = "咧嘴笑",
        ["pale"] = "脸色苍白", ["pain"] = "痛苦", ["salk"] = "闹别扭", ["sulk"] = "闹别扭", ["shadow"] = "面部阴影",
        ["C"] = "正面", ["L"] = "左向", ["R"] = "右向"
    };
    static JsonObject ReadNames()
    {
        using var stream = typeof(DisplayNames).Assembly.GetManifestResourceStream("OurNotes.Director.Desktop.Core.Resources.CharacterNames.json")
            ?? throw new InvalidOperationException("角色名称资源缺失。");
        return JsonNode.Parse(stream)!.AsObject();
    }
    public static string Character(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        string compact = name.Replace(" ", "", StringComparison.Ordinal);
        var direct = Names["characters"]?.AsObject().FirstOrDefault(pair => pair.Key.Equals(compact, StringComparison.OrdinalIgnoreCase)).Value
            ?? Names["aliases"]?[compact];
        if (direct is not null) return direct.ToString();
        foreach (var part in new[] { "_back", "_front", "_body", "_face" })
            if (name.EndsWith(part, StringComparison.OrdinalIgnoreCase))
            {
                string stem = name[..^part.Length], translated = Character(stem);
                if (translated != stem) return translated + " · " + (part == "_back" ? "后景部件" : part == "_front" ? "前景部件" : part == "_body" ? "身体" : "面部");
            }
        return name;
    }
    public static string Model(string id)
    {
        var match = Regex.Match(id, @"^(?:adv_)?live2d_(?:sub_)?(?<name>[^_]+)(?:_\d{3})?_(?<costume>.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success) return Character(id);
        string character = Character(match.Groups["name"].Value);
        string rest = match.Groups["costume"].Value.Replace("nose_glasses", "noseglasses", StringComparison.OrdinalIgnoreCase);
        var words = rest.Split('_').Where(word => word != "01").Select(word => Costume.GetValueOrDefault(word) ?? word);
        string costume = string.Join(" · ", words);
        return character + (costume.Length > 0 ? " · " + costume : "");
    }
    public static string Animation(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return id;
        string cleaned = Regex.Replace(id, @"\.(?:motion3|exp3)(?:\.json)?$", "", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"^(?:mtn|exp|anim)_", "", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"([a-z])([A-Z])", "$1_$2", RegexOptions.CultureInvariant);
        bool translated = false;
        var words = Regex.Split(cleaned, @"[_\-\s]+").Where(word => word.Length > 0).Select(word =>
        {
            var numbered = Regex.Match(word, @"^(?<word>[A-Za-z]+)(?<number>\d*)$", RegexOptions.CultureInvariant);
            string stem = numbered.Success ? numbered.Groups["word"].Value : word;
            string number = numbered.Success ? numbered.Groups["number"].Value : "";
            string friendly = AnimationWords.GetValueOrDefault(stem) ?? Character(stem);
            if (friendly != stem) translated = true;
            return friendly + (number.Length > 0 ? " " + number : "");
        }).ToArray();
        return translated ? string.Join(" · ", words) : cleaned;
    }
    public static string SearchAlias(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return query;
        foreach (var pair in Names["characters"]!.AsObject())
            if (pair.Value?.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) == true) return pair.Key;
        return query;
    }
}
