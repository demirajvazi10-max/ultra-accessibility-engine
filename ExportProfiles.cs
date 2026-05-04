using System.Collections.Generic;

namespace UltraVideoEditor
{
    public class ExportProfile
    {
        public string Name { get; set; }
        public string Resolution { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Bitrate { get; set; }
        public int FrameRate { get; set; }
        public string AudioCodec { get; set; }
    }

    public static class ExportProfiles
    {
        public static List<ExportProfile> GetProfiles()
        {
            return new List<ExportProfile>
            {
                new ExportProfile { Name = "YouTube (1080p)", Resolution = "1920x1080", Width = 1920, Height = 1080, Bitrate = 8000, FrameRate = 30, AudioCodec = "aac" },
                new ExportProfile { Name = "YouTube (720p)", Resolution = "1280x720", Width = 1280, Height = 720, Bitrate = 5000, FrameRate = 30, AudioCodec = "aac" },
                new ExportProfile { Name = "TikTok/Reels (9:16)", Resolution = "1080x1920", Width = 1080, Height = 1920, Bitrate = 6000, FrameRate = 30, AudioCodec = "aac" },
                new ExportProfile { Name = "Instagram (1:1)", Resolution = "1080x1080", Width = 1080, Height = 1080, Bitrate = 5000, FrameRate = 30, AudioCodec = "aac" },
                new ExportProfile { Name = "Visoka kvaliteta (4K)", Resolution = "3840x2160", Width = 3840, Height = 2160, Bitrate = 20000, FrameRate = 60, AudioCodec = "aac" },
                new ExportProfile { Name = "Samo audio (MP3)", Resolution = "0x0", Width = 0, Height = 0, Bitrate = 320, FrameRate = 0, AudioCodec = "mp3" }
            };
        }
    }
}