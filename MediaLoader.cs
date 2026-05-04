using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace UltraVideoEditor
{
    public static class MediaLoader
    {
        private static Dictionary<string, double> _durationCache = new Dictionary<string, double>();
        private static Dictionary<string, bool> _validFileCache = new Dictionary<string, bool>();

        public static async Task<double> GetMediaDurationAsync(string filePath, LibVLC libVLC)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return 5.0;

            // Provera keša
            if (_durationCache.ContainsKey(filePath))
                return _durationCache[filePath];

            try
            {
                using (var media = new Media(libVLC, filePath))
                {
                    var tcs = new TaskCompletionSource<bool>();
                    media.AddOption("play-and-exit");

                    // OVO JE BILO SPORNO - dodajte underscore da ignorišete upozorenje
                    _ = media.Parse(MediaParseOptions.ParseNetwork);

                    void OnParsed(object sender, MediaParsedChangedEventArgs args)
                    {
                        if (args.ParsedStatus == MediaParsedStatus.Done)
                        {
                            media.ParsedChanged -= OnParsed;
                            tcs.TrySetResult(true);
                        }
                    }

                    media.ParsedChanged += OnParsed;

                    // Timeout posle 5 sekundi
                    var timeout = Task.Delay(5000);
                    var completed = await Task.WhenAny(tcs.Task, timeout);

                    if (completed == timeout)
                    {
                        _durationCache[filePath] = 5.0;
                        return 5.0;
                    }

                    double duration = media.Duration / 1000.0;
                    if (duration <= 0) duration = 5.0;

                    _durationCache[filePath] = duration;
                    return duration;
                }
            }
            catch
            {
                _durationCache[filePath] = 5.0;
                return 5.0;
            }
        }

        public static bool IsValidMediaFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return false;

            if (_validFileCache.ContainsKey(filePath))
                return _validFileCache[filePath];

            string ext = Path.GetExtension(filePath).ToLower();
            bool isValid = ext == ".mp4" || ext == ".avi" || ext == ".mov" || ext == ".mkv" ||
                          ext == ".mp3" || ext == ".wav" || ext == ".m4a" || ext == ".flac" || ext == ".ogg" ||
                          ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".bmp";

            _validFileCache[filePath] = isValid;
            return isValid;
        }

        public static void ClearCache()
        {
            _durationCache.Clear();
            _validFileCache.Clear();
        }
    }
}