using System.Collections.Generic;

namespace UltraVideoEditor
{
    public class ProjectData
    {
        public List<TimelineItem> TimelineItems { get; set; } = new List<TimelineItem>();
        public List<SubtitleItem> Subtitles { get; set; } = new List<SubtitleItem>();
        public List<double> Markers { get; set; } = new List<double>();
        public List<TransitionEffect> Transitions { get; set; } = new List<TransitionEffect>();
        public int CurrentTrackFilter { get; set; } = -1;
        public double ZoomLevel { get; set; } = 1.0;
        public string ProjectVersion { get; set; } = "4.3";
    }
}