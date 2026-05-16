using System.Globalization;

namespace UltraVideoEditor
{
    public static class VideoEffects
    {
        public static string GetFFmpegFilter(VideoEffectData effect)
        {
            if (effect == null) return "";
            string filter = "";

            if (effect.Brightness != 0 || effect.Contrast != 0)
            {
                double brightness = effect.Brightness / 100.0;
                double contrast = effect.Contrast / 100.0 + 1.0;
                filter += $"eq=brightness={brightness.ToString(CultureInfo.InvariantCulture)}:contrast={contrast.ToString(CultureInfo.InvariantCulture)},";
            }
            if (effect.Blur > 0) filter += $"boxblur={effect.Blur.ToString(CultureInfo.InvariantCulture)},";
            if (effect.Speed != 1.0) filter += $"setpts={(1.0 / effect.Speed).ToString(CultureInfo.InvariantCulture)}*PTS,";

            return filter.TrimEnd(',');
        }
    }

    public static class AudioEffects
    {
        public static string GetFFmpegFilter(AudioSettingsData settings)
        {
            if (settings == null) return "";
            string filter = "";

            // Bass boost (100Hz) i Treble boost (4000Hz) koristeći equalizer filter
            if (settings.BassBoost > 0)
            {
                double bassGain = settings.BassBoost / 5.0; // 0-20 -> 0-4
                filter += $"equalizer=frequency=100:width_type=o:width=1:gain={bassGain.ToString(CultureInfo.InvariantCulture)},";
            }

            if (settings.TrebleBoost > 0)
            {
                double trebleGain = settings.TrebleBoost / 5.0; // 0-20 -> 0-4
                filter += $"equalizer=frequency=4000:width_type=o:width=1:gain={trebleGain.ToString(CultureInfo.InvariantCulture)},";
            }

            // Reverb efekat
            if (settings.ReverbAmount > 0)
            {
                double reverbDelay = 0.3 + (settings.ReverbAmount / 100) * 0.5;
                filter += $"aecho=0.8:{reverbDelay.ToString(CultureInfo.InvariantCulture)}:40:0.3,";
            }

            // Kompresor
            if (settings.CompressorEnabled)
            {
                filter += $"acompressor=threshold=0.1:ratio=3:attack=5:release=50,";
            }

            return filter.TrimEnd(',');
        }
    }
}