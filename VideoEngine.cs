using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // MODELI - JSON kompatibilni sa GPT formatom
    // ═══════════════════════════════════════════════════════════════

    public class ShotData
    {
        [JsonPropertyName("search_query")]
        public string SearchQuery       { get; set; }   // Pixabay API query

        [JsonPropertyName("shot_type")]
        public string ShotType          { get; set; }   // Wide Shot / Medium Shot / Close Up

        [JsonPropertyName("motion_intent")]
        public string MotionIntent      { get; set; }   // tracking, panning, zoom in...

        [JsonPropertyName("vibe_score")]
        public int    VibeScore         { get; set; }   // 1-10

        [JsonPropertyName("visual_bridge")]
        public string VisualBridge      { get; set; }   // vizuelni most sa prethodnim

        // ── 5 CINEMATIC PARAMETARA ─────────────────────────────────

        // 1. Color Psychology - dominantna boja scene
        [JsonPropertyName("dominant_color")]
        public string DominantColor     { get; set; }   // npr. "warm golden yellow"

        [JsonPropertyName("color_hex")]
        public string ColorHex          { get; set; }   // npr. "#F5C842"

        [JsonPropertyName("color_purpose")]
        public string ColorPurpose      { get; set; }   // zašto baš ta boja

        // 2. Kompozicija - pravilo trećina
        [JsonPropertyName("composition")]
        public string Composition       { get; set; }   // "subject left third", "centered"

        [JsonPropertyName("composition_note")]
        public string CompositionNote   { get; set; }   // detaljna instrukcija

        // 3. Mikro-tranzicija
        [JsonPropertyName("transition_type")]
        public string TransitionType    { get; set; }   // "match_cut", "cut", "dissolve"

        [JsonPropertyName("transition_note")]
        public string TransitionNote    { get; set; }   // opis match cut-a

        // 4. Audio-Visual Sync - vizuelni akcenti
        [JsonPropertyName("visual_accent")]
        public string VisualAccent      { get; set; }   // ključna riječ za akcent

        [JsonPropertyName("accent_action")]
        public string AccentAction      { get; set; }   // šta se dešava u videu

        // 5. Cinematic query (prošireni sa kompozicijom)
        [JsonPropertyName("cinematic_query")]
        public string CinematicQuery    { get; set; }   // finalni Pixabay query
    }

    public class LyricShot
    {
        [JsonPropertyName("stih")]
        public string Lyric         { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp     { get; set; }

        [JsonPropertyName("data")]
        public ShotData Data        { get; set; }

        // Interna polja (ne serializuju se)
        [JsonIgnore] public double StartSeconds { get; set; }
        [JsonIgnore] public double EndSeconds   { get; set; }
        [JsonIgnore] public double Duration     => EndSeconds - StartSeconds;
        [JsonIgnore] public bool   IsChorus     { get; set; }
        [JsonIgnore] public int    LyricIndex   { get; set; }
    }

    public class VideoIntent
    {
        public string Type  { get; set; } = "emotional";
        public string Style { get; set; } = "cinematic";
        public string Pace  { get; set; } = "auto";
    }

    // ═══════════════════════════════════════════════════════════════
    // GENERIČKI VIDEO ENGINE - radi za bilo koju pesmu
    // ═══════════════════════════════════════════════════════════════

    public static class VideoEngine
    {
        // Rotacija shot tipova - garantovana varijacija, nikad 2 ista zaredom
        private static readonly string[] ShotRotation = 
            { "Wide Shot", "Medium Shot", "Close Up" };

        // Cinematic deskriptori po vibe_score
        private static string VibeToDescriptor(int vibe) => vibe switch
        {
            >= 9 => "slow motion high energy cinematic bokeh",
            >= 7 => "dynamic cinematic high quality bokeh",
            >= 5 => "cinematic slow motion golden hour",
            >= 3 => "peaceful cinematic slow motion bokeh",
            _    => "gentle slow motion cinematic soft focus"
        };

        // Motion intent po shot tipu i energiji
        private static string GetMotion(string shotType, int vibe, int index) => shotType switch
        {
            "Wide Shot"   => vibe >= 7 
                ? new[]{"aerial drone slow pan","slow pan right revealing landscape",
                        "tracking shot wide angle"}[index % 3]
                : new[]{"slow pan right","gentle tilt up","slow pull back"}[index % 3],
            "Close Up"    => vibe >= 7
                ? new[]{"slow zoom in on face","subject towards camera laughing",
                        "slow zoom in detail"}[index % 3]
                : new[]{"slow zoom in gentle","soft focus close detail",
                        "gentle tilt down to hands"}[index % 3],
            _             => // Medium Shot
                vibe >= 7
                ? new[]{"tracking shot subject walking","subject turns to camera",
                        "medium pan following action"}[index % 3]
                : new[]{"subject walking towards camera","gentle medium pan",
                        "slow tilt up medium frame"}[index % 3]
        };

        // ─────────────────────────────────────────────────────────────
        // GLAVNI METOD: Generiši shot listu iz stihova
        // Input:  lista stihova + ukupno trajanje
        // Output: lista LyricShot objekata spremnnih za Pixabay
        // ─────────────────────────────────────────────────────────────
        public static List<LyricShot> GenerateFromLyrics(
            List<string> lyrics,
            double totalDurationSeconds,
            VideoIntent intent,
            List<string> chorusLines = null)  // koje linije su refren (za tematski kontinuitet)
        {
            if (lyrics == null || lyrics.Count == 0)
                return new List<LyricShot>();

            // Izračunaj trajanje po stihu
            double perLyric = Math.Round(totalDurationSeconds / lyrics.Count, 2);
            var    shots    = new List<LyricShot>();
            string prevShotType = "";
            int    rotationIdx  = 0;

            // Detektuj refrene ako nisu eksplicitno navedeni
            var chorusSet = new HashSet<string>(
                chorusLines ?? DetectChorusLines(lyrics),
                StringComparer.OrdinalIgnoreCase);

            // Query za refren - isti vizuelni jezik kroz cijelu pesmu
            string chorusThemeQuery = BuildChorusQuery(intent);

            for (int i = 0; i < lyrics.Count; i++)
            {
                string lyric     = lyrics[i].Trim();
                bool   isChorus  = chorusSet.Contains(lyric) ||
                                   (lyric.Length > 3 && chorusSet.Any(c =>
                                       c.Contains(lyric) || lyric.Contains(c.Split(' ')[0])));

                double start = Math.Round(i * perLyric, 2);
                double end   = Math.Round((i + 1) * perLyric, 2);
                if (i == lyrics.Count - 1) end = totalDurationSeconds;

                // vibe_score iz pozicije u pesmi i tipa stiha
                int vibe = CalculateVibe(i, lyrics.Count, isChorus, intent);

                // Shot rotacija - NIKAD isti zaredom
                string shotType = GetNextShotType(ref rotationIdx, prevShotType, vibe, isChorus);
                prevShotType = shotType;

                // Search query: refren = tema, strofa = iz teksta stiha
                string searchQuery = isChorus
                    ? chorusThemeQuery
                    : BuildVerseQuery(lyric, intent, vibe, i, lyrics.Count);

                // Visual bridge
                string bridge = BuildVisualBridge(shotType, isChorus, i, vibe, intent);

                shots.Add(new LyricShot
                {
                    Lyric        = lyric,
                    Timestamp    = $"{FormatTs(start)} - {FormatTs(end)}",
                    StartSeconds = start,
                    EndSeconds   = end,
                    IsChorus     = isChorus,
                    LyricIndex   = i,
                    Data = new ShotData
                    {
                        SearchQuery  = searchQuery,
                        ShotType     = shotType,
                        MotionIntent = GetMotion(shotType, vibe, i),
                        VibeScore    = vibe,
                        VisualBridge = bridge
                    }
                });
            }

            return shots;
        }

        // ─────────────────────────────────────────────────────────────
        // ALTERNATIVNI METOD: Ollama generiše JSON za svaki stih
        // Program samo validilja i popunjava praznine
        // ─────────────────────────────────────────────────────────────
        public static List<LyricShot> MergeWithAIOutput(
            List<string> lyrics,
            double totalDuration,
            string ollamaJsonResponse)
        {
            // Pokušaj parsirati Ollama output
            List<LyricShot> aiShots = null;
            try
            {
                int s = ollamaJsonResponse.IndexOf('[');
                int e = ollamaJsonResponse.LastIndexOf(']');
                if (s >= 0 && e > s)
                {
                    string json = ollamaJsonResponse.Substring(s, e - s + 1);
                    aiShots = JsonSerializer.Deserialize<List<LyricShot>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch { }

            // Ako AI output nije validan, koristimo lokalni engine
            if (aiShots == null || aiShots.Count == 0)
                return GenerateFromLyrics(lyrics, totalDuration, 
                    new VideoIntent { Type = "emotional" });

            // Popuni timestamp-ove i provjeri pravila
            double perLyric    = totalDuration / lyrics.Count;
            string prevShot    = "";
            for (int i = 0; i < aiShots.Count; i++)
            {
                var shot = aiShots[i];
                shot.StartSeconds = Math.Round(i * perLyric, 2);
                shot.EndSeconds   = Math.Round((i + 1) * perLyric, 2);
                if (i == aiShots.Count - 1) shot.EndSeconds = totalDuration;

                // Provjeri i popravi shot_type varijaciju
                if (!string.IsNullOrEmpty(shot.Data?.ShotType) &&
                    shot.Data.ShotType == prevShot)
                {
                    // Promijeni na sljedeći u rotaciji
                    int idx = Array.IndexOf(ShotRotation, prevShot);
                    shot.Data.ShotType = ShotRotation[(idx + 1) % 3];
                }
                prevShot = shot.Data?.ShotType ?? "";
            }

            return aiShots;
        }

        // ─────────────────────────────────────────────────────────────
        // OLLAMA PROMPT GENERATOR
        // ─────────────────────────────────────────────────────────────
        public static string BuildOllamaPrompt(
            List<string> lyrics,
            double totalDuration,
            VideoIntent intent)
        {
            double perLyric = totalDuration / lyrics.Count;
            var    lines    = new System.Text.StringBuilder();

            lines.AppendLine("You are a professional video director for children's music videos.");
            lines.AppendLine($"Song intent: {intent.Type}. Total duration: {totalDuration:F1}s.");
            lines.AppendLine();
            lines.AppendLine("For each lyric line below, generate a JSON array with shot parameters.");
            lines.AppendLine("Rules you MUST follow:");
            lines.AppendLine("1. NEVER use same shot_type twice in a row (rotate: Wide Shot, Medium Shot, Close Up)");
            lines.AppendLine("2. For CHORUS lines (repeated lyrics): use same or very similar search_query for visual theme");
            lines.AppendLine("3. Use cinematic descriptors: 'cinematic', 'bokeh', 'slow motion', 'golden hour', 'high quality'");
            lines.AppendLine("4. Focus on children, families, nature in motion - NO static shots without action");
            lines.AppendLine("5. vibe_score: chorus=8-9, verse=4-6, intro/outro=2-4");
            lines.AppendLine();
            lines.AppendLine("Return ONLY valid JSON array, no explanation:");
            lines.AppendLine("[");

            for (int i = 0; i < lyrics.Count; i++)
            {
                double s = i * perLyric;
                double e = (i + 1) * perLyric;
                string ts = $"{FormatTs(s)} - {FormatTs(e)}";
                lines.AppendLine($"  // Line {i+1}: \"{lyrics[i]}\" ({ts})");
                if (i < lyrics.Count - 1)
                    lines.AppendLine($"  {{\"stih\": \"\", \"timestamp\": \"{ts}\", \"data\": {{\"search_query\": \"\", \"shot_type\": \"\", \"motion_intent\": \"\", \"vibe_score\": 0, \"visual_bridge\": \"\"}}}},");
                else
                    lines.AppendLine($"  {{\"stih\": \"\", \"timestamp\": \"{ts}\", \"data\": {{\"search_query\": \"\", \"shot_type\": \"\", \"motion_intent\": \"\", \"vibe_score\": 0, \"visual_bridge\": \"\"}}}}");
            }
            lines.AppendLine("]");

            return lines.ToString();
        }

        // ─────────────────────────────────────────────────────────────
        // PRIVATNE HELPER METODE
        // ─────────────────────────────────────────────────────────────

        private static string GetNextShotType(ref int idx, string prev, int vibe, bool isChorus)
        {
            // Za visoki vibe u refrenu preferiramo Close Up
            if (isChorus && vibe >= 8 && prev != "Close Up")
                return "Close Up";

            // Inače striktna rotacija
            string next;
            do
            {
                next = ShotRotation[idx % 3];
                idx++;
            } while (next == prev);
            return next;
        }

        private static int CalculateVibe(int idx, int total, bool isChorus, VideoIntent intent)
        {
            // Emotivni luk: početak miran, sredina dinamična, kraj smirena
            double pos = (double)idx / total;

            int baseVibe = isChorus ? 8 :
                pos < 0.1 ? 3 :          // intro
                pos > 0.9 ? 3 :          // outro
                pos > 0.4 && pos < 0.7 ? 7 : // kulminacija
                5;                        // strofa

            // Intent modifikator
            return intent.Type switch
            {
                "hype"   => Math.Min(10, baseVibe + 2),
                "dark"   => Math.Max(1,  baseVibe - 1),
                _        => baseVibe  // emotional/storytelling
            };
        }

        private static string BuildVerseQuery(
            string lyric, VideoIntent intent, int vibe, int idx, int total)
        {
            // Izvuci ključne riječi iz stiha (ignoruj kratke i učestale riječi)
            var stopWords = new HashSet<string>
                {"i","je","da","se","u","na","za","od","do","sa","ma","pa","a","ali","kad"};

            var keywords = lyric
                .ToLower()
                .Split(new[] { ' ', ',', ';', '!', '?', '.' }, 
                       StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3 && !stopWords.Contains(w))
                .Take(2)
                .ToArray();

            // Kontekstualni predmetni mapping - srpske -> engleske asocijacije
            var wordMap = new Dictionary<string, string>
            {
                {"šeta","child walking park"},   {"trči","children running"},
                {"smej","laughing happy child"},  {"snega","children snow playing"},
                {"zima","winter children snow"},  {"leto","summer children outdoor"},
                {"park","children park nature"},  {"mama","mother child tender"},
                {"tata","father child family"},   {"baka","grandmother child warm"},
                {"deka","grandfather child"},     {"sladol","child ice cream summer"},
                {"čokol","child hot chocolate"},  {"sunce","sunny day children"},
                {"priro","children nature walk"}, {"zdravo","active child healthy"},
                {"šetaj","child walking cinematic"},{"jutro","morning child sunshine"},
            };

            // Provjeri da li neki keyword matchuje predmetni mapping
            string mapped = "";
            foreach (var kw in keywords)
            {
                foreach (var (key, val) in wordMap)
                {
                    if (kw.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    {
                        mapped = val;
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(mapped)) break;
            }

            // Pozicijski kontekst
            double pos = (double)idx / total;
            string context = pos < 0.1 ? "peaceful opening" :
                             pos > 0.9 ? "warm ending golden" :
                             pos > 0.5 ? "joyful middle" : "";

            string subject = !string.IsNullOrEmpty(mapped)
                ? mapped
                : string.Join(" ", keywords.Length > 0
                    ? keywords : new[] { "children nature" });

            string vibeDesc = VibeToDescriptor(vibe);
            string intentDesc = intent.Type switch
            {
                "hype"         => "energetic dynamic",
                "dark"         => "dramatic moody",
                "storytelling" => "narrative cinematic",
                _              => "warm emotional"
            };

            return $"{subject} {intentDesc} {vibeDesc} {context}".Trim()
                .Replace("  ", " ");
        }

        private static string BuildChorusQuery(VideoIntent intent)
        {
            // Refren uvijek ima istu vizuelnu temu - srž pesme
            return intent.Type switch
            {
                "hype"   => "children running joyful slow motion dynamic bokeh high quality",
                "dark"   => "dramatic children cinematic moody slow motion bokeh",
                _        => "happy children playing laughing slow motion cinematic golden hour bokeh"
            };
        }

        private static string BuildVisualBridge(
            string shotType, bool isChorus, int idx, int vibe, VideoIntent intent)
        {
            if (isChorus)
                return "CHORUS THEME: consistent visual language, same warm palette";
            if (idx == 0)
                return "OPENING: warm golden tones, peaceful introduction";

            return (shotType, vibe) switch
            {
                ("Close Up",  >= 7) => "intimate energy, subject filling frame",
                ("Close Up",  _)    => "gentle intimacy, soft bokeh background",
                ("Wide Shot", >= 7) => "expansive energy, dynamic landscape",
                ("Wide Shot", _)    => "breathing space, calm environment",
                (_, >= 7)          => "medium dynamic action, subject in motion",
                _                  => "balanced framing, smooth tonal transition"
            };
        }

        private static List<string> DetectChorusLines(List<string> lyrics)
        {
            // Detektuj refrene po ponavljanju - linija koja se pojavi 2+ puta
            var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lyrics)
            {
                string key = l.Trim();
                freq[key] = freq.GetValueOrDefault(key, 0) + 1;
            }
            return freq.Where(kv => kv.Value >= 2).Select(kv => kv.Key).ToList();
        }

        private static string FormatTs(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalMinutes >= 1
                ? $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}"
                : $"0:{ts.Seconds:D2}";
        }

        // Export u JSON (za debug/pregled)
        public static string ToJson(List<LyricShot> shots)
        {
            return JsonSerializer.Serialize(shots, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }
    }
}
