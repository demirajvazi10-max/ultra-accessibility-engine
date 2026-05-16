using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Data;
using Newtonsoft.Json;

namespace UltraVideoEditor
{
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return string.IsNullOrEmpty(value as string) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return System.Windows.Visibility.Visible;
        }
    }

    // Keyframe za animaciju
    public class AnimationKeyframe
    {
        public double Time { get; set; } = 0;
        public double Zoom { get; set; } = 1.0;
        public double Rotation { get; set; } = 0;
        public double X { get; set; } = 0;
        public double Y { get; set; } = 0;
        public double Opacity { get; set; } = 1.0;
    }

    // Scene za AI animaciju
    public class AnimationScene
    {
        public string ImageName { get; set; } = "";
        public string ImagePath { get; set; } = "";
        public double Duration { get; set; } = 5.0;
        public string Effect { get; set; } = "Fade In";
        public string Description { get; set; } = "";
        public List<string> AvailableImages { get; set; } = new List<string>();
        public List<string> AvailableEffects { get; set; } = new List<string> { "Fade In", "Fade Out", "Zoom In", "Zoom Out", "Slide Left", "Slide Right", "None" };
    }

    // Podaci za tekst na slici (overlay)
    public class TextOverlayData
    {
        public string Text { get; set; } = "";
        public string Font { get; set; } = "Arial";
        public int SelectedFontSize { get; set; } = 48;
        public string Color { get; set; } = "#FFFFFF";
        public string Position { get; set; } = "Centar";
        public bool Enabled { get; set; } = false;
    }

    // Timeline Item sa podrškom za više traka i keyframe-ove
    public class TimelineItem
    {
        private static string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private static string L(string key) => LanguageManager.GetText(key, _LangCode);
        private static string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        public double FixedPosition { get; set; } = 0;
        public string Path { get; set; } = "";
        public double Duration { get; set; } = 5.0;

        // DODATO ZA SELEKCIJU
        public bool IsSelected { get; set; } = false;

        // DODATO ZA FIKSNU POZICIJU (POSTAVI NA OSU)
        public bool UseFixedPosition { get; set; } = false;

        // DODATO ZA TEKST NA SLICI (OVERLAY)
        public TextOverlayData TextOverlay { get; set; } = new TextOverlayData();

        [JsonIgnore]
        public double Start { get; set; } = 0;

        [JsonIgnore]
        public double End { get; set; } = 5.0;

        [JsonIgnore]
        public int Index { get; set; } = 0;

        [JsonIgnore]
        public string TimeRange { get; set; } = "";

        public string Name { get; set; } = "";
        public string Type { get; set; } = "User";
        public double Volume { get; set; } = 100;
        public int TrackIndex { get; set; } = 0;

        [JsonIgnore]
        public string AccessibilityDescription { get; set; } = "";

        public string AudioDescription { get; set; }
        public string AmbientSoundPath { get; set; } = "";
        public string ContentTag { get; set; }  // Hybrid Content Selector: Emotional|Portrait|Action|Nature
        public VideoEffectData VideoEffect { get; set; } = new VideoEffectData();
        public List<AnimationKeyframe> Keyframes { get; set; } = new List<AnimationKeyframe>();

        [JsonIgnore]
        public string TrackName => TrackIndex switch
        {
            0 => "🎬 Video 1",
            1 => "🎬 Video 2",
            2 => "🎵 Audio 1",
            3 => "🎵 Audio 2",
            _ => $"Track {TrackIndex + 1}"
        };

        [JsonIgnore]
        public string TypeIcon
        {
            get
            {
                if (Path.EndsWith(".mp3") || Path.EndsWith(".wav") || Path.EndsWith(".m4a") || Path.EndsWith(".flac") || Path.EndsWith(".ogg"))
                    return "🎵";
                if (Path.EndsWith(".jpg") || Path.EndsWith(".png") || Path.EndsWith(".jpeg") || Path.EndsWith(".bmp"))
                    return "🖼️";
                if (Path.EndsWith(".mp4") || Path.EndsWith(".avi") || Path.EndsWith(".mov") || Path.EndsWith(".mkv"))
                    return "🎬";
                return "📄";
            }
        }

        [JsonIgnore]
        public bool IsAudio => Path.EndsWith(".mp3") || Path.EndsWith(".wav") || Path.EndsWith(".m4a") || Path.EndsWith(".flac") || Path.EndsWith(".ogg");

        [JsonIgnore]
        public bool IsImage => Path.EndsWith(".jpg") || Path.EndsWith(".jpeg") || Path.EndsWith(".png") || Path.EndsWith(".bmp");

        [JsonIgnore]
        public bool IsVideo => Path.EndsWith(".mp4") || Path.EndsWith(".avi") || Path.EndsWith(".mov") || Path.EndsWith(".mkv");

        [JsonIgnore]
        public bool IsVideoTrack => TrackIndex == 0 || TrackIndex == 1;

        [JsonIgnore]
        public bool IsAudioTrack => TrackIndex == 2 || TrackIndex == 3;

        public override string ToString()
        {
            string trackInfo = TrackIndex switch
            {
                0 => "🎬 Video 1 ",
                1 => "🎬 Video 2 ",
                2 => "🎵 Audio 1 ",
                3 => "🎵 Audio 2 ",
                _ => $"Track {TrackIndex + 1} "
            };

            string typeInfo = IsAudio ? "Audio" : (IsVideo ? "Video" : "Slika");
            string volumeInfo = IsAudio ? string.Format(L("models_volume_sfx"), Volume) : "";
            string audioDescInfo = !string.IsNullOrEmpty(AudioDescription) ? $", opis: {AudioDescription}" : "";
            string textOverlayInfo = TextOverlay.Enabled && !string.IsNullOrEmpty(TextOverlay.Text) ? $", tekst: {TextOverlay.Text}" : "";

            return $"{trackInfo}{Index}. {typeInfo}: {Name} (trajanje: {TimeSpan.FromSeconds(Duration):mm\\:ss}{volumeInfo}{audioDescInfo}{textOverlayInfo})";
        }

        public void AddKeyframe(AnimationKeyframe keyframe)
        {
            Keyframes.Add(keyframe);
            Keyframes = Keyframes.OrderBy(k => k.Time).ToList();
        }

        public void RemoveKeyframeAt(double time)
        {
            var kf = Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) < 0.01);
            if (kf != null) Keyframes.Remove(kf);
        }

        [JsonIgnore]
        public string AccessibilityText
        {
            get
            {
                string tip = IsAudio ? "Audio" : (IsImage ? L("model_image2") : "Video");
                string opis = !string.IsNullOrEmpty(AudioDescription) ? string.Format(L("model_desc"), AudioDescription) : "";
                string tekst = TextOverlay.Enabled && !string.IsNullOrEmpty(TextOverlay.Text) ? string.Format(L("model_txt_on"), TextOverlay.Text) : "";
                string track = TrackName;
                return string.Format(L("model_acc_text"), track, Index, tip, Name,
                    TimeSpan.FromSeconds(Duration).ToString(@"mm\:ss"),
                    TimeSpan.FromSeconds(Start).ToString(@"mm\:ss"),
                    TimeSpan.FromSeconds(End).ToString(@"mm\:ss"),
                    Volume, opis, tekst);
            }
        }
    }

    public class SubtitleItem
    {
        public string Text { get; set; } = "";
        public double Start { get; set; } = 0;
        public double End { get; set; } = 5.0;
    }

    public class VideoEffectData
    {
        public double Brightness { get; set; } = 0;
        public double Contrast { get; set; } = 0;
        public double Blur { get; set; } = 0;
        public double Speed { get; set; } = 1.0;
    }

    public class AudioSettingsData
    {
        public double BassBoost { get; set; } = 0;
        public double TrebleBoost { get; set; } = 0;
        public double ReverbAmount { get; set; } = 0;
        public bool CompressorEnabled { get; set; } = false;
    }

    public class ExportSettingsData
    {
        public string Format { get; set; } = "MP4";
        public string Quality { get; set; } = "Medium";
        public bool ExportAudioOnly { get; set; } = false;
    }

    public class AISubtitle
    {
        public string Text { get; set; } = "";
        public double Start { get; set; } = 0;
        public double End { get; set; } = 0;
        public double Confidence { get; set; } = 0;
    }

    // TranscriptionResult i TranscriptionSegment definisani su u AITranscription.cs kao ugnjezdene klase.
    // Ovdje su bili duplirana definicija — uzrokovala CS0101 build error. Obrisano.

    public class AILayoutResponse
    {
        public List<AILayoutItem> raspored { get; set; }
    }

    public class AILayoutItem
    {
        public int animacija_index { get; set; }
        public double pocetak { get; set; }
        public double kraj { get; set; }
        public string razlog { get; set; }
    }

    public static class AnimationEffects
    {
        public static string GetFFmpegFilter(TimelineItem item, double fps = 30)
        {
            if (item.Keyframes == null || item.Keyframes.Count < 2) return "";

            var sorted = item.Keyframes.OrderBy(k => k.Time).ToList();
            string filter = "";

            if (sorted.Any(k => Math.Abs(k.Zoom - 1.0) > 0.01) || sorted.Any(k => k.X != 0 || k.Y != 0))
            {
                filter += $"zoompan=z='if(eq(on,0),{sorted[0].Zoom}',";
                for (int i = 1; i < sorted.Count; i++)
                {
                    double startTime = sorted[i - 1].Time;
                    double endTime = sorted[i].Time;
                    double startZoom = sorted[i - 1].Zoom;
                    double endZoom = sorted[i].Zoom;
                    double startX = sorted[i - 1].X;
                    double endX = sorted[i].X;
                    double startY = sorted[i - 1].Y;
                    double endY = sorted[i].Y;

                    filter += $"if(between(t,{startTime},{endTime}),{startZoom}+({endZoom}-{startZoom})*(t-{startTime})/({endTime}-{startTime}),";
                }
                filter += $"{sorted.Last().Zoom}" + new string(')', sorted.Count) + $"'):d=1:fps={fps}:s=1920x1080,";
            }

            if (sorted.Any(k => Math.Abs(k.Rotation) > 0.01))
            {
                filter += $"rotate=a='{sorted[0].Rotation}*PI/180'";
                for (int i = 1; i < sorted.Count; i++)
                {
                    filter += $"+({sorted[i].Rotation}-{sorted[i - 1].Rotation})*PI/180*(t-{sorted[i - 1].Time})/({sorted[i].Time}-{sorted[i - 1].Time})";
                }
                filter += "':ow=hypot(iw,ih):oh=ow,";
            }

            if (sorted.Any(k => Math.Abs(k.Opacity - 1.0) > 0.01))
            {
                filter += $"fade=type=in:start_time=0:duration={sorted.Last().Time},fade=type=out:start_time={sorted.Last().Time - 1}:duration=1,";
            }

            return filter.TrimEnd(',');
        }
    }
}