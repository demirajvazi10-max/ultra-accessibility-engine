using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace UltraAudioEditor.Services
{
    public enum AiProvider { Groq, Anthropic }

    public class AnthropicService
    {
        private readonly HttpClient _http;
        private string _apiKey = "";
        private AiProvider _provider = AiProvider.Groq;

        private const string GROQ_URL = "https://api.groq.com/openai/v1/chat/completions";
        private const string GROQ_MODEL = "llama-3.3-70b-versatile";
        private const string ANTHROPIC_URL = "https://api.anthropic.com/v1/messages";
        private const string ANTHROPIC_MODEL = "claude-sonnet-4-20250514";

        public AiProvider Provider
        {
            get => _provider;
            set { _provider = value; RebuildHeaders(); }
        }

        public AnthropicService()
        {
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(120);
        }

        public void SetApiKey(string key)
        {
            _apiKey = key;
            RebuildHeaders();
        }

        private void RebuildHeaders()
        {
            _http.DefaultRequestHeaders.Clear();
            if (_provider == AiProvider.Groq)
            {
                _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            }
            else
            {
                _http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
                _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            }
        }

        public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKey);

        private async Task<string> SendAsync(string systemPrompt, string userPrompt,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            progress?.Report(10);

            string json;
            string url;

            if (_provider == AiProvider.Groq)
            {
                // OpenAI-compatible format (Groq)
                var payload = new
                {
                    model = GROQ_MODEL,
                    messages = new[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    },
                    max_tokens = 2048,
                    temperature = 0.7
                };
                json = JsonSerializer.Serialize(payload);
                url = GROQ_URL;
            }
            else
            {
                // Anthropic format
                var payload = new
                {
                    model = ANTHROPIC_MODEL,
                    max_tokens = 2048,
                    system = systemPrompt,
                    messages = new[] { new { role = "user", content = userPrompt } }
                };
                json = JsonSerializer.Serialize(payload);
                url = ANTHROPIC_URL;
            }

            progress?.Report(30);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content, ct);
            progress?.Report(70);
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            progress?.Report(90);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"API greška {(int)response.StatusCode}: {responseJson}");

            using var doc = JsonDocument.Parse(responseJson);

            string result;
            if (_provider == AiProvider.Groq)
            {
                // OpenAI format: choices[0].message.content
                result = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "Nema odgovora.";
            }
            else
            {
                // Anthropic format: content[0].text
                result = doc.RootElement
                    .GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString() ?? "Nema odgovora.";
            }

            progress?.Report(100);
            return result;
        }

        public async Task<string> TranscribeAudioAsync(string trackInfo, string language,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            string sys = "Ti si AI asistent u profesionalnom audio editoru. Odgovaraj na srpskom jeziku. Budi koncizan i stručan.";
            string prompt = $@"Korisnik ima audio projekat sa sljedećim trakama: {trackInfo}
Jezik: {language}

Napravi primjer transkripcije govora u formatu:
[HH:MM:SS.ms - HH:MM:SS.ms] Govornik: Tekst govora

Uključi ~8-10 vremenskih oznaka za snimak od 2-3 minute.
Na kraju dodaj: ukupan broj reči, prosječan tempo (reči/min), napomene o kvalitetu.";
            return await SendAsync(sys, prompt, progress, ct);
        }

        public async Task<string> AnalyzeNoiseAsync(string trackInfo, string noiseLevel,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            string sys = "Ti si ekspert za audio obradu. Odgovaraj na srpskom jeziku.";
            string prompt = $@"Korisnik želi AI uklanjanje šuma. Trake: {trackInfo}. Nivo: {noiseLevel}.

Daj izvještaj:
1. Detektovani tipovi šuma
2. Primijenjen spektralni profil (frekvencijski opsezi)
3. Parametri filtriranja (threshold, attenuation po pojasu)
4. Prognoza kvaliteta rezultata (1-10)
5. Upozorenja ako agresivna obrada može oštetiti glas
6. Preporuke za post-procesiranje";
            return await SendAsync(sys, prompt, progress, ct);
        }

        public async Task<string> SmartCutAnalysisAsync(string trackInfo, float silenceThreshDb,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            string sys = "Ti si AI editor za audio. Odgovaraj na srpskom jeziku. Budi precizan sa vremenima.";
            string prompt = $@"Analiziraj audio za SmartCut. Trake: {trackInfo}. Prag tišine: {silenceThreshDb} dB.

Generiši tablicu detektovanih segmenata (~15 primjera):
Timestamp Pocetak | Timestamp Kraj | Trajanje | RMS nivo | Preporuka: REŽI/ZADRŽI

Dodaj: ukupno trajanje za rezanje, postotak kompresije projekta, upozorenja.";
            return await SendAsync(sys, prompt, progress, ct);
        }

        public async Task<string> VocalSeparationAdviceAsync(string trackInfo,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            string sys = "Ti si audio inžinjer specijalizovan za stem separation. Odgovaraj na srpskom jeziku.";
            string prompt = $@"Korisnik želi AI separaciju vokalnih i instrumentalnih stemova. Trake: {trackInfo}

Daj uputstva:
1. Koji AI model (Demucs, Spleeter, MDX-Net) i koji je optimalan
2. Parametri separacije
3. Očekivana kvaliteta po tipu muzike
4. Koraci post-procesiranja vokalnih i instrumentalnih traka
5. Kako miksovati nazad sa optimalnim nivoima
6. Šta raditi sa bleeding efektom";
            return await SendAsync(sys, prompt, progress, ct);
        }

        public async Task<string> DescribeAudioAsync(string trackInfo, string projectInfo,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            string sys = "Ti si audio opisivač za osobe sa oštećenjem vida. Odgovaraj na srpskom jeziku. Budi detaljan.";
            string prompt = $@"Kreiraj verbalni opis audio projekta za JAWS korisnika.
Projekat: {projectInfo}
Trake: {trackInfo}

Struktura:
1. SAŽETAK PROJEKTA
2. OPIS SVAKE TRAKE (ime, tip zvuka, trajanje, karakteristike)
3. TIMELINE PREGLED (po minutama)
4. FREKVENCIJSKI PROFIL
5. RASPOLOŽENJE I ŽANR
6. PREPORUKE ZA MIKSOVANJE
7. TEHNIČKI PODACI

Koristi jasne opise bez vizualnih metafora.";
            return await SendAsync(sys, prompt, progress, ct);
        }

        public async Task<string> VocalMixAdviceAsync(string vocalTracks, string instTracks,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            string sys = "Ti si profesionalni mix inžinjer. Odgovaraj na srpskom jeziku.";
            string prompt = $@"Preporuke za miksovanje glasa na instrumental.
Vokalne trake: {vocalTracks}
Instrumentalne: {instTracks}

Daj:
GLASNOĆA: preporučeni nivoi (dB/%)
KOMPRESIJA GLASA: Attack, Release, Ratio, Threshold, Makeup gain
EQ ZA GLAS: Low cut, pojačati/smanjiti frekvencije
SIDECHAIN KOMPRESIJA: parametri
PROSTORNI EFEKTI: Reverb tip, pre-delay, stereo width";
            return await SendAsync(sys, prompt, progress, ct);
        }

        public async Task<string> EqRecommendationsAsync(string trackName, string trackType,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            string sys = "Ti si mastering inžinjer. Odgovaraj na srpskom jeziku sa konkretnim frekvencijama.";
            string prompt = $@"EQ preporuke za traku: '{trackName}' (Tip: {trackType})

NISKOFREKVENTNI DIO (20-250 Hz): [frekvencija]: [akcija] - [razlog]
SREDNJI DIO (250 Hz - 4 kHz): [frekvencija]: [akcija] - [razlog]
VISOKOFREKVENTNI DIO (4 kHz - 20 kHz): [frekvencija]: [akcija] - [razlog]
FILTERI: High Pass, Low Pass frekvencije

Budi konkretan sa dB vrijednostima.";
            return await SendAsync(sys, prompt, progress, ct);
        }

        public async Task<string> AutoLevelAnalysisAsync(string trackInfo,
            IProgress<int>? progress = null, CancellationToken ct = default)
        {
            string sys = "Ti si loudness normalizacijski stručnjak. Odgovaraj na srpskom jeziku.";
            string prompt = $@"Analiziraj nivoe glasnoće za: {trackInfo}

Daj preporuke:
1. Procijenjeni LUFS po traci
2. Ciljni LUFS za: Spotify (-14), YouTube (-14), Apple Music (-16), Radio (-23)
3. True Peak preporuke (max -1 dBTP)
4. Dynamic Range procjena
5. Koraci za target loudness
6. Limiter parametri ako su potrebni";
            return await SendAsync(sys, prompt, progress, ct);
        }
    }
}
