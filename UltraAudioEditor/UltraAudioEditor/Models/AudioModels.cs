using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace UltraAudioEditor.Models
{
    public class AudioClip : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Klip";
        public string FilePath { get; set; } = "";
        public double StartTime { get; set; }      // offset u projektu (sekunde)
        public double Duration { get; set; }        // trajanje klipa (sekunde)
        public double TrimStart { get; set; }       // od kojeg trenutka u fajlu
        public float[]? WaveformData { get; set; }  // za crtanje

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class AudioTrack : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        private string _name = "Traka";
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        private Color _color = Color.FromRgb(55, 138, 221);
        public Color Color
        {
            get => _color;
            set { _color = value; OnPropertyChanged(); OnPropertyChanged(nameof(ColorBrush)); }
        }
        public SolidColorBrush ColorBrush => new SolidColorBrush(_color);

        private bool _isMuted;
        public bool IsMuted
        {
            get => _isMuted;
            set { _isMuted = value; OnPropertyChanged(); }
        }

        private bool _isSolo;
        public bool IsSolo
        {
            get => _isSolo;
            set { _isSolo = value; OnPropertyChanged(); }
        }

        private float _volume = 0.8f;
        public float Volume
        {
            get => _volume;
            set { _volume = Math.Clamp(value, 0f, 1f); OnPropertyChanged(); }
        }

        private float _pan = 0f;
        public float Pan
        {
            get => _pan;
            set { _pan = Math.Clamp(value, -1f, 1f); OnPropertyChanged(); }
        }

        private double _height = 80;
        public double Height
        {
            get => _height;
            set { _height = value; OnPropertyChanged(); }
        }

        public TrackType Type { get; set; } = TrackType.Audio;

        private ObservableCollection<AudioClip> _clips = new();
        public ObservableCollection<AudioClip> Clips
        {
            get => _clips;
            set { _clips = value; OnPropertyChanged(); }
        }

        // Efekti
        public TrackEffects Effects { get; set; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public enum TrackType { Audio, Vocal, Instrumental, Effects, Bus }

    public class TrackEffects : INotifyPropertyChanged
    {
        private bool _reverbEnabled; public bool ReverbEnabled { get => _reverbEnabled; set { _reverbEnabled = value; OnPropertyChanged(); } }
        private float _reverbMix = 0.3f; public float ReverbMix { get => _reverbMix; set { _reverbMix = value; OnPropertyChanged(); } }
        private float _reverbRoom = 0.5f; public float ReverbRoom { get => _reverbRoom; set { _reverbRoom = value; OnPropertyChanged(); } }

        private bool _delayEnabled; public bool DelayEnabled { get => _delayEnabled; set { _delayEnabled = value; OnPropertyChanged(); } }
        private float _delayTime = 0.25f; public float DelayTime { get => _delayTime; set { _delayTime = value; OnPropertyChanged(); } }
        private float _delayFeedback = 0.3f; public float DelayFeedback { get => _delayFeedback; set { _delayFeedback = value; OnPropertyChanged(); } }

        private bool _compressorEnabled; public bool CompressorEnabled { get => _compressorEnabled; set { _compressorEnabled = value; OnPropertyChanged(); } }
        private float _compThreshold = -20f; public float CompThreshold { get => _compThreshold; set { _compThreshold = value; OnPropertyChanged(); } }
        private float _compRatio = 4f; public float CompRatio { get => _compRatio; set { _compRatio = value; OnPropertyChanged(); } }
        private float _compAttack = 10f; public float CompAttack { get => _compAttack; set { _compAttack = value; OnPropertyChanged(); } }
        private float _compRelease = 100f; public float CompRelease { get => _compRelease; set { _compRelease = value; OnPropertyChanged(); } }

        private bool _eqEnabled; public bool EqEnabled { get => _eqEnabled; set { _eqEnabled = value; OnPropertyChanged(); } }
        private float _eqLow = 0f; public float EqLow { get => _eqLow; set { _eqLow = value; OnPropertyChanged(); } }
        private float _eqMid = 0f; public float EqMid { get => _eqMid; set { _eqMid = value; OnPropertyChanged(); } }
        private float _eqHigh = 0f; public float EqHigh { get => _eqHigh; set { _eqHigh = value; OnPropertyChanged(); } }

        private bool _pitchEnabled; public bool PitchEnabled { get => _pitchEnabled; set { _pitchEnabled = value; OnPropertyChanged(); } }
        private float _pitchSemitones = 0f; public float PitchSemitones { get => _pitchSemitones; set { _pitchSemitones = value; OnPropertyChanged(); } }

        private bool _noiseGateEnabled; public bool NoiseGateEnabled { get => _noiseGateEnabled; set { _noiseGateEnabled = value; OnPropertyChanged(); } }
        private float _gateThreshold = -40f; public float GateThreshold { get => _gateThreshold; set { _gateThreshold = value; OnPropertyChanged(); } }

        private bool _bassBostEnabled; public bool BassBostEnabled { get => _bassBostEnabled; set { _bassBostEnabled = value; OnPropertyChanged(); } }
        private float _bassGain = 6f; public float BassGain { get => _bassGain; set { _bassGain = value; OnPropertyChanged(); } }

        private bool _chorusEnabled; public bool ChorusEnabled { get => _chorusEnabled; set { _chorusEnabled = value; OnPropertyChanged(); } }
        private float _chorusDepth = 0.5f; public float ChorusDepth { get => _chorusDepth; set { _chorusDepth = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class AudioProject : INotifyPropertyChanged
    {
        public string Name { get; set; } = "Novi projekat";
        public string FilePath { get; set; } = "";
        public int SampleRate { get; set; } = 44100;
        public int BitDepth { get; set; } = 24;
        public double Bpm { get; set; } = 120;
        public ObservableCollection<AudioTrack> Tracks { get; set; } = new();
        public double Duration => Tracks.Count == 0 ? 30 :
            Tracks.SelectMany(t => t.Clips).Select(c => c.StartTime + c.Duration).DefaultIfEmpty(30).Max() + 5;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
