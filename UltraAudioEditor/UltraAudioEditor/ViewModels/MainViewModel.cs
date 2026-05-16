using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using UltraAudioEditor.Models;
using UltraAudioEditor.Services;

namespace UltraAudioEditor.ViewModels
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;
        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        { _execute = execute; _canExecute = canExecute; }
        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        { _execute = _ => execute(); _canExecute = canExecute == null ? null : _ => canExecute(); }
        public bool CanExecute(object? p) => _canExecute?.Invoke(p) ?? true;
        public void Execute(object? p) => _execute(p);
        public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        private readonly Func<T?, bool>? _canExecute;
        public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
        { _execute = execute; _canExecute = canExecute; }
        public bool CanExecute(object? p) => _canExecute?.Invoke(p is T t ? t : default) ?? true;
        public void Execute(object? p) => _execute(p is T t ? t : default);
        public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    }

    public class MainViewModel : INotifyPropertyChanged
    {
        public AudioProject Project { get; set; } = new();
        private readonly AudioEngine _engine = new();
        public readonly AnthropicService AI = new();

        private readonly Stack<string> _undoStack = new();
        private readonly Stack<string> _redoStack = new();

        static readonly string[] TrackColors = {
            "#378ADD","#1D9E75","#D85A30","#D4537E",
            "#7F77DD","#BA7517","#639922","#E24B4A",
            "#5DCAA5","#EF9F27","#97C459","#F09595"
        };

        // --- Transport ---
        private bool _isPlaying; public bool IsPlaying { get => _isPlaying; set { _isPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlayPauseLabel)); } }
        private bool _isLooping; public bool IsLooping { get => _isLooping; set { _isLooping = value; OnPropertyChanged(); } }
        private bool _isRecording; public bool IsRecording { get => _isRecording; set { _isRecording = value; OnPropertyChanged(); } }
        private double _playheadPosition; public double PlayheadPosition { get => _playheadPosition; set { _playheadPosition = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeDisplay)); } }
        public string TimeDisplay => TimeSpan.FromSeconds(PlayheadPosition).ToString(@"hh\:mm\:ss\.fff");
        public string PlayPauseLabel => IsPlaying ? "⏸  Pauziraj" : "▶  Reprodukuj";

        private float _masterVolume = 0.8f; public float MasterVolume { get => _masterVolume; set { _masterVolume = value; _engine.MasterVolume = value; OnPropertyChanged(); } }
        private double _zoomLevel = 1.0; public double ZoomLevel { get => _zoomLevel; set { _zoomLevel = Math.Clamp(value, 0.1, 20); OnPropertyChanged(); } }

        // --- Selection ---
        private AudioTrack? _selectedTrack; public AudioTrack? SelectedTrack { get => _selectedTrack; set { _selectedTrack = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelectedTrack)); } }
        public bool HasSelectedTrack => _selectedTrack != null;
        private AudioClip? _selectedClip;
        public AudioClip? SelectedClip
        {
            get => _selectedClip;
            set
            {
                _selectedClip = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedClip));
                if (value != null)
                    Announce($"Odabran klip: {value.Name}, pozicija {value.StartTime:F2}s, trajanje {value.Duration:F2}s. Pritisni Enter za postavljanje pozicije, Ctrl+strelice za pomjeranje.");
            }
        }

        // --- Status ---
        private string _statusMessage = "Spreman. Pritisnite Ctrl+I da uvezete audio fajl.";
        public string StatusMessage { get => _statusMessage; set { _statusMessage = value; OnPropertyChanged(); } }

        // --- AI Panel ---
        private string _aiResult = "Ovdje će se pojaviti AI rezultati.";
        public string AiResult { get => _aiResult; set { _aiResult = value; OnPropertyChanged(); } }
        private int _aiProgress; public int AiProgress { get => _aiProgress; set { _aiProgress = value; OnPropertyChanged(); } }
        private bool _isAiBusy; public bool IsAiBusy { get => _isAiBusy; set { _isAiBusy = value; OnPropertyChanged(); } }
        private bool _isJawsMode = false;
        public bool IsJawsMode { get => _isJawsMode; set { _isJawsMode = value; OnPropertyChanged(); } }
        private string _aiApiKey = ""; public string AiApiKey { get => _aiApiKey; set { _aiApiKey = value; AI.SetApiKey(value); OnPropertyChanged(); } }

        private bool _useGroq = true;
        public bool UseGroq
        {
            get => _useGroq;
            set
            {
                _useGroq = value;
                AI.Provider = value ? AiProvider.Groq : AiProvider.Anthropic;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UseAnthropic));
                OnPropertyChanged(nameof(ApiKeyHint));
                OnPropertyChanged(nameof(ApiKeyLink));
            }
        }
        public bool UseAnthropic { get => !_useGroq; set => UseGroq = !value; }
        public string ApiKeyHint => _useGroq
            ? "Groq API ključ — besplatno na console.groq.com"
            : "Anthropic API ključ — console.anthropic.com";
        public string ApiKeyLink => _useGroq
            ? "https://console.groq.com/keys"
            : "https://console.anthropic.com";

        private string _selectedLanguage = "Srpski"; public string SelectedLanguage { get => _selectedLanguage; set { _selectedLanguage = value; OnPropertyChanged(); } }
        private string _selectedNoiseLevel = "Srednji"; public string SelectedNoiseLevel { get => _selectedNoiseLevel; set { _selectedNoiseLevel = value; OnPropertyChanged(); } }
        private float _silenceThreshold = -40f; public float SilenceThreshold { get => _silenceThreshold; set { _silenceThreshold = value; OnPropertyChanged(); } }

        // --- Export ---
        private string _exportFormat = "WAV"; public string ExportFormat { get => _exportFormat; set { _exportFormat = value; OnPropertyChanged(); } }
        private int _exportBitRate = 192; public int ExportBitRate { get => _exportBitRate; set { _exportBitRate = value; OnPropertyChanged(); } }
        private int _exportSampleRate = 44100; public int ExportSampleRate { get => _exportSampleRate; set { _exportSampleRate = value; OnPropertyChanged(); } }

        // --- Commands ---
        public ICommand NewProjectCommand { get; }
        public ICommand ImportAudioCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand AddTrackCommand { get; }
        public ICommand RemoveTrackCommand { get; }
        public ICommand PlayPauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand RecordCommand { get; }
        public ICommand ToStartCommand { get; }
        public ICommand ToEndCommand { get; }
        public ICommand LoopCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }
        public ICommand NormalizeCommand { get; }
        public ICommand FadeInCommand { get; }
        public ICommand FadeOutCommand { get; }
        public ICommand ZoomInCommand { get; }
        public ICommand ZoomOutCommand { get; }
        public ICommand ZoomFitCommand { get; }
        // AI Commands
        public ICommand AiTranscribeCommand { get; }
        public ICommand AiNoiseRemoveCommand { get; }
        public ICommand AiSmartCutCommand { get; }
        public ICommand AiVocalSepCommand { get; }
        public ICommand AiDescribeCommand { get; }
        public ICommand AiVocalMixCommand { get; }
        public ICommand AiEqRecommendCommand { get; }
        public ICommand AiAutoLevelCommand { get; }
        public ICommand MoveTrackUpCommand { get; }
        public ICommand MoveTrackDownCommand { get; }
        public ICommand DuplicateTrackCommand { get; }
        public ICommand MuteAllCommand { get; }
        public ICommand UnmuteAllCommand { get; }
        // Clip komande
        public ICommand MoveClipLeftCommand { get; }
        public ICommand MoveClipLeftFineCommand { get; }
        public ICommand MoveClipRightCommand { get; }
        public ICommand MoveClipRightFineCommand { get; }
        public ICommand SetClipPositionCommand { get; }
        public ICommand DeleteClipCommand { get; }
        public ICommand ToggleWorkspaceModeCommand { get; }
        public ICommand SaveProjectCommand { get; }
        public ICommand SaveProjectAsCommand { get; }
        public ICommand OpenProjectCommand { get; }
        public ICommand SelectClipCommand { get; }
        public ICommand AnnounceStatusCommand { get; }
        public bool HasSelectedClip => _selectedClip != null;

        public MainViewModel()
        {
            _engine.PositionChanged += (_, pos) =>
                Application.Current?.Dispatcher.Invoke(() => PlayheadPosition = pos);
            _engine.PlaybackStopped += (_, __) =>
                Application.Current?.Dispatcher.Invoke(() => { IsPlaying = false; StatusMessage = "Reprodukcija završena."; });

            NewProjectCommand = new RelayCommand(NewProject);
            ImportAudioCommand = new RelayCommand(ImportAudio);
            ExportCommand = new RelayCommand(ExportAudio);
            AddTrackCommand = new RelayCommand(AddTrack);
            RemoveTrackCommand = new RelayCommand(RemoveTrack, () => HasSelectedTrack);
            PlayPauseCommand = new RelayCommand(PlayPause);
            StopCommand = new RelayCommand(Stop);
            RecordCommand = new RelayCommand(Record);
            ToStartCommand = new RelayCommand(() => { PlayheadPosition = 0; Announce("Na početak."); });
            ToEndCommand = new RelayCommand(() => { PlayheadPosition = Project.Duration; Announce("Na kraj."); });
            LoopCommand = new RelayCommand(() => { IsLooping = !IsLooping; Announce(IsLooping ? "Loop uključen." : "Loop isključen."); });
            UndoCommand = new RelayCommand(Undo, () => _undoStack.Count > 0);
            RedoCommand = new RelayCommand(Redo, () => _redoStack.Count > 0);
            NormalizeCommand = new RelayCommand(NormalizeSelected, () => HasSelectedTrack);
            FadeInCommand = new RelayCommand(ApplyFadeIn, () => HasSelectedTrack);
            FadeOutCommand = new RelayCommand(ApplyFadeOut, () => HasSelectedTrack);
            ZoomInCommand = new RelayCommand(() => { ZoomLevel *= 1.5; Announce($"Zoom: {ZoomLevel * 100:F0}%"); });
            ZoomOutCommand = new RelayCommand(() => { ZoomLevel /= 1.5; Announce($"Zoom: {ZoomLevel * 100:F0}%"); });
            ZoomFitCommand = new RelayCommand(() => { ZoomLevel = 1; Announce("Zoom resetovan."); });
            AiTranscribeCommand = new RelayCommand(async () => await RunAI(AiTranscribe));
            AiNoiseRemoveCommand = new RelayCommand(async () => await RunAI(AiNoiseRemove));
            AiSmartCutCommand = new RelayCommand(async () => await RunAI(AiSmartCut));
            AiVocalSepCommand = new RelayCommand(async () => await RunAI(AiVocalSep));
            AiDescribeCommand = new RelayCommand(async () => await RunAI(AiDescribe));
            AiVocalMixCommand = new RelayCommand(async () => await RunAI(AiVocalMix));
            AiEqRecommendCommand = new RelayCommand(async () => await RunAI(AiEqRecommend));
            AiAutoLevelCommand = new RelayCommand(async () => await RunAI(AiAutoLevel));
            MoveTrackUpCommand = new RelayCommand(MoveTrackUp, () => HasSelectedTrack && Project.Tracks.IndexOf(SelectedTrack!) > 0);
            MoveTrackDownCommand = new RelayCommand(MoveTrackDown, () => HasSelectedTrack && Project.Tracks.IndexOf(SelectedTrack!) < Project.Tracks.Count - 1);
            DuplicateTrackCommand = new RelayCommand(DuplicateTrack, () => HasSelectedTrack);
            MuteAllCommand = new RelayCommand(() => { foreach (var t in Project.Tracks) t.IsMuted = true; Announce("Sve trake utišane."); });
            UnmuteAllCommand = new RelayCommand(() => { foreach (var t in Project.Tracks) t.IsMuted = false; Announce("Sve trake aktivirane."); });
            MoveClipLeftCommand      = new RelayCommand(() => MoveClip(-1.0),  () => HasSelectedClip);
            MoveClipLeftFineCommand  = new RelayCommand(() => MoveClip(-0.1),  () => HasSelectedClip);
            MoveClipRightCommand     = new RelayCommand(() => MoveClip(1.0),   () => HasSelectedClip);
            MoveClipRightFineCommand = new RelayCommand(() => MoveClip(0.1),   () => HasSelectedClip);
            SetClipPositionCommand   = new RelayCommand(OpenSetClipPositionDialog, () => HasSelectedClip);
            DeleteClipCommand        = new RelayCommand(DeleteSelectedClip, () => HasSelectedClip);
            ToggleWorkspaceModeCommand = new RelayCommand(ToggleWorkspaceMode);
            SaveProjectCommand   = new RelayCommand(SaveProject);
            SaveProjectAsCommand = new RelayCommand(SaveProjectAs);
            OpenProjectCommand   = new RelayCommand(OpenProject);
            AnnounceStatusCommand = new RelayCommand(AnnounceProjectStatus);
            SelectClipCommand = new RelayCommand<object>(param =>
            {
                if (param is Models.AudioClip clip)
                {
                    var track = Project.Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
                    if (track != null) SelectClip(clip, track);
                }
            });

            // Inicijalne demo trake
            AddTrackInternal("Vokal 1", "#378ADD", TrackType.Vocal);
            AddTrackInternal("Instrumental", "#1D9E75", TrackType.Instrumental);
            AddTrackInternal("Efekti", "#D85A30", TrackType.Effects);
        }

        string GetTrackInfo() => Project.Tracks.Count == 0 ? "Nema traka." :
            string.Join("; ", Project.Tracks.Select(t =>
                $"{t.Name} ({t.Type}, {t.Clips.Count} klipova, ~{t.Clips.Sum(c => c.Duration):F1}s)"));

        public void Announce(string msg)
        {
            StatusMessage = msg;
            // AutomationProperties.SetName poziva se iz koda UI-a za JAWS live region
        }

        private void SaveState()
        {
            _undoStack.Push(System.Text.Json.JsonSerializer.Serialize(
                Project.Tracks.Select(t => new { t.Name, t.IsMuted, t.IsSolo, t.Volume, t.Pan })));
            _redoStack.Clear();
        }

        private void NewProject()
        {
            if (MessageBox.Show("Novi projekat? Sve nesnimljene izmjene biti će izgubljene.",
                    "Ultra Audio Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Stop();
            Project.Tracks.Clear();
            PlayheadPosition = 0;
            Announce("Novi projekat kreiran.");
        }

        private void ImportAudio()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Uvezi audio fajl - Ultra Audio Editor",
                Filter = "Audio fajlovi|*.wav;*.mp3;*.ogg;*.flac;*.m4a;*.aiff;*.aif|WAV|*.wav|MP3|*.mp3|Svi fajlovi|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;
            foreach (var path in dlg.FileNames)
            {
                var track = AddTrackInternal(System.IO.Path.GetFileNameWithoutExtension(path));
                track.Type = TrackType.Audio;
                var waveData = AudioEngine.LoadWaveformData(path);
                double dur = 5;
                try { using var r = new NAudio.Wave.AudioFileReader(path); dur = r.TotalTime.TotalSeconds; } catch { }
                var clip = new AudioClip
                {
                    Name = System.IO.Path.GetFileName(path),
                    FilePath = path,
                    StartTime = 0,
                    Duration = dur,
                    WaveformData = waveData
                };
                track.Clips.Add(clip);
                Announce($"Uvezen fajl: {clip.Name}, trajanje {dur:F1} sekundi. Waveform prikazan.");
                OnRebuildTrackList?.Invoke();
            }
        }

        private void ExportAudio()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Izvezi audio - Ultra Audio Editor",
                Filter = "WAV|*.wav|MP3|*.mp3|OGG|*.ogg|FLAC|*.flac|M4A|*.m4a|AIFF|*.aiff",
                FileName = Project.Name
            };
            if (dlg.ShowDialog() != true) return;
            var fmt = dlg.FilterIndex switch
            {
                2 => Services.ExportFormat.MP3,
                3 => Services.ExportFormat.OGG,
                4 => Services.ExportFormat.FLAC,
                5 => Services.ExportFormat.M4A,
                6 => Services.ExportFormat.AIFF,
                _ => Services.ExportFormat.WAV
            };
            Announce("Izvoz u toku...");
            Task.Run(() =>
            {
                AudioEngine.ExportMixdown(Project, dlg.FileName, fmt, _exportBitRate,
                    pct => Application.Current?.Dispatcher.Invoke(() => AiProgress = pct));
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    Announce($"Izvoz završen: {dlg.FileName}");
                    MessageBox.Show($"Audio uspješno izvezen:\n{dlg.FileName}", "Izvoz završen",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                });
            });
        }

        public AudioTrack AddTrackInternal(string? name = null, string? colorHex = null, TrackType type = TrackType.Audio)
        {
            SaveState();
            int idx = Project.Tracks.Count;
            var color = colorHex ?? TrackColors[idx % TrackColors.Length];
            var wc = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
            var track = new AudioTrack
            {
                Name = name ?? $"Traka {idx + 1}",
                Color = wc,
                Type = type
            };
            Project.Tracks.Add(track);
            SelectedTrack = track;
            Announce($"Dodana traka: {track.Name}. Ukupno {Project.Tracks.Count} traka.");
            return track;
        }

        private void AddTrack() => AddTrackInternal();

        private void RemoveTrack()
        {
            if (SelectedTrack == null) return;
            SaveState();
            string name = SelectedTrack.Name;
            Project.Tracks.Remove(SelectedTrack);
            SelectedTrack = Project.Tracks.LastOrDefault();
            Announce($"Traka '{name}' obrisana. Preostalo {Project.Tracks.Count} traka.");
        }

        private void PlayPause()
        {
            if (IsPlaying) { _engine.Pause(); IsPlaying = false; Announce($"Pauzirano na {TimeDisplay}."); }
            else
            {
                _engine.Play(Project, PlayheadPosition);
                IsPlaying = true;
                Announce("Reprodukcija pokrenuta.");
            }
        }

        private void Stop()
        {
            _engine.Stop();
            IsPlaying = false;
            PlayheadPosition = 0;
            Announce("Zaustavljeno. Pozicija na početku.");
        }

        private void Record()
        {
            Announce("Snimanje: ova funkcija zahtijeva podešavanje audio ulaza. Koristite Settings da odaberete mikrofon.");
        }

        private void Undo()
        {
            if (_undoStack.Count == 0) { Announce("Ništa za poništiti."); return; }
            _redoStack.Push(_undoStack.Pop());
            Announce("Poništeno.");
        }

        private void Redo()
        {
            if (_redoStack.Count == 0) { Announce("Ništa za ponavljati."); return; }
            _undoStack.Push(_redoStack.Pop());
            Announce("Ponovljeno.");
        }

        private void NormalizeSelected()
        {
            if (SelectedTrack?.Clips.Count == 0) { Announce("Nema audio sadržaja za normalizaciju."); return; }
            var clip = SelectedTrack!.Clips.First();
            if (!System.IO.File.Exists(clip.FilePath)) { Announce("Fajl nije pronađen."); return; }
            Announce("Normalizacija u toku...");
            Task.Run(() =>
            {
                string temp = System.IO.Path.GetTempFileName() + ".wav";
                AudioEngine.NormalizeFile(clip.FilePath, temp);
                clip.FilePath = temp;
                clip.WaveformData = AudioEngine.LoadWaveformData(temp);
                Application.Current?.Dispatcher.Invoke(() => Announce("Normalizacija završena."));
            });
        }

        private void ApplyFadeIn()
        {
            if (SelectedTrack?.Clips.FirstOrDefault()?.FilePath is not string path) { Announce("Nema audio za fade."); return; }
            Task.Run(() =>
            {
                string temp = System.IO.Path.GetTempFileName() + ".wav";
                AudioEngine.ApplyFadeIn(path, temp, 2.0);
                SelectedTrack.Clips.First().FilePath = temp;
                Application.Current?.Dispatcher.Invoke(() => Announce("Fade in primijenjen (2 sekunde)."));
            });
        }

        private void ApplyFadeOut()
        {
            if (SelectedTrack?.Clips.FirstOrDefault()?.FilePath is not string path) { Announce("Nema audio za fade."); return; }
            Task.Run(() =>
            {
                string temp = System.IO.Path.GetTempFileName() + ".wav";
                AudioEngine.ApplyFadeOut(path, temp, 2.0);
                SelectedTrack.Clips.First().FilePath = temp;
                Application.Current?.Dispatcher.Invoke(() => Announce("Fade out primijenjen (2 sekunde)."));
            });
        }

        private void MoveTrackUp()
        {
            if (SelectedTrack == null) return;
            int i = Project.Tracks.IndexOf(SelectedTrack);
            if (i > 0) { Project.Tracks.Move(i, i - 1); Announce($"Traka '{SelectedTrack.Name}' premještena gore."); }
        }

        private void MoveTrackDown()
        {
            if (SelectedTrack == null) return;
            int i = Project.Tracks.IndexOf(SelectedTrack);
            if (i < Project.Tracks.Count - 1) { Project.Tracks.Move(i, i + 1); Announce($"Traka '{SelectedTrack.Name}' premještena dole."); }
        }

        private void DuplicateTrack()
        {
            if (SelectedTrack == null) return;
            var orig = SelectedTrack;
            var dup = AddTrackInternal(orig.Name + " (kopija)", "#" + orig.Color.R.ToString("X2") + orig.Color.G.ToString("X2") + orig.Color.B.ToString("X2"), orig.Type);
            dup.Volume = orig.Volume;
            dup.Pan = orig.Pan;
            foreach (var c in orig.Clips)
                dup.Clips.Add(new AudioClip { Name = c.Name, FilePath = c.FilePath, StartTime = c.StartTime, Duration = c.Duration, WaveformData = c.WaveformData });
            Announce($"Traka '{orig.Name}' duplicirana.");
        }

        private async Task RunAI(Func<Task> aiFunc)
        {
            if (!AI.HasApiKey)
            {
                string providerName = UseGroq ? "Groq (besplatno)" : "Anthropic";
                string link = UseGroq ? "console.groq.com" : "console.anthropic.com";
                Announce($"Unesite {providerName} API ključ u AI panelu.");
                MessageBox.Show(
                    $"Molimo unesite {providerName} API ključ u polju 'API Ključ' u desnom panelu.\n\nDobijte besplatni ključ na: {link}",
                    "API ključ potreban", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            IsAiBusy = true;
            AiProgress = 0;
            try { await aiFunc(); }
            catch (Exception ex) { AiResult = $"Greška: {ex.Message}"; Announce("AI greška. Pogledaj AI panel."); }
            finally { IsAiBusy = false; }
        }

        private async Task AiTranscribe()
        {
            Announce("AI transkripcija pokrenuta...");
            var prog = new Progress<int>(p => { AiProgress = p; });
            AiResult = await AI.TranscribeAudioAsync(GetTrackInfo(), SelectedLanguage, prog);
            Announce("Transkripcija završena. Rezultati u AI panelu.");
        }

        private async Task AiNoiseRemove()
        {
            Announce("AI analiza šuma pokrenuta...");
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.AnalyzeNoiseAsync(GetTrackInfo(), SelectedNoiseLevel, prog);
            Announce("Analiza šuma završena. Rezultati u AI panelu.");
        }

        private async Task AiSmartCut()
        {
            Announce("AI SmartCut analiza pokrenuta...");
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.SmartCutAnalysisAsync(GetTrackInfo(), SilenceThreshold, prog);
            Announce("SmartCut analiza završena. Prijedlozi u AI panelu.");
        }

        private async Task AiVocalSep()
        {
            Announce("AI savjeti za vokalnu separaciju...");
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.VocalSeparationAdviceAsync(GetTrackInfo(), prog);
            Announce("Savjeti za vokalnu separaciju dostupni u AI panelu.");
        }

        private async Task AiDescribe()
        {
            Announce("AI kreira verbalni opis projekta...");
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.DescribeAudioAsync(GetTrackInfo(), $"Projekat: {Project.Name}, {Project.Tracks.Count} traka, {Project.SampleRate}Hz/{Project.BitDepth}bit", prog);
            Announce("Audio opis kreiran. Dostupan u AI panelu.");
        }

        private async Task AiVocalMix()
        {
            var vocals = string.Join(", ", Project.Tracks.Where(t => t.Type == TrackType.Vocal).Select(t => t.Name));
            var inst = string.Join(", ", Project.Tracks.Where(t => t.Type == TrackType.Instrumental).Select(t => t.Name));
            Announce("AI preporuke za vocal mix...");
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.VocalMixAdviceAsync(vocals.Length > 0 ? vocals : "nije definirano", inst.Length > 0 ? inst : "nije definirano", prog);
            Announce("Vocal mix preporuke dostupne u AI panelu.");
        }

        private async Task AiEqRecommend()
        {
            if (SelectedTrack == null) { Announce("Odaberite traku za EQ preporuke."); return; }
            Announce($"AI EQ preporuke za traku: {SelectedTrack.Name}...");
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.EqRecommendationsAsync(SelectedTrack.Name, SelectedTrack.Type.ToString(), prog);
            Announce("EQ preporuke dostupne u AI panelu.");
        }

        private async Task AiAutoLevel()
        {
            Announce("AI analiza nivoa...");
            var prog = new Progress<int>(p => AiProgress = p);
            AiResult = await AI.AutoLevelAnalysisAsync(GetTrackInfo(), prog);
            Announce("Analiza nivoa završena. Preporuke u AI panelu.");
        }


        // ─── Clip pozicioniranje ─────────────────────────────────────────────

        /// Pomjeri odabrani klip za delta sekundi (pozitivno = desno, negativno = lijevo)
        private void MoveClip(double deltaSec)
        {
            if (_selectedClip == null) return;
            SaveState();
            _selectedClip.StartTime = Math.Max(0, _selectedClip.StartTime + deltaSec);
            Announce($"Klip '{_selectedClip.Name}' pomjeren na {_selectedClip.StartTime:F2} sekundi.");
            // Osvježi waveform prikaz
            foreach (var track in Project.Tracks)
                track.Clips = track.Clips; // trigger PropertyChanged
        }

        /// Otvori dialog za precizno postavljanje pozicije klipa
        public void OpenSetClipPositionDialog()
        {
            if (_selectedClip == null) return;
            var dlg = new Views.SetClipPositionDialog(_selectedClip.StartTime, _selectedClip.Name);
            if (dlg.ShowDialog() == true)
            {
                SaveState();
                double newPos = dlg.ResultSeconds;
                _selectedClip.StartTime = Math.Max(0, newPos);
                Announce($"Klip '{_selectedClip.Name}' postavljen na {_selectedClip.StartTime:F2} sekundi.");
            }
        }

        private void DeleteSelectedClip()
        {
            if (_selectedClip == null) return;
            SaveState();
            string name = _selectedClip.Name;
            foreach (var track in Project.Tracks)
                track.Clips.Remove(_selectedClip);
            SelectedClip = null;
            Announce($"Klip '{name}' obrisan.");
        }


        // ─── Save / Load Projekta ────────────────────────────────────────────

        private void SaveProject()
        {
            if (string.IsNullOrEmpty(Project.FilePath))
                SaveProjectAs();
            else
                DoSave(Project.FilePath);
        }

        private void SaveProjectAs()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Sačuvaj projekat — Ultra Audio Editor",
                Filter = "Ultra Audio projekat|*.paproj|Svi fajlovi|*.*",
                FileName = Project.Name,
                DefaultExt = ".paproj"
            };
            if (dlg.ShowDialog() != true) return;
            Project.FilePath = dlg.FileName;
            Project.Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            DoSave(dlg.FileName);
        }

        private void DoSave(string path)
        {
            try
            {
                var data = new ProjectSaveData
                {
                    Name       = Project.Name,
                    SampleRate = Project.SampleRate,
                    BitDepth   = Project.BitDepth,
                    Bpm        = Project.Bpm,
                    Tracks     = Project.Tracks.Select(t => new TrackSaveData
                    {
                        Id      = t.Id,
                        Name    = t.Name,
                        Type    = t.Type.ToString(),
                        Volume  = t.Volume,
                        Pan     = t.Pan,
                        IsMuted = t.IsMuted,
                        IsSolo  = t.IsSolo,
                        ColorHex = $"#{t.Color.R:X2}{t.Color.G:X2}{t.Color.B:X2}",
                        Clips   = t.Clips.Select(c => new ClipSaveData
                        {
                            Id        = c.Id,
                            Name      = c.Name,
                            FilePath  = c.FilePath,
                            StartTime = c.StartTime,
                            Duration  = c.Duration,
                            TrimStart = c.TrimStart
                        }).ToList(),
                        Effects = new EffectsSaveData
                        {
                            EqEnabled        = t.Effects.EqEnabled,
                            EqLow            = t.Effects.EqLow,
                            EqMid            = t.Effects.EqMid,
                            EqHigh           = t.Effects.EqHigh,
                            ReverbEnabled    = t.Effects.ReverbEnabled,
                            ReverbMix        = t.Effects.ReverbMix,
                            ReverbRoom       = t.Effects.ReverbRoom,
                            DelayEnabled     = t.Effects.DelayEnabled,
                            DelayTime        = t.Effects.DelayTime,
                            DelayFeedback    = t.Effects.DelayFeedback,
                            CompressorEnabled= t.Effects.CompressorEnabled,
                            CompThreshold    = t.Effects.CompThreshold,
                            CompRatio        = t.Effects.CompRatio,
                            CompAttack       = t.Effects.CompAttack,
                            CompRelease      = t.Effects.CompRelease,
                            PitchEnabled     = t.Effects.PitchEnabled,
                            PitchSemitones   = t.Effects.PitchSemitones,
                            NoiseGateEnabled = t.Effects.NoiseGateEnabled,
                            GateThreshold    = t.Effects.GateThreshold,
                            BassBostEnabled  = t.Effects.BassBostEnabled,
                            BassGain         = t.Effects.BassGain,
                            ChorusEnabled    = t.Effects.ChorusEnabled,
                            ChorusDepth      = t.Effects.ChorusDepth
                        }
                    }).ToList()
                };
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(path, json, System.Text.Encoding.UTF8);
                Announce($"Projekat sacuvan: {System.IO.Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Greska pri cuvanju:\n{ex.Message}", "Greska", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenProject()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Otvori projekat — Ultra Audio Editor",
                Filter = "Ultra Audio projekat|*.paproj|Svi fajlovi|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string json = System.IO.File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                var data = Newtonsoft.Json.JsonConvert.DeserializeObject<ProjectSaveData>(json);
                if (data == null) throw new Exception("Neispravan format fajla.");
                Stop();
                Project.Tracks.Clear();
                Project.Name     = data.Name;
                Project.FilePath = dlg.FileName;
                Project.SampleRate = data.SampleRate;
                Project.BitDepth   = data.BitDepth;
                Project.Bpm        = data.Bpm;
                foreach (var td in data.Tracks)
                {
                    var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(td.ColorHex ?? "#378ADD");
                    var track = new Models.AudioTrack
                    {
                        Id      = td.Id,
                        Name    = td.Name,
                        Color   = color,
                        Volume  = td.Volume,
                        Pan     = td.Pan,
                        IsMuted = td.IsMuted,
                        IsSolo  = td.IsSolo,
                        Type    = Enum.TryParse<Models.TrackType>(td.Type, out var tt) ? tt : Models.TrackType.Audio
                    };
                    if (td.Effects != null)
                    {
                        var fx = track.Effects;
                        fx.EqEnabled = td.Effects.EqEnabled; fx.EqLow = td.Effects.EqLow; fx.EqMid = td.Effects.EqMid; fx.EqHigh = td.Effects.EqHigh;
                        fx.ReverbEnabled = td.Effects.ReverbEnabled; fx.ReverbMix = td.Effects.ReverbMix; fx.ReverbRoom = td.Effects.ReverbRoom;
                        fx.DelayEnabled = td.Effects.DelayEnabled; fx.DelayTime = td.Effects.DelayTime; fx.DelayFeedback = td.Effects.DelayFeedback;
                        fx.CompressorEnabled = td.Effects.CompressorEnabled; fx.CompThreshold = td.Effects.CompThreshold;
                        fx.CompRatio = td.Effects.CompRatio; fx.CompAttack = td.Effects.CompAttack; fx.CompRelease = td.Effects.CompRelease;
                        fx.PitchEnabled = td.Effects.PitchEnabled; fx.PitchSemitones = td.Effects.PitchSemitones;
                        fx.NoiseGateEnabled = td.Effects.NoiseGateEnabled; fx.GateThreshold = td.Effects.GateThreshold;
                        fx.BassBostEnabled = td.Effects.BassBostEnabled; fx.BassGain = td.Effects.BassGain;
                        fx.ChorusEnabled = td.Effects.ChorusEnabled; fx.ChorusDepth = td.Effects.ChorusDepth;
                    }
                    foreach (var cd in td.Clips)
                    {
                        var clip = new Models.AudioClip
                        {
                            Id        = cd.Id,
                            Name      = cd.Name,
                            FilePath  = cd.FilePath,
                            StartTime = cd.StartTime,
                            Duration  = cd.Duration,
                            TrimStart = cd.TrimStart
                        };
                        if (System.IO.File.Exists(cd.FilePath))
                            clip.WaveformData = Services.AudioEngine.LoadWaveformData(cd.FilePath);
                        track.Clips.Add(clip);
                    }
                    Project.Tracks.Add(track);
                }
                PlayheadPosition = 0;
                Announce($"Projekat otvoren: {Project.Name}. {Project.Tracks.Count} traka ucitano.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Greska pri otvaranju:\n{ex.Message}", "Greska", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public Action? OnToggleWorkspaceMode { get; set; }
        public Action? OnRebuildTrackList { get; set; }

        /// F6 — čita kompletan status projekta naglas
        public void AnnounceProjectStatus()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"PROJEKAT: {Project.Name}");
            sb.AppendLine($"Playhead: {TimeDisplay} ({PlayheadPosition:F3}s)");
            sb.AppendLine($"Trake: {Project.Tracks.Count}");
            foreach (var track in Project.Tracks)
            {
                sb.AppendLine($"");
                sb.AppendLine($"TRAKA: {track.Name} | Vol: {track.Volume:P0} | Mute: {(track.IsMuted?"Da":"Ne")} | Solo: {(track.IsSolo?"Da":"Ne")}");
                if (track.Clips.Count == 0)
                    sb.AppendLine("  Nema klipova.");
                else
                    foreach (var clip in track.Clips)
                        sb.AppendLine($"  Klip: {clip.Name} | Pocinje: {clip.StartTime:F3}s | Traje: {clip.Duration:F3}s | Kraj: {(clip.StartTime+clip.Duration):F3}s");
            }
            var msg = sb.ToString();
            Announce(msg);
            // Otvori status prozor
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var win = new Views.ProjectStatusWindow(msg);
                win.Show();
            });
        }
        public void ToggleWorkspaceMode()
        {
            OnToggleWorkspaceMode?.Invoke();
        }

        /// Pozovi iz code-behind kada korisnik klikne na klip blok
        public void SelectClip(AudioClip clip, AudioTrack track)
        {
            SelectedTrack = track;
            SelectedClip = clip;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

// ─── Save/Load DTO klase ────────────────────────────────────────────────────
namespace UltraAudioEditor.ViewModels
{
    public class ProjectSaveData
    {
        public string Name { get; set; } = "";
        public int SampleRate { get; set; } = 44100;
        public int BitDepth { get; set; } = 24;
        public double Bpm { get; set; } = 120;
        public List<TrackSaveData> Tracks { get; set; } = new();
    }
    public class TrackSaveData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "Audio";
        public float Volume { get; set; } = 0.8f;
        public float Pan { get; set; }
        public bool IsMuted { get; set; }
        public bool IsSolo { get; set; }
        public string? ColorHex { get; set; }
        public List<ClipSaveData> Clips { get; set; } = new();
        public EffectsSaveData? Effects { get; set; }
    }
    public class ClipSaveData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public double StartTime { get; set; }
        public double Duration { get; set; }
        public double TrimStart { get; set; }
    }
    public class EffectsSaveData
    {
        public bool EqEnabled { get; set; }
        public float EqLow { get; set; }
        public float EqMid { get; set; }
        public float EqHigh { get; set; }
        public bool ReverbEnabled { get; set; }
        public float ReverbMix { get; set; }
        public float ReverbRoom { get; set; }
        public bool DelayEnabled { get; set; }
        public float DelayTime { get; set; }
        public float DelayFeedback { get; set; }
        public bool CompressorEnabled { get; set; }
        public float CompThreshold { get; set; }
        public float CompRatio { get; set; }
        public float CompAttack { get; set; }
        public float CompRelease { get; set; }
        public bool PitchEnabled { get; set; }
        public float PitchSemitones { get; set; }
        public bool NoiseGateEnabled { get; set; }
        public float GateThreshold { get; set; }
        public bool BassBostEnabled { get; set; }
        public float BassGain { get; set; }
        public bool ChorusEnabled { get; set; }
        public float ChorusDepth { get; set; }
    }
}
