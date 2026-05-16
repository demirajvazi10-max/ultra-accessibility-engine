using System;

namespace UltraVideoEditor
{
    public enum TransitionType
    {
        None,
        Fade,
        Crossfade,
        SlideLeft,
        SlideRight,
        SlideUp,
        SlideDown,
        WipeLeft,
        WipeRight,
        WipeUp,
        WipeDown,
        ZoomIn,
        ZoomOut
    }

    public class TransitionEffect
    {
        public string Name { get; set; }
        public TransitionType Type { get; set; }
        public double Duration { get; set; } = 1.0;
        public int ClipIndex1 { get; set; }
        public int ClipIndex2 { get; set; }

        public string Icon => Type switch
        {
            TransitionType.Fade => "🌅",
            TransitionType.Crossfade => "🔄",
            TransitionType.SlideLeft => "⬅️",
            TransitionType.SlideRight => "➡️",
            TransitionType.SlideUp => "⬆️",
            TransitionType.SlideDown => "⬇️",
            TransitionType.WipeLeft => "🧹",
            TransitionType.WipeRight => "🧹",
            TransitionType.ZoomIn => "🔍➕",
            TransitionType.ZoomOut => "🔍➖",
            _ => "📄"
        };

        public string FFmpegFilter
        {
            get
            {
                string transitionType = Type switch
                {
                    TransitionType.Fade => "fade",
                    TransitionType.Crossfade => "fade",
                    TransitionType.SlideLeft => "slideleft",
                    TransitionType.SlideRight => "slideright",
                    TransitionType.SlideUp => "slideup",
                    TransitionType.SlideDown => "slidedown",
                    TransitionType.WipeLeft => "wipeleft",
                    TransitionType.WipeRight => "wiperight",
                    TransitionType.ZoomIn => "zoom",
                    TransitionType.ZoomOut => "zoom",
                    _ => "fade"
                };
                return $"xfade=transition={transitionType}:duration={Duration}:offset={Duration}";
            }
        }

        public string AudioCrossfadeFilter => $"acrossfade=d={Duration}:c1=1:c2=1";
    }
}