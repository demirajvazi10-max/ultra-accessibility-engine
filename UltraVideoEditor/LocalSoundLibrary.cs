using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UltraVideoEditor
{
    /// <summary>
    /// Lokalna zvučna biblioteka — zamjena za FreesoundClient.
    /// Auto-skenira Assets/Sounds/ i Assets/SFX/, gradi indeks iz naziva fajlova,
    /// opcionalno koristi LLaMA za semantičko mapiranje nepoznatih tipova zvukova.
    /// Nema potrebe za internetom ili API ključem.
    /// </summary>
    public static class LocalSoundLibrary
    {
        // ── Putanje ──────────────────────────────────────────────────────────
        private static readonly string _appDir       = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string _soundsDir    = Path.Combine(_appDir, "Assets", "Sounds");
        private static readonly string _sfxDir       = Path.Combine(_appDir, "Assets", "SFX");

        private static readonly HashSet<string> _audioExts = new(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".wav", ".flac", ".ogg", ".aiff", ".aif", ".m4a" };

        // ── Indeksi (lazy, thread-safe) ───────────────────────────────────────
        private static Dictionary<string, List<string>> _ambientIndex;
        private static Dictionary<string, List<string>> _sfxIndex;
        private static readonly object _lock = new();

        // Korišćeni fajlovi u jednoj sesiji — sprečava ponavljanje istog zvuka
        private static readonly HashSet<string> _usedAmbient = new();
        private static readonly HashSet<string> _usedSfx     = new();

        // ── Inicijalizacija / skeniranje ──────────────────────────────────────

        private static void EnsureIndexed()
        {
            lock (_lock)
            {
                if (_ambientIndex != null) return;
                _ambientIndex = BuildIndex(_soundsDir);
                _sfxIndex     = BuildIndex(_sfxDir);
            }
        }

        /// <summary>
        /// Vraća ukupan broj zvukova u biblioteci (za UI info).
        /// </summary>
        public static int GetSoundCount()
        {
            EnsureIndexed();
            return _ambientIndex.Count + _sfxIndex.Count;
        }

        /// <summary>
        /// Prisiljava ponovnu izgradnju indeksa (korisno ako korisnik doda fajlove).
        /// </summary>
        public static void Rescan()
        {
            lock (_lock)
            {
                _ambientIndex = null;
                _sfxIndex     = null;
                _usedAmbient.Clear();
                _usedSfx.Clear();
            }
            EnsureIndexed();
        }

        // ── Javni API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Vraća putanju ambijentalnog zvuka koji odgovara traženom tipu.
        /// soundType: slobodan string, npr. "birds", "rain", "snow", "joy playground", "forest"
        /// </summary>
        public static string GetAmbientSound(string soundType)
        {
            EnsureIndexed();
            if (_ambientIndex.Count == 0) return null;

            var tags = TokenizeQuery(soundType);
            tags.AddRange(ExpandSoundType(soundType)); // semantičke sinonime

            string match = FindBestMatch(_ambientIndex, tags, _usedAmbient);
            if (match != null) _usedAmbient.Add(match);
            return match;
        }

        /// <summary>
        /// Vraća putanju tranzicionog zvuka (pop, whoosh, click...).
        /// </summary>
        public static string GetTransitionSound(string type = "pop")
        {
            EnsureIndexed();

            // Pokušaj iz SFX foldera prvo
            if (_sfxIndex.Count > 0)
            {
                var sfxTags = TokenizeQuery(type);
                string sfxMatch = FindBestMatch(_sfxIndex, sfxTags, _usedSfx);
                if (sfxMatch != null)
                {
                    _usedSfx.Add(sfxMatch);
                    return sfxMatch;
                }
            }

            // Fallback: iz Sounds foldera
            if (_ambientIndex.Count > 0)
            {
                var tags = TokenizeQuery(type);
                string match = FindBestMatch(_ambientIndex, tags, _usedSfx);
                if (match != null)
                {
                    _usedSfx.Add(match);
                    return match;
                }
            }

            return null;
        }

        /// <summary>
        /// Asinhronski: koristi LLaMA (ako je dostupna) da mapira slobodan opis
        /// na najodgovarajući zvuk u biblioteci. Korisno za nepoznate tipove.
        /// </summary>
        public static async Task<string> GetAmbientSoundWithAiAsync(
            string description,
            CancellationToken ct = default)
        {
            EnsureIndexed();
            if (_ambientIndex.Count == 0) return null;

            // Pokušaj direktno prvo (brže, bez LLaMA)
            string direct = GetAmbientSound(description);
            if (direct != null) return direct;

            // LLaMA fallback: pitamo model koji tag iz biblioteke odgovara opisu
            try
            {
                var ollama = new OllamaClient();
                if (!await ollama.IsOllamaRunning()) return null;

                // Šaljemo listu dostupnih tagova i tražimo koji odgovara
                var sampleTags = CollectTopTags(_ambientIndex, 60);
                string prompt =
                    $"You are a sound librarian. The user needs an ambient sound for this scene: \"{description}\".\n\n" +
                    $"Available sound tags in our library:\n{string.Join(", ", sampleTags)}\n\n" +
                    $"Reply with ONLY a short comma-separated list (max 4 tags) from the available list above that best match the scene. No explanation.";

                string aiResponse = await ollama.GenerateAsync(prompt, ct: ct);
                if (string.IsNullOrWhiteSpace(aiResponse)) return null;

                var aiTags = aiResponse.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(t => t.Trim().ToLower())
                                       .Where(t => t.Length > 1)
                                       .ToList();

                string aiMatch = FindBestMatch(_ambientIndex, aiTags, _usedAmbient);
                if (aiMatch != null) _usedAmbient.Add(aiMatch);
                return aiMatch;
            }
            catch
            {
                return null;
            }
        }

        // ── Indeksiranje ──────────────────────────────────────────────────────

        private static Dictionary<string, List<string>> BuildIndex(string folder)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(folder)) return index;

            foreach (string file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (!_audioExts.Contains(Path.GetExtension(file))) continue;

                string name   = Path.GetFileNameWithoutExtension(file).ToLower();
                var rawTokens = Regex.Split(name, @"[_\-\s\.]+");
                var tags      = new List<string>();

                foreach (string token in rawTokens)
                {
                    // CamelCase razdvajanje
                    var camel = Regex.Replace(token, @"([a-z])([A-Z])", "$1 $2")
                                     .ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    tags.AddRange(camel.Where(t => t.Length > 1));
                }

                // Dodaj cijeli naziv (bez ekstenzije) kao tag
                tags.Add(name.Replace("_", " ").Replace("-", " "));

                // Dodaj naziv parent foldera kao kontekst
                string parentFolder = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                if (!string.IsNullOrEmpty(parentFolder) && parentFolder.Length > 1)
                    tags.Add(parentFolder.ToLower());

                // Dodaj semantičke sinonime za sve tokene
                foreach (var tag in tags.ToList())
                    tags.AddRange(GetSynonyms(tag));

                index[file] = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            return index;
        }

        private static string FindBestMatch(
            Dictionary<string, List<string>> index,
            IEnumerable<string> queryTags,
            HashSet<string> usedFiles = null)
        {
            if (index == null || index.Count == 0) return null;

            var queryList = queryTags
                .Select(t => t.ToLower().Trim())
                .Where(t => t.Length > 1)
                .ToList();

            if (queryList.Count == 0) return null;

            string bestFile  = null;
            int    bestScore = 0;

            foreach (var (file, tags) in index)
            {
                if (usedFiles != null && usedFiles.Contains(file)) continue;

                int score = 0;
                foreach (string q in queryList)
                {
                    if (tags.Any(t => t == q))                          score += 4; // tačno poklapanje
                    else if (tags.Any(t => t.StartsWith(q) || q.StartsWith(t))) score += 2; // prefiks
                    else if (tags.Any(t => t.Contains(q) || q.Contains(t)))     score += 1; // sadrži
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestFile  = file;
                }
            }

            // Ako ništa nije nađeno, vrati slučajan fajl (bolje od tišine)
            if (bestFile == null && index.Count > 0 && (usedFiles == null || usedFiles.Count < index.Count))
            {
                var available = index.Keys.Where(k => usedFiles == null || !usedFiles.Contains(k)).ToList();
                if (available.Count > 0)
                {
                    int idx = Math.Abs(string.Join("", queryList).GetHashCode()) % available.Count;
                    bestFile = available[idx];
                }
            }

            return bestFile;
        }

        // ── Semantička proširenja ──────────────────────────────────────────────

        /// <summary>
        /// Expanduje soundType string u sinonime za bolje poklapanje s imenima fajlova.
        /// Ovo je heuristički dictionary koji nadopunjuje AI analizu.
        /// </summary>
        private static List<string> ExpandSoundType(string soundType)
        {
            if (string.IsNullOrWhiteSpace(soundType)) return new();

            string lower = soundType.ToLower();
            var result   = new List<string>();

            // Priroda / outdoor
            if (lower.Contains("bird") || lower.Contains("ptic") || lower.Contains("cvrkut"))
                result.AddRange(new[] { "bird", "birds", "chirp", "tweeting", "nature", "outdoor" });
            if (lower.Contains("rain") || lower.Contains("kiša") || lower.Contains("kisa"))
                result.AddRange(new[] { "rain", "rainfall", "drizzle", "drops", "weather" });
            if (lower.Contains("thunder") || lower.Contains("grmlj") || lower.Contains("storm"))
                result.AddRange(new[] { "thunder", "thunderstorm", "storm", "lightning" });
            if (lower.Contains("wind") || lower.Contains("vetar") || lower.Contains("vjetar"))
                result.AddRange(new[] { "wind", "breeze", "gusty", "outdoor" });
            if (lower.Contains("forest") || lower.Contains("šuma") || lower.Contains("suma"))
                result.AddRange(new[] { "forest", "woodland", "trees", "nature", "birds" });
            if (lower.Contains("ocean") || lower.Contains("more") || lower.Contains("sea") || lower.Contains("wave"))
                result.AddRange(new[] { "ocean", "sea", "waves", "shore", "beach", "water" });
            if (lower.Contains("stream") || lower.Contains("creek") || lower.Contains("potok") || lower.Contains("rijeka"))
                result.AddRange(new[] { "stream", "creek", "water", "flowing", "river" });
            if (lower.Contains("snow") || lower.Contains("snijeg") || lower.Contains("sneg") || lower.Contains("winter"))
                result.AddRange(new[] { "snow", "winter", "blizzard", "cold", "freeze" });
            if (lower.Contains("summer") || lower.Contains("ljeto") || lower.Contains("leto"))
                result.AddRange(new[] { "summer", "crickets", "hot", "cicada", "outdoor" });
            if (lower.Contains("night") || lower.Contains("noć") || lower.Contains("noc"))
                result.AddRange(new[] { "night", "crickets", "dark", "evening", "owl" });
            if (lower.Contains("morning") || lower.Contains("jutro"))
                result.AddRange(new[] { "morning", "birds", "dawn", "sunrise", "nature" });

            // Životinje
            if (lower.Contains("dog") || lower.Contains("pas") || lower.Contains("bark"))
                result.AddRange(new[] { "dog", "bark", "barking", "canine" });
            if (lower.Contains("cat") || lower.Contains("mačka") || lower.Contains("maca"))
                result.AddRange(new[] { "cat", "meow", "purr", "feline" });
            if (lower.Contains("horse") || lower.Contains("konj") || lower.Contains("hoof"))
                result.AddRange(new[] { "horse", "gallop", "hooves", "neigh" });
            if (lower.Contains("frog") || lower.Contains("žaba") || lower.Contains("zaba"))
                result.AddRange(new[] { "frog", "amphibian", "pond", "croak" });

            // Urbano / indoor
            if (lower.Contains("city") || lower.Contains("urban") || lower.Contains("grad"))
                result.AddRange(new[] { "city", "urban", "traffic", "street", "cars" });
            if (lower.Contains("cafe") || lower.Contains("coffee") || lower.Contains("restoran"))
                result.AddRange(new[] { "cafe", "indoor", "coffee", "chatter", "ambient" });
            if (lower.Contains("crowd") || lower.Contains("people") || lower.Contains("masa"))
                result.AddRange(new[] { "crowd", "people", "chatter", "murmur" });

            // Djeca / igra
            if (lower.Contains("child") || lower.Contains("djec") || lower.Contains("djet") ||
                lower.Contains("kids") || lower.Contains("playground"))
                result.AddRange(new[] { "children", "kids", "playground", "laughter", "playing" });

            // Radost / energija (iz AI konteksta)
            if (lower.Contains("joy") || lower.Contains("radost") || lower.Contains("happy"))
                result.AddRange(new[] { "children", "playground", "birds", "outdoor", "cheerful" });
            if (lower.Contains("peace") || lower.Contains("mir") || lower.Contains("calm"))
                result.AddRange(new[] { "nature", "birds", "water", "gentle", "soft" });

            // Tranzicioni zvukovi
            if (lower.Contains("pop"))
                result.AddRange(new[] { "pop", "click", "button", "snap" });
            if (lower.Contains("whoosh") || lower.Contains("swoosh"))
                result.AddRange(new[] { "whoosh", "swoosh", "swipe", "transition", "air" });

            return result;
        }

        private static List<string> TokenizeQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return new();
            return Regex.Split(query.ToLower(), @"[_\-\s,;\.]+")
                        .Where(t => t.Length > 1)
                        .ToList();
        }

        private static IEnumerable<string> GetSynonyms(string tag)
        {
            // Srpsko-engleski i česti sinonimi — poboljšava matching s imenima fajlova na engleskom
            return tag.ToLower() switch
            {
                "ptica" or "ptice" or "cvrkut" => new[] { "bird", "birds", "chirp" },
                "kiša" or "kisa"               => new[] { "rain", "rainfall" },
                "more" or "okean"              => new[] { "ocean", "sea", "waves" },
                "snijeg" or "sneg"             => new[] { "snow", "winter" },
                "šuma" or "suma"               => new[] { "forest", "woodland" },
                "vjetar" or "vetar"            => new[] { "wind", "breeze" },
                "djeca" or "djete" or "deca"   => new[] { "children", "kids" },
                "noć" or "noc"                 => new[] { "night", "evening" },
                "jutro"                        => new[] { "morning", "dawn" },
                "grad"                         => new[] { "city", "urban" },
                "rijeka" or "reka"             => new[] { "river", "stream" },
                "potok"                        => new[] { "creek", "stream" },
                "plaža" or "plaza"             => new[] { "beach", "shore" },
                "pas"                          => new[] { "dog", "bark" },
                "mačka" or "maca"              => new[] { "cat", "meow" },
                "konj"                         => new[] { "horse", "gallop" },
                "žaba" or "zaba"               => new[] { "frog", "croak" },
                _                              => Array.Empty<string>()
            };
        }

        private static List<string> CollectTopTags(
            Dictionary<string, List<string>> index, int maxTags)
        {
            var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var tags in index.Values)
                foreach (var tag in tags)
                    freq[tag] = freq.TryGetValue(tag, out int c) ? c + 1 : 1;

            return freq.OrderByDescending(kv => kv.Value)
                       .Take(maxTags)
                       .Select(kv => kv.Key)
                       .ToList();
        }
    }
}
