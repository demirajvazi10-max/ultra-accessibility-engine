using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Dsp;
using UltraAudioEditor.Models;

namespace UltraAudioEditor.Services
{
    // ═══════════════════════════════════════════════════════════════════════════
    // DSP EFEKTI — svaki čuva referencu na TrackEffects i čita LIVE vrijednosti
    // ═══════════════════════════════════════════════════════════════════════════

    public class EqualizerSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private BiQuadFilter[] _lowFilters, _midFilters, _highFilters;
        private float _lastLow, _lastMid, _lastHigh;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public EqualizerSampleProvider(ISampleProvider source, TrackEffects fx)
        {
            _source = source; _fx = fx;
            int ch = source.WaveFormat.Channels, sr = source.WaveFormat.SampleRate;
            _lowFilters  = Enumerable.Range(0,ch).Select(_=>BiQuadFilter.LowShelf(sr,200f,0.7f,fx.EqLow)).ToArray();
            _midFilters  = Enumerable.Range(0,ch).Select(_=>BiQuadFilter.PeakingEQ(sr,1000f,0.7f,fx.EqMid)).ToArray();
            _highFilters = Enumerable.Range(0,ch).Select(_=>BiQuadFilter.HighShelf(sr,8000f,0.7f,fx.EqHigh)).ToArray();
            _lastLow = fx.EqLow; _lastMid = fx.EqMid; _lastHigh = fx.EqHigh;
        }

        private void RebuildIfChanged()
        {
            if (_lastLow == _fx.EqLow && _lastMid == _fx.EqMid && _lastHigh == _fx.EqHigh) return;
            int ch = WaveFormat.Channels, sr = WaveFormat.SampleRate;
            _lowFilters  = Enumerable.Range(0,ch).Select(_=>BiQuadFilter.LowShelf(sr,200f,0.7f,_fx.EqLow)).ToArray();
            _midFilters  = Enumerable.Range(0,ch).Select(_=>BiQuadFilter.PeakingEQ(sr,1000f,0.7f,_fx.EqMid)).ToArray();
            _highFilters = Enumerable.Range(0,ch).Select(_=>BiQuadFilter.HighShelf(sr,8000f,0.7f,_fx.EqHigh)).ToArray();
            _lastLow = _fx.EqLow; _lastMid = _fx.EqMid; _lastHigh = _fx.EqHigh;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.EqEnabled) return read;
            RebuildIfChanged();
            int ch = WaveFormat.Channels;
            for (int i = 0; i < read; i++)
            {
                int c = i % ch;
                float s = buffer[offset + i];
                s = _lowFilters[c].Transform(s);
                s = _midFilters[c].Transform(s);
                s = _highFilters[c].Transform(s);
                buffer[offset + i] = s;
            }
            return read;
        }
    }

    public class ReverbSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private readonly float[] _comb1, _comb2, _comb3, _comb4;
        private readonly float[] _allpass1, _allpass2;
        private int _c1, _c2, _c3, _c4, _a1, _a2;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public ReverbSampleProvider(ISampleProvider source, TrackEffects fx)
        {
            _source = source; _fx = fx;
            int sr = source.WaveFormat.SampleRate;
            // Freeschlager Moorer comb + allpass mreža
            _comb1 = new float[(int)(sr * 0.0297)];
            _comb2 = new float[(int)(sr * 0.0371)];
            _comb3 = new float[(int)(sr * 0.0411)];
            _comb4 = new float[(int)(sr * 0.0437)];
            _allpass1 = new float[(int)(sr * 0.005)];
            _allpass2 = new float[(int)(sr * 0.0017)];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.ReverbEnabled) return read;
            float fb = 0.5f + _fx.ReverbRoom * 0.35f;
            float mix = _fx.ReverbMix;
            for (int i = 0; i < read; i++)
            {
                float dry = buffer[offset + i];
                // 4 parallel comb filters
                float wet = 0;
                wet += ProcessComb(_comb1, ref _c1, dry, fb * 0.96f);
                wet += ProcessComb(_comb2, ref _c2, dry, fb * 0.93f);
                wet += ProcessComb(_comb3, ref _c3, dry, fb * 0.91f);
                wet += ProcessComb(_comb4, ref _c4, dry, fb * 0.89f);
                wet *= 0.25f;
                // 2 series allpass
                wet = ProcessAllpass(_allpass1, ref _a1, wet);
                wet = ProcessAllpass(_allpass2, ref _a2, wet);
                buffer[offset + i] = dry * (1f - mix) + wet * mix;
            }
            return read;
        }

        private static float ProcessComb(float[] buf, ref int pos, float input, float feedback)
        {
            float output = buf[pos];
            buf[pos] = input + output * feedback;
            pos = (pos + 1) % buf.Length;
            return output;
        }

        private static float ProcessAllpass(float[] buf, ref int pos, float input)
        {
            float bufout = buf[pos];
            float output = -input + bufout;
            buf[pos] = input + bufout * 0.5f;
            pos = (pos + 1) % buf.Length;
            return output;
        }
    }

    public class DelaySampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private float[] _buffer;
        private int _pos, _delaySamples;
        private float _lastDelayTime;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public DelaySampleProvider(ISampleProvider source, TrackEffects fx)
        {
            _source = source; _fx = fx;
            _lastDelayTime = fx.DelayTime;
            _delaySamples = (int)(fx.DelayTime * source.WaveFormat.SampleRate) * source.WaveFormat.Channels;
            _buffer = new float[Math.Max(_delaySamples * 2, source.WaveFormat.SampleRate * 2)];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.DelayEnabled) return read;
            // Ažuriraj delay time ako se promijenio
            if (Math.Abs(_lastDelayTime - _fx.DelayTime) > 0.001f)
            {
                _delaySamples = (int)(_fx.DelayTime * WaveFormat.SampleRate) * WaveFormat.Channels;
                _delaySamples = Math.Clamp(_delaySamples, 1, _buffer.Length - 1);
                _lastDelayTime = _fx.DelayTime;
            }
            for (int i = 0; i < read; i++)
            {
                float dry = buffer[offset + i];
                int readPos = (_pos - _delaySamples + _buffer.Length) % _buffer.Length;
                float delayed = _buffer[readPos];
                _buffer[_pos] = dry + delayed * _fx.DelayFeedback;
                _pos = (_pos + 1) % _buffer.Length;
                buffer[offset + i] = dry + delayed * 0.7f;
            }
            return read;
        }
    }

    public class CompressorSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private float _envelope;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public CompressorSampleProvider(ISampleProvider source, TrackEffects fx)
        { _source = source; _fx = fx; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.CompressorEnabled) return read;
            float threshold = (float)Math.Pow(10.0, _fx.CompThreshold / 20.0);
            float sr = WaveFormat.SampleRate;
            float attackCoeff  = (float)Math.Exp(-1.0 / (sr * _fx.CompAttack  / 1000.0));
            float releaseCoeff = (float)Math.Exp(-1.0 / (sr * _fx.CompRelease / 1000.0));
            for (int i = 0; i < read; i++)
            {
                float input = Math.Abs(buffer[offset + i]);
                float coeff = input > _envelope ? attackCoeff : releaseCoeff;
                _envelope = input + coeff * (_envelope - input);
                float gain = 1f;
                if (_envelope > threshold && _fx.CompRatio > 1f)
                {
                    float overDb = 20f * (float)Math.Log10(_envelope / threshold);
                    gain = (float)Math.Pow(10.0, -overDb * (1f - 1f / _fx.CompRatio) / 20.0);
                }
                buffer[offset + i] *= gain;
            }
            return read;
        }
    }

    public class NoiseGateSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private float _envelope;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public NoiseGateSampleProvider(ISampleProvider source, TrackEffects fx)
        { _source = source; _fx = fx; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.NoiseGateEnabled) return read;
            float threshold = (float)Math.Pow(10.0, _fx.GateThreshold / 20.0);
            float sr = WaveFormat.SampleRate;
            float attackCoeff  = (float)Math.Exp(-1.0 / (sr * 0.001));
            float releaseCoeff = (float)Math.Exp(-1.0 / (sr * 0.100));
            for (int i = 0; i < read; i++)
            {
                float input = Math.Abs(buffer[offset + i]);
                float coeff = input > _envelope ? attackCoeff : releaseCoeff;
                _envelope = input + coeff * (_envelope - input);
                if (_envelope < threshold) buffer[offset + i] = 0f;
            }
            return read;
        }
    }

    public class BassBoostSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private BiQuadFilter[] _filters;
        private float _lastGain;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public BassBoostSampleProvider(ISampleProvider source, TrackEffects fx)
        {
            _source = source; _fx = fx;
            int ch = source.WaveFormat.Channels, sr = source.WaveFormat.SampleRate;
            _filters = Enumerable.Range(0,ch).Select(_=>BiQuadFilter.LowShelf(sr,200f,0.7f,fx.BassGain)).ToArray();
            _lastGain = fx.BassGain;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.BassBostEnabled) return read;
            if (Math.Abs(_lastGain - _fx.BassGain) > 0.01f)
            {
                int ch = WaveFormat.Channels, sr = WaveFormat.SampleRate;
                _filters = Enumerable.Range(0,ch).Select(_=>BiQuadFilter.LowShelf(sr,200f,0.7f,_fx.BassGain)).ToArray();
                _lastGain = _fx.BassGain;
            }
            int channels = WaveFormat.Channels;
            for (int i = 0; i < read; i++)
                buffer[offset + i] = _filters[i % channels].Transform(buffer[offset + i]);
            return read;
        }
    }

    public class ChorusSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackEffects _fx;
        private readonly float[] _delayBuf;
        private int _pos;
        private float _lfoPhase;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public ChorusSampleProvider(ISampleProvider source, TrackEffects fx)
        {
            _source = source; _fx = fx;
            _delayBuf = new float[source.WaveFormat.SampleRate / 4];
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_fx.ChorusEnabled) return read;
            float sr = WaveFormat.SampleRate;
            float lfoInc = 2f * (float)Math.PI * 0.8f / sr;
            for (int i = 0; i < read; i++)
            {
                float dry = buffer[offset + i];
                float lfo = (float)Math.Sin(_lfoPhase);
                _lfoPhase += lfoInc;
                if (_lfoPhase > 2 * Math.PI) _lfoPhase -= 2f * (float)Math.PI;
                int delaySamples = Math.Clamp((int)(5 + _fx.ChorusDepth * 15 + lfo * _fx.ChorusDepth * 10), 1, _delayBuf.Length - 1);
                int readPos = (_pos - delaySamples + _delayBuf.Length) % _delayBuf.Length;
                _delayBuf[_pos] = dry;
                _pos = (_pos + 1) % _delayBuf.Length;
                buffer[offset + i] = (dry + _delayBuf[readPos] * 0.7f) * 0.85f;
            }
            return read;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // AUDIO ENGINE
    // ════════════════════════════════════════════════════════════════════════════

    public class AudioEngine : IDisposable
    {
        private WaveOutEvent? _waveOut;
        private MixingSampleProvider? _mixer;
        private readonly List<AudioFileReader> _readers = new();
        private bool _disposed;

        public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;
        public float MasterVolume { get; set; } = 0.8f;

        public event EventHandler? PlaybackStopped;
        public event EventHandler<double>? PositionChanged;

        private System.Timers.Timer? _posTimer;
        private double _playStartOffset;
        private DateTime _playStartTime;

        public void Play(AudioProject project, double fromPosition = 0)
        {
            Stop();
            _playStartOffset = fromPosition;
            _playStartTime   = DateTime.UtcNow;

            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(project.SampleRate, 2);
            _mixer = new MixingSampleProvider(waveFormat) { ReadFully = true };

            bool anySolo = project.Tracks.Any(t => t.IsSolo);

            foreach (var track in project.Tracks)
            {
                if (track.IsMuted) continue;
                if (anySolo && !track.IsSolo) continue;

                foreach (var clip in track.Clips)
                {
                    if (string.IsNullOrEmpty(clip.FilePath) || !File.Exists(clip.FilePath)) continue;
                    if (clip.StartTime + clip.Duration < fromPosition) continue;

                    try
                    {
                        var reader = new AudioFileReader(clip.FilePath);
                        _readers.Add(reader);

                        double skip = Math.Max(0, fromPosition - clip.StartTime) + clip.TrimStart;
                        if (skip > 0)
                            reader.CurrentTime = TimeSpan.FromSeconds(
                                Math.Min(skip, reader.TotalTime.TotalSeconds - 0.01));

                        // Stereo + Resample
                        ISampleProvider src = reader.WaveFormat.Channels == 1
                            ? (ISampleProvider)new MonoToStereoSampleProvider(reader)
                            : reader;
                        if (reader.WaveFormat.SampleRate != project.SampleRate)
                            src = new WdlResamplingSampleProvider(src, project.SampleRate);

                        // ── LIVE DSP LANAC ── svaki provider drži referencu na track.Effects
                        src = new NoiseGateSampleProvider(src, track.Effects);
                        src = new CompressorSampleProvider(src, track.Effects);
                        src = new EqualizerSampleProvider(src, track.Effects);
                        src = new BassBoostSampleProvider(src, track.Effects);
                        src = new ChorusSampleProvider(src, track.Effects);
                        src = new DelaySampleProvider(src, track.Effects);
                        src = new ReverbSampleProvider(src, track.Effects);

                        // Volume (čita live iz track.Volume)
                        var vol = new LiveVolumeSampleProvider(src, track, MasterVolume);

                        // Offset (početak klipa)
                        double delay = Math.Max(0, clip.StartTime - fromPosition);
                        ISampleProvider final = delay > 0
                            ? new OffsetSampleProvider(vol) { DelayBySamples = (int)(delay * project.SampleRate * 2) }
                            : (ISampleProvider)vol;

                        double remaining = clip.Duration - Math.Max(0, fromPosition - clip.StartTime);
                        var trimmed = new OffsetSampleProvider(final)
                            { TakeSamples = (int)(remaining * project.SampleRate * 2) };

                        _mixer.AddMixerInput(trimmed);
                    }
                    catch { }
                }
            }

            _waveOut = new WaveOutEvent { DesiredLatency = 100 };
            _waveOut.Init(_mixer);
            _waveOut.PlaybackStopped += (s, e) => PlaybackStopped?.Invoke(this, EventArgs.Empty);
            _waveOut.Play();

            _posTimer = new System.Timers.Timer(50);
            _posTimer.Elapsed += (s, e) =>
                PositionChanged?.Invoke(this, _playStartOffset + (DateTime.UtcNow - _playStartTime).TotalSeconds);
            _posTimer.Start();
        }

        public void Stop()
        {
            _posTimer?.Stop(); _posTimer?.Dispose(); _posTimer = null;
            _waveOut?.Stop(); _waveOut?.Dispose(); _waveOut = null;
            foreach (var r in _readers) try { r.Dispose(); } catch { }
            _readers.Clear();
        }

        public void Pause() { _posTimer?.Stop(); _waveOut?.Pause(); }
        public void Resume() { _waveOut?.Play(); _posTimer?.Start(); }

        // ── Statičke operacije ────────────────────────────────────────────────

        public static float[] LoadWaveformData(string filePath, int points = 2000)
        {
            try
            {
                using var reader = new AudioFileReader(filePath);
                var result = new float[points];
                int ch = reader.WaveFormat.Channels;
                int step = Math.Max(1, (int)(reader.TotalTime.TotalSeconds * reader.WaveFormat.SampleRate) / points);
                var buf = new float[step * ch];
                int idx = 0; int n;
                while (idx < points && (n = reader.Read(buf, 0, buf.Length)) > 0)
                {
                    float max = 0;
                    for (int i = 0; i < n; i++) max = Math.Max(max, Math.Abs(buf[i]));
                    result[idx++] = max;
                }
                return result;
            }
            catch { return new float[points]; }
        }

        public static void NormalizeFile(string inputPath, string outputPath, float target = 0.95f)
        {
            using var r = new AudioFileReader(inputPath);
            float peak = 0;
            var buf = new float[r.WaveFormat.SampleRate * r.WaveFormat.Channels];
            int n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
            if (peak < 0.0001f) return;
            float factor = target / peak;
            r.Position = 0;
            using var w = new WaveFileWriter(outputPath, r.WaveFormat);
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++) buf[i] = Math.Clamp(buf[i] * factor, -1f, 1f);
                w.WriteSamples(buf, 0, n);
            }
        }

        public static void ApplyFadeIn(string inputPath, string outputPath, double fadeSec)
        {
            using var r = new AudioFileReader(inputPath);
            using var w = new WaveFileWriter(outputPath, r.WaveFormat);
            int fadeSamples = (int)(fadeSec * r.WaveFormat.SampleRate) * r.WaveFormat.Channels;
            var buf = new float[4096]; int total = 0, n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                    if (total + i < fadeSamples) buf[i] *= (float)(total + i) / fadeSamples;
                total += n; w.WriteSamples(buf, 0, n);
            }
        }

        public static void ApplyFadeOut(string inputPath, string outputPath, double fadeSec)
        {
            using var r = new AudioFileReader(inputPath);
            long total = r.Length / sizeof(float);
            long fadeSamples = (long)(fadeSec * r.WaveFormat.SampleRate) * r.WaveFormat.Channels;
            long fadeStart = total - fadeSamples;
            using var w = new WaveFileWriter(outputPath, r.WaveFormat);
            var buf = new float[4096]; long pos = 0; int n;
            while ((n = r.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    long sp = pos + i;
                    if (sp >= fadeStart && fadeSamples > 0)
                        buf[i] *= Math.Max(0f, (float)(total - sp) / fadeSamples);
                }
                pos += n; w.WriteSamples(buf, 0, n);
            }
        }

        public static void ExportMixdown(AudioProject project, string outputPath,
            ExportFormat format, int bitRate = 192, Action<int>? progress = null)
        {
            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(project.SampleRate, 2);
            var mixer = new MixingSampleProvider(waveFormat) { ReadFully = true };
            bool anySolo = project.Tracks.Any(t => t.IsSolo);
            var readers = new List<AudioFileReader>();

            foreach (var track in project.Tracks)
            {
                if (track.IsMuted || (anySolo && !track.IsSolo)) continue;
                foreach (var clip in track.Clips)
                {
                    if (!File.Exists(clip.FilePath)) continue;
                    try
                    {
                        var reader = new AudioFileReader(clip.FilePath);
                        readers.Add(reader);
                        if (clip.TrimStart > 0)
                            reader.CurrentTime = TimeSpan.FromSeconds(clip.TrimStart);
                        ISampleProvider src = reader.WaveFormat.Channels == 1
                            ? (ISampleProvider)new MonoToStereoSampleProvider(reader) : reader;
                        if (reader.WaveFormat.SampleRate != project.SampleRate)
                            src = new WdlResamplingSampleProvider(src, project.SampleRate);
                        src = new NoiseGateSampleProvider(src, track.Effects);
                        src = new CompressorSampleProvider(src, track.Effects);
                        src = new EqualizerSampleProvider(src, track.Effects);
                        src = new BassBoostSampleProvider(src, track.Effects);
                        src = new ChorusSampleProvider(src, track.Effects);
                        src = new DelaySampleProvider(src, track.Effects);
                        src = new ReverbSampleProvider(src, track.Effects);
                        var vol = new VolumeSampleProvider(src) { Volume = track.Volume };
                        var offset = new OffsetSampleProvider(vol)
                        {
                            DelayBySamples = (int)(clip.StartTime * project.SampleRate * 2),
                            TakeSamples    = (int)(clip.Duration   * project.SampleRate * 2)
                        };
                        mixer.AddMixerInput(offset);
                    }
                    catch { }
                }
            }

            progress?.Invoke(10);
            var wav16 = new SampleToWaveProvider16(mixer);
            string tmp = Path.Combine(Path.GetTempPath(), $"ultraaudio_{Guid.NewGuid()}.wav");
            WaveFileWriter.CreateWaveFile(tmp, wav16);
            progress?.Invoke(60);
            try
            {
                if (format == ExportFormat.MP3)
                {
                    using var rr = new AudioFileReader(tmp);
                    using var mp3 = new NAudio.Lame.LameMP3FileWriter(outputPath, rr.WaveFormat, bitRate);
                    rr.CopyTo(mp3);
                }
                else File.Copy(tmp, outputPath, true);
            }
            finally
            {
                foreach (var rr in readers) try { rr.Dispose(); } catch { }
                progress?.Invoke(100);
                if (File.Exists(tmp)) try { File.Delete(tmp); } catch { }
            }
        }

        public void Dispose() { if (!_disposed) { Stop(); _disposed = true; } }
    }

    // Live volume — čita Volume i MasterVolume u realnom vremenu
    public class LiveVolumeSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly AudioTrack _track;
        private readonly float _masterVolume;
        public WaveFormat WaveFormat => _source.WaveFormat;

        public LiveVolumeSampleProvider(ISampleProvider source, AudioTrack track, float masterVolume)
        { _source = source; _track = track; _masterVolume = masterVolume; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            float vol = _track.Volume * _masterVolume;
            for (int i = 0; i < read; i++) buffer[offset + i] *= vol;
            return read;
        }
    }

    public enum ExportFormat { WAV, MP3, OGG, FLAC, M4A, AIFF }
}
