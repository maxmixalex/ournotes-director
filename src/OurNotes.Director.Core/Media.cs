using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OurNotes.Director.Core;

public static class Media
{
    // Keep every atempo stage within 0.5..2 to avoid high-rate sample skipping.
    public static string TempoFilter(double speed)
    {
        if(!double.IsFinite(speed)||speed<.25||speed>4)throw new ArgumentOutOfRangeException(nameof(speed));
        var factors=new List<double>();while(speed>2){factors.Add(2);speed/=2;}while(speed<.5){factors.Add(.5);speed/=.5;}factors.Add(speed);
        return string.Join(',',factors.Select(f=>"atempo="+f.ToString("R",CultureInfo.InvariantCulture)));
    }
    public static string Run(string executable,IEnumerable<string> args)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var a in args)start.ArgumentList.Add(a);using var p=Process.Start(start)??throw new IOException($"Cannot start {executable}");var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();p.WaitForExit();if(p.ExitCode!=0)throw new IOException($"{executable}: {stderr.GetAwaiter().GetResult()}");return stdout.GetAwaiter().GetResult();
    }
    public static JsonElement Probe(string path,string ffprobe)
    {
        using var document = JsonDocument.Parse(Run(ffprobe,["-v","error","-show_format","-show_streams","-of","json",Path.GetFullPath(path)]));
        return document.RootElement.Clone();
    }
    public static object Mux(string video,string audio,string? subtitles,string output,bool overwrite,string ffmpeg,string ffprobe)
    {
        video=Path.GetFullPath(video);audio=Path.GetFullPath(audio);output=Path.GetFullPath(output);
        if(subtitles is not null)subtitles=Path.GetFullPath(subtitles);
        if(new[]{video,audio,subtitles}.Any(p=>string.Equals(p,output,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Output must differ from source files");
        if(!overwrite&&(File.Exists(output)||File.Exists(output+".source.json")))throw new IOException("Output exists");
        var extension=Path.GetExtension(output).ToLowerInvariant();
        if(extension is not (".mp4" or ".mkv" or ".mov"))throw new ArgumentException("media mux outputs .mp4, .mkv, or .mov");
        var info=Probe(video,ffprobe);
        var v=info.GetProperty("streams").EnumerateArray().FirstOrDefault(s=>s.GetProperty("codec_type").GetString()=="video");
        if(v.ValueKind==JsonValueKind.Undefined)throw new ArgumentException("No video stream");
        if(extension==".mp4"&&v.GetProperty("codec_name").GetString() is not ("h264" or "hevc"))throw new ArgumentException("Use .mkv for this video codec; .mp4 mux supports H.264/HEVC");
        if(extension==".mov"&&v.GetProperty("codec_name").GetString() is not ("cfhd" or "prores" or "h264" or "hevc"))throw new ArgumentException(".mov mux supports CineForm, ProRes, H.264, or HEVC; use .mkv for this codec");
        if(!Probe(audio,ffprobe).GetProperty("streams").EnumerateArray().Any(s=>s.GetProperty("codec_type").GetString()=="audio"))throw new ArgumentException("No audio stream");
        var durationText=v.TryGetProperty("duration",out var streamDuration)?streamDuration.GetString():null;
        if(durationText is null or "N/A")durationText=info.GetProperty("format").GetProperty("duration").GetString();
        if(!double.TryParse(durationText,NumberStyles.Float,CultureInfo.InvariantCulture,out var duration)||!double.IsFinite(duration)||duration<=0)throw new InvalidDataException("Video duration unavailable");
        var length=duration.ToString("R",CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary=Path.Combine(Path.GetDirectoryName(output)!,".mux-"+Guid.NewGuid().ToString("N")+extension);
        var clippedSubtitles=temporary+".srt";
        try
        {
            var arguments=new List<string>{"-v","error","-nostdin","-y","-i",video,"-i",audio};
            if(subtitles is not null)
            {
                if(!Path.GetExtension(subtitles).Equals(".srt",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Subtitles must be SRT");
                File.WriteAllText(clippedSubtitles,ClipSrt(File.ReadAllText(subtitles),duration));
                arguments.AddRange(["-i",clippedSubtitles]);
            }
            arguments.AddRange(["-map","0:v:0","-map","1:a:0"]);
            if(subtitles is not null)arguments.AddRange(["-map","2:s:0","-c:s",extension is ".mp4" or ".mov"?"mov_text":"srt"]);
            arguments.AddRange(["-c:v","copy","-af","apad,atrim=duration="+length,"-ar","48000","-ac","2","-c:a",extension==".mp4"?"aac":extension==".mov"?"pcm_s24le":"pcm_s16le"]);
            if(extension==".mp4")arguments.AddRange(["-b:a","256k","-movflags","+faststart"]);
            arguments.AddRange(["-t",length,"-avoid_negative_ts","disabled",temporary]);
            Run(ffmpeg,arguments);
            File.Move(temporary,output,overwrite);
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);if(File.Exists(clippedSubtitles))File.Delete(clippedSubtitles);}
        static string Fingerprint(string path){using var stream=File.OpenRead(path);return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));}
        var result=JsonOutput.Object(("output", output), ("duration", duration), ("video", JsonOutput.Object(("path", video), ("sha256", Fingerprint(video)), ("codec", "copy"))), ("audio", JsonOutput.Object(("path", audio), ("sha256", Fingerprint(audio)), ("policy", "Replace original audio; pad silence or trim to video duration"), ("codec", extension==".mp4"?"aac":extension==".mov"?"pcm_s24le":"pcm_s16le"))), ("subtitles", subtitles is null?null:JsonOutput.Object(("path", subtitles), ("sha256", Fingerprint(subtitles)), ("mode", "selectable track; not burned in"))));
        File.WriteAllText(output+".source.json",JsonOutput.Serialize(result,Shot.JsonOptions));return result;
    }
    public static string ClipSrt(string source,double duration)
    {
        if(!double.IsFinite(duration)||duration<=0)throw new ArgumentException("Invalid subtitle duration");
        var result=new System.Text.StringBuilder();int count=0;
        static long Milliseconds(string value)
        {
            var parts=value.Split(':',',');
            long h=long.Parse(parts[0],CultureInfo.InvariantCulture),m=long.Parse(parts[1],CultureInfo.InvariantCulture),s=long.Parse(parts[2],CultureInfo.InvariantCulture),ms=long.Parse(parts[3],CultureInfo.InvariantCulture);
            if(m>=60||s>=60)throw new ArgumentException("Invalid SRT timestamp");
            return checked(h*3600000+m*60000+s*1000+ms);
        }
        static string Stamp(long ms)=>FormattableString.Invariant($"{ms/3600000:00}:{ms/60000%60:00}:{ms/1000%60:00},{ms%1000:000}");
        foreach(var block in System.Text.RegularExpressions.Regex.Split(source.Replace("\r","").Trim(),@"\n[ \t]*\n"))
        {
            var match=System.Text.RegularExpressions.Regex.Match(block,@"\A\d+\n(?<start>\d{2,}:\d{2}:\d{2},\d{3}) --> (?<end>\d{2,}:\d{2}:\d{2},\d{3})[^\n]*\n(?<text>[\s\S]+)\z");
            if(!match.Success)throw new ArgumentException("Invalid SRT cue");
            long start=Milliseconds(match.Groups["start"].Value),end=Milliseconds(match.Groups["end"].Value);
            if(end<=start)throw new ArgumentException("SRT cue must have positive duration");
            end=Math.Min(end,(long)Math.Floor(duration*1000));if(start>=end)continue;
            result.Append(++count).Append('\n').Append(Stamp(start)).Append(" --> ").Append(Stamp(end)).Append('\n').Append(match.Groups["text"].Value).Append("\n\n");
        }
        if(count==0)throw new ArgumentException("No subtitle cues within video duration");
        return result.ToString();
    }
    public static object Clip(string path,string output,double start,double duration,bool overwrite,string ffmpeg,string ffprobe,string codec="prores",string cineformQuality=VideoEncoding.DefaultCineFormQuality)
    {
        path=Path.GetFullPath(path);output=Path.GetFullPath(output);if(start<0||duration<=0||!double.IsFinite(start)||!double.IsFinite(duration))throw new ArgumentException("Invalid media start/duration");if(!overwrite&&(File.Exists(output)||File.Exists(output+".source.json")))throw new IOException("Output exists");
        codec=VideoEncoding.NormalizeCodec(codec);
        if(codec is not ("prores" or "cineform"))throw new ArgumentException("media clip supports --codec prores or cineform");
        if(!output.EndsWith(".mov",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("media clip outputs a .mov file");
        if(string.Equals(path,output,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Output must differ from source file");
        var info=Probe(path,ffprobe);var video=info.GetProperty("streams").EnumerateArray().FirstOrDefault(s=>s.GetProperty("codec_type").GetString()=="video");if(video.ValueKind==JsonValueKind.Undefined)throw new ArgumentException("No video stream");
        VideoEncoding.Validate(codec,video.GetProperty("width").GetInt32(),video.GetProperty("height").GetInt32(),cineformQuality);
        var length=double.Parse(info.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);if(start+duration>length+.001)throw new ArgumentException("Requested media range exceeds source duration");
        // Preserve the existing ProRes HQ default; CineForm is always RGB+alpha.
        string[] encoding=codec=="cineform"?VideoEncoding.Arguments(codec,cineformQuality):["-c:v","prores_ks","-profile:v","3","-pix_fmt","yuv422p10le"];
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);string temp=Path.Combine(Path.GetDirectoryName(output)!,Guid.NewGuid()+".mov");try{Run(ffmpeg,new[]{"-v","error","-nostdin","-y","-i",path,"-ss",start.ToString(CultureInfo.InvariantCulture),"-t",duration.ToString(CultureInfo.InvariantCulture),"-map","0:v:0","-an"}.Concat(encoding).Concat(["-f","mov",temp]));File.Move(temp,output,overwrite);}finally{if(File.Exists(temp))File.Delete(temp);}
        var result=JsonOutput.Object(("source", path), ("start", start), ("duration", duration), ("output", output), ("codec", codec=="cineform"?"cfhd":"prores_ks HQ"), ("cineformQuality", codec=="cineform"?VideoEncoding.NormalizeCineFormQuality(cineformQuality):null), ("alpha", codec=="cineform"?"preserved from source":"none"), ("audio", false), ("sourceLength", new FileInfo(path).Length), ("sourceLastWriteUtc", File.GetLastWriteTimeUtc(path)));File.WriteAllText(output+".source.json",JsonOutput.Serialize(result,Shot.JsonOptions));return result;
    }
}
