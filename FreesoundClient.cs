using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UltraVideoEditor
{
    // ═══════════════════════════════════════════════════════════════
    // FREESOUND CLIENT - ambijentalni zvukovi za scene
    // API: https://freesound.org/apiv2
    // Čita API key iz pixabay-style .bin enkriptovanog fajla
    // ═══════════════════════════════════════════════════════════════

    public class FreesoundClient
    {
        private readonly HttpClient _http;
        // Language helper
        private static string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private static string L(string key) => LanguageManager.GetText(key, _LangCode);
        private static string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);
       // Za API pozive (sa Token auth)
        private readonly HttpClient _dlHttp;     // Za download preview-ova (BEZ auth — CDN ne prihvata Token header!)
        private readonly string     _apiKey;

        // Map scene konteksta -> Freesound query + trajanje
        private static readonly Dictionary<string, (string query, double minDur, double maxDur)> SceneSoundMap
            = new()
        {
            // PRAVILO: max 3 ključne riječi - Freesound API bolje reaguje
            // Testirano i provjereno radi

            // Priroda
            { "park",         ("park birds",                    10, 45) },
            { "forest",       ("forest birds",                  15, 60) },
            { "snow",         ("snow wind",                     10, 45) },
            { "rain",         ("rain gentle",                   10, 45) },
            { "wind",         ("wind nature",                   10, 30) },
            { "birds",        ("birds chirping",                10, 30) },
            { "water",        ("stream water",                  10, 45) },
            { "beach",        ("ocean waves",                   15, 60) },
            { "meadow",       ("meadow birds",                  10, 45) },
            { "garden",       ("garden birds",                  10, 40) },

            // Grad i svakodnevica
            { "city",         ("city outdoor",                  10, 40) },
            { "playground",   ("children playing",              10, 40) },
            { "cafe",         ("cafe interior",                 10, 40) },
            { "home",         ("indoor quiet",                  10, 30) },
            { "morning",      ("morning birds",                 10, 30) },
            { "bedroom",      ("indoor ambient",                10, 30) },
            { "classroom",    ("indoor children",               10, 30) },

            // Godišnja doba
            { "spring",       ("spring birds",                  10, 40) },
            { "summer",       ("summer outdoor",                10, 40) },
            { "autumn",       ("autumn wind",                   10, 40) },
            { "winter",       ("winter wind",                   10, 45) },

            // Emotivni kontekst
            { "joy",          ("children laughing",             10, 30) },
            { "calm",         ("nature peaceful",               10, 45) },
            { "adventure",    ("outdoor wind",                  10, 30) },
            { "romantic",     ("nature birds",                  10, 45) },
            { "melancholy",   ("rain indoor",                   10, 45) },

            // Uspavanka / noć
            { "lullaby",      ("music box",                     10, 60) },
            { "night",        ("night crickets",                10, 45) },
            { "sleep",        ("ambient quiet",                 10, 60) },
            { "moonlight",    ("night ambient",                 10, 45) },

            // Proslava
            { "party",        ("children laughing",             10, 30) },
            { "birthday",     ("children party",                10, 30) },
            { "celebration",  ("celebration outdoor",           10, 30) },

            // Božić / zima
            { "christmas",    ("fireplace indoor",              10, 45) },
            { "fireplace",    ("fireplace crackling",           10, 60) },
            { "holiday",      ("indoor cozy",                   10, 45) },

            // Životinje
            { "farm",         ("farm animals",                  10, 30) },
            { "animals",      ("animals outdoor",               10, 30) },
            { "dog",          ("outdoor nature",                10, 30) },

            // Škola
            { "school",       ("children outdoor",              10, 30) },
            { "learning",     ("indoor children",               10, 30) },
        };

        public FreesoundClient(string apiKey)
        {
            _apiKey = apiKey;

            // HTTP klijent SA autentifikacijom — samo za Freesound API pozive
            // 45s umjesto 30s — Freesound server ponekad kasni (rate limiting, server load)
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            _http.DefaultRequestHeaders.Add("Authorization", $"Token {_apiKey}");

            // HTTP klijent BEZ autentifikacije — za download sa CDN-a (cdn.freesound.org)
            // Freesound CDN vraća 404 ako šalješ Authorization header!
            // Timeout 3 minute — audio fajlovi mogu biti 10-50MB na sporijoj konekciji
            _dlHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            _dlHttp.DefaultRequestHeaders.Add("User-Agent", "UltraVideoEditor/1.0");
        }

        // ── Preuzmi ambijentalni zvuk po kontekstu scene ─────────────
        public async Task<string> GetAmbientForScene(
            string sceneContext,
            double sceneDuration,
            string outputDir,
            CancellationToken ct = default,
            string songContext = null)  // opcionalni globalni kontekst pjesme
        {
            // Ako imamo globalni kontekst pjesme, on ima prioritet za određene tipove
            string effectiveContext = sceneContext;
            if (!string.IsNullOrEmpty(songContext))
            {
                // Za uspavanku — sve scene trebaju mirne zvukove bez obzira na lokaciju
                if (songContext == "lullaby") effectiveContext = "lullaby";
                // Za tužnu — sve scene dobijaju melanholičan prizvuk
                else if (songContext == "sad" && !sceneContext.Contains("rain")) effectiveContext = "melancholy";
                // Za proslavu — sve scene dobijaju veseliji zvuk
                else if (songContext == "party") effectiveContext = sceneContext.Contains("outdoor") ? "playground" : "party";
                // Za božić — sve scene dobijaju topli zimski prizvuk
                else if (songContext == "christmas") effectiveContext = sceneContext.Contains("outdoor") ? "winter" : "christmas";
            }

            // Nađi odgovarajući query
            string key = FindBestMatch(effectiveContext);
            var (query, minDur, maxDur) = SceneSoundMap.TryGetValue(key, out var val)
                ? val
                : ("birds nature", 10, 45);  // Kratki default koji sigurno radi

            // Traži zvuk koji traje barem koliko scena
            double searchMin = Math.Min(sceneDuration * 0.8, minDur);
            double searchMax = Math.Max(sceneDuration * 2,   maxDur);

            return await SearchAndDownload(query, searchMin, searchMax, outputDir, ct);
        }

        // ── Preuzmi zvuk za tranziciju (match cut support) ───────────
        public async Task<string> GetTransitionSound(
            string transitionType,
            string outputDir,
            CancellationToken ct = default)
        {
            string query = transitionType switch
            {
                "match_cut" => "whoosh fast transition short",
                "dissolve"  => "soft transition gentle short",
                "zoom"      => "zoom whoosh short",
                "whoosh"    => "whoosh swoosh transition short",
                "pop"       => "pop click transition short",
                _           => "soft pop transition short"
            };
            // Tranzicioni zvukovi moraju biti kratki: 0.2 - 1.5 sekundi
            return await SearchAndDownload(query, 0.2, 1.5, outputDir, ct);
        }

        // ── Direktna pretraga ────────────────────────────────────────
        public async Task<string> SearchAndDownload(
            string query,
            double minDuration,
            double maxDuration,
            string outputDir,
            CancellationToken ct = default)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                string minDurStr = minDuration.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                string maxDurStr = maxDuration.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

                // type:sfx OR type:field-recording — Freesound API podržava type kao
                // poseban search parametar: &filter=type:sfx
                // NAPOMENA: Freesound ne podržava OR u filter stringu za type.
                // Radimo dvije pretrage (sfx i field-recording) i uzimamo bolje rezultate.

                // Pokušaj 1: SFX tip + duration filter
                string url = "https://freesound.org/apiv2/search/text/" +
                    $"?query={Uri.EscapeDataString(query)}" +
                    $"&filter=duration:[{minDurStr}%20TO%20{maxDurStr}]%20type:sfx" +
                    "&fields=id,name,duration,previews,type" +
                    "&page_size=15&sort=rating_desc";

                // Lokalni helper koji hvata timeout i vraća null umjesto bacanja exceptiona
                // — sprečava da jedan spori Freesound API poziv zablokirta čitav generator
                async Task<HttpResponseMessage> SafeGet(string reqUrl)
                {
                    try { return await _http.GetAsync(reqUrl, ct); }
                    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                    {
                        LastError = $"Freesound API timeout za: {reqUrl.Substring(0, Math.Min(80, reqUrl.Length))}";
                        return null; // server spor → vrati null, ne baci exception
                    }
                }

                HttpResponseMessage searchResp = await SafeGet(url);
                if (searchResp == null) return null; // timeout na prvom pokušaju → odustani

                // Pokušaj 2: field-recording tip + duration filter
                if (searchResp?.IsSuccessStatusCode == true && (await TryGetHits(searchResp))?.Count == 0
                    || searchResp?.IsSuccessStatusCode == false)
                {
                    url = "https://freesound.org/apiv2/search/text/" +
                        $"?query={Uri.EscapeDataString(query)}" +
                        $"&filter=duration:[{minDurStr}%20TO%20{maxDurStr}]%20type:field-recording" +
                        "&fields=id,name,duration,previews,type" +
                        "&page_size=15&sort=rating_desc";
                    searchResp = await SafeGet(url) ?? searchResp; // zadrži prethodni ako novi timeout
                }

                // Pokušaj 3: samo query, bez type filtera
                if (searchResp?.IsSuccessStatusCode == false)
                {
                    url = "https://freesound.org/apiv2/search/text/" +
                        $"?query={Uri.EscapeDataString(query)}" +
                        $"&filter=duration:[{minDurStr}%20TO%20{maxDurStr}]" +
                        "&fields=id,name,duration,previews,type" +
                        "&page_size=15&sort=rating_desc";
                    searchResp = await SafeGet(url) ?? searchResp;
                }

                // Pokušaj 4: bez ikakvog filtera
                if (searchResp?.IsSuccessStatusCode == false)
                {
                    url = "https://freesound.org/apiv2/search/text/" +
                        $"?query={Uri.EscapeDataString(query)}" +
                        "&fields=id,name,duration,previews,type" +
                        "&page_size=15&sort=rating_desc";
                    searchResp = await SafeGet(url) ?? searchResp;
                }

                if (searchResp == null || !searchResp.IsSuccessStatusCode)
                {
                    LastError = searchResp == null
                        ? "Freesound API nije odgovorio (timeout)"
                        : $"Search HTTP {(int)searchResp.StatusCode}: {searchResp.ReasonPhrase}";
                    return null;
                }

                string body = await searchResp.Content.ReadAsStringAsync(ct);
                var    json = JObject.Parse(body);
                var    hits = json["results"] as JArray;

                if (hits == null || hits.Count == 0)
                {
                    // Retry sa skraćenim query-jem - uzmi samo prvu riječ
                    string shortQuery = query.Split(' ')[0];
                    if (shortQuery != query && shortQuery.Length > 2)
                    {
                        string retryUrl = "https://freesound.org/apiv2/search/text/" +
                            $"?query={Uri.EscapeDataString(shortQuery)}" +
                            "&fields=id,name,duration,previews" +
                            "&page_size=10&sort=rating_desc";
                        var retryResp = await _http.GetAsync(retryUrl, ct);
                        if (retryResp.IsSuccessStatusCode)
                        {
                            string retryBody = await retryResp.Content.ReadAsStringAsync(ct);
                            var retryJson = JObject.Parse(retryBody);
                            hits = retryJson["results"] as JArray;
                        }
                    }

                    if (hits == null || hits.Count == 0)
                    {
                        LastError = $"Nema rezultata za query: '{query}'";
                        return null;
                    }
                }

                // Ručni filter po trajanju + isključi kratke loop zvukove koji su obično muzika
                var validHits = new List<JToken>();
                foreach (var h in hits)
                {
                    double dur = h["duration"]?.Value<double>() ?? 0;
                    string hitType = h["type"]?.ToString()?.ToLower() ?? "";
                    string hitName = h["name"]?.ToString()?.ToLower() ?? "";

                    // Isključi muzičke fajlove po tipu
                    if (hitType == "music") continue;

                    // Isključi fajlove koji po imenu izgledaju kao muzika
                    bool looksLikeMusic = hitName.Contains("music") || hitName.Contains("song") ||
                                         hitName.Contains("melody") || hitName.Contains("beat") ||
                                         hitName.Contains("track") || hitName.Contains("loop") ||
                                         hitName.Contains("jingle") || hitName.Contains("tune");
                    if (looksLikeMusic) continue;

                    if (dur >= minDuration * 0.5 && dur <= maxDuration * 2.0)
                        validHits.Add(h);
                }
                // Ako smo isključili previše, uzmimo sve non-music
                if (validHits.Count == 0)
                {
                    foreach (var h in hits)
                    {
                        string hitType = h["type"]?.ToString()?.ToLower() ?? "";
                        if (hitType != "music") validHits.Add(h);
                    }
                }
                if (validHits.Count == 0) validHits.AddRange(hits);

                var rng = new Random(Guid.NewGuid().GetHashCode());
                var hit = validHits[rng.Next(Math.Min(validHits.Count, 5))];

                string dlUrl = hit["previews"]?["preview-hq-mp3"]?.ToString()
                            ?? hit["previews"]?["preview-lq-mp3"]?.ToString();

                if (string.IsNullOrEmpty(dlUrl))
                {
                    LastError = "Preview URL nije dostupan u rezultatu";
                    return null;
                }

                // CDN download BEZ Authorization headera — streaming direktno na disk
                // GetAsync sa HttpCompletionOption.ResponseHeadersRead ne čeka cijeli body
                HttpResponseMessage dlResp = await _dlHttp.GetAsync(dlUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!dlResp.IsSuccessStatusCode)
                {
                    LastError = $"CDN download HTTP {(int)dlResp.StatusCode} za: {dlUrl}";
                    return null;
                }

                string name = $"ambient_{Guid.NewGuid().ToString().Substring(0, 8)}.mp3";
                string dest = Path.Combine(outputDir, name);

                using (var dlStream = await dlResp.Content.ReadAsStreamAsync(ct))
                using (var fileStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await dlStream.CopyToAsync(fileStream, ct);
                }

                var fi = new System.IO.FileInfo(dest);
                if (!fi.Exists || fi.Length < 1000)
                {
                    LastError = string.Format(L("fs_file_too_small"), fi?.Length ?? 0);
                    try { File.Delete(dest); } catch { }
                    return null;
                }

                LastError = null;
                return dest;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LastError = $"Iznimka: {ex.Message}";
                return null;
            }
        }

        /// <summary>Zadnja greška — čitaj nakon null rezultata za dijagnostiku.</summary>
        public string LastError { get; private set; }

        // Helper: pokušaj parsirati hits iz response, vrati null ako ne ide
        private async Task<JArray> TryGetHits(HttpResponseMessage resp)
        {
            try
            {
                if (!resp.IsSuccessStatusCode) return null;
                string body = await resp.Content.ReadAsStringAsync();
                var json = JObject.Parse(body);
                var hits = json["results"] as JArray;
                return hits?.Count > 0 ? hits : null;
            }
            catch { return null; }
        }

        // ── API key management ────────────────────────────────────────
        [SupportedOSPlatform("windows")]
        public static string ReadKey()
        {
            try
            {
                // Isti format kao MainWindow.GetApiKey("freesound")
                string dir  = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UltraVideoEditor");
                // Pokušaj oba formata koja MainWindow koristi
                string bin1 = Path.Combine(dir, "freesound_key.bin");
                string bin2 = Path.Combine(dir, "freesound.bin");
                string path = File.Exists(bin1) ? bin1 : File.Exists(bin2) ? bin2 : null;
                if (path != null)
                {
                    byte[] enc = File.ReadAllBytes(path);
                    byte[] dec = System.Security.Cryptography.ProtectedData.Unprotect(
                        enc, null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    return System.Text.Encoding.UTF8.GetString(dec).Trim();
                }
            }
            catch { }
            return null;
        }

        [SupportedOSPlatform("windows")]
        public static void SaveKey(string key)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UltraVideoEditor");
                Directory.CreateDirectory(dir);
                byte[] data = System.Text.Encoding.UTF8.GetBytes(key);
                byte[] enc  = System.Security.Cryptography.ProtectedData.Protect(
                    data, null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                File.WriteAllBytes(Path.Combine(dir, "freesound_key.bin"), enc);
            }
            catch { }
        }

        // Alias za kompatibilnost sa starim kodom
        public Task<string> GetAmbientSound(string soundType, string outputDir)
            => GetAmbientForScene(soundType, 30, outputDir);

        // ── Helper: nađi najbliži ključ u mapi ───────────────────────
        private static string FindBestMatch(string context)
        {
            if (string.IsNullOrEmpty(context)) return "park";
            string lower = context.ToLower();

            // Direktan match
            if (SceneSoundMap.ContainsKey(lower)) return lower;

            // Parcijalan match
            foreach (var key in SceneSoundMap.Keys)
                if (lower.Contains(key) || key.Contains(lower))
                    return key;

            // Semantički fallback — svi konteksti
            // Uspavanka
            if (lower.Contains("sleep") || lower.Contains("lullaby") || lower.Contains("bedtime") || lower.Contains("uspa"))
                return "lullaby";
            if (lower.Contains("night") || lower.Contains("moon") || lower.Contains("noć"))
                return "night";
            if (lower.Contains("bedroom") || lower.Contains("soba") || lower.Contains("krevet"))
                return "bedroom";

            // Proslava
            if (lower.Contains("birthday") || lower.Contains("party") || lower.Contains("celebr") || lower.Contains("рођендан"))
                return "party";
            if (lower.Contains("balloon") || lower.Contains("balon") || lower.Contains("confetti"))
                return "celebration";

            // Božić
            if (lower.Contains("christmas") || lower.Contains("santa") || lower.Contains("božić") || lower.Contains("holiday"))
                return "christmas";
            if (lower.Contains("fireplace") || lower.Contains("kamin") || lower.Contains("fire"))
                return "fireplace";

            // Priroda
            if (lower.Contains("child") || lower.Contains("play") || lower.Contains("djec") || lower.Contains("deca"))
                return "playground";
            if (lower.Contains("walk") || lower.Contains("šetaj"))
                return "park";
            if (lower.Contains("snow") || lower.Contains("snijeg") || lower.Contains("sneg"))
                return "snow";
            if (lower.Contains("tree") || lower.Contains("šuma") || lower.Contains("forest"))
                return "forest";
            if (lower.Contains("mom") || lower.Contains("fam") || lower.Contains("home") || lower.Contains("kuć"))
                return "home";
            if (lower.Contains("run") || lower.Contains("trci"))
                return "park";
            if (lower.Contains("sun") || lower.Contains("sunce") || lower.Contains("summer"))
                return "summer";
            if (lower.Contains("joy") || lower.Contains("smej") || lower.Contains("laugh"))
                return "joy";
            if (lower.Contains("rain") || lower.Contains("kiša") || lower.Contains("kisa"))
                return "rain";
            if (lower.Contains("beach") || lower.Contains("wave") || lower.Contains("ocean") || lower.Contains("more"))
                return "beach";
            if (lower.Contains("school") || lower.Contains("škola") || lower.Contains("class"))
                return "school";
            if (lower.Contains("autumn") || lower.Contains("jesen") || lower.Contains("fall"))
                return "autumn";
            if (lower.Contains("spring") || lower.Contains("proljeć") || lower.Contains("proleć"))
                return "spring";
            if (lower.Contains("bird") || lower.Contains("ptica"))
                return "birds";
            if (lower.Contains("water") || lower.Contains("stream") || lower.Contains("voda"))
                return "water";
            if (lower.Contains("meadow") || lower.Contains("livad"))
                return "meadow";
            if (lower.Contains("garden") || lower.Contains("bašt") || lower.Contains("vrt"))
                return "garden";
            if (lower.Contains("farm") || lower.Contains("farma") || lower.Contains("animal"))
                return "farm";
            if (lower.Contains("romantic") || lower.Contains("love") || lower.Contains("ljubav"))
                return "romantic";
            if (lower.Contains("sad") || lower.Contains("tužan") || lower.Contains("melan"))
                return "melancholy";
            if (lower.Contains("advent") || lower.Contains("explor") || lower.Contains("istraž"))
                return "adventure";

            return "park"; // universal default
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // AMBIENT MIXER - FFmpeg miksovanje pesme + ambijentalnog zvuka
    // ═══════════════════════════════════════════════════════════════

    public static class AmbientMixer
    {
        // Miksuj audio fajl sa ambijentalnim zvukom
        // ambientVolume: 0.0 - 1.0 (preporučeno 0.12 - 0.18)
        public static async Task<string> MixWithAmbient(
            string mainAudioPath,
            string ambientPath,
            double mainDuration,
            string outputPath,
            double ambientVolume = 0.15,
            CancellationToken ct = default)
        {
            string ffmpeg = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Ffmpeg", "ffmpeg.exe");

            if (!File.Exists(ffmpeg) || !File.Exists(ambientPath))
                return mainAudioPath; // fallback na originalni audio

            // FFmpeg amix filter:
            // - ambijent se loop-uje da pokrije cijelo trajanje
            // - pesma ostaje na 100%, ambijent na ambientVolume
            string args = $"-nostdin " +
                $"-i \"{mainAudioPath}\" " +
                $"-stream_loop -1 -i \"{ambientPath}\" " +
                $"-filter_complex \"" +
                $"[0:a]volume=1.0[main];" +
                $"[1:a]volume={ambientVolume.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}[amb];" +
                $"[main][amb]amix=inputs=2:duration=first:dropout_transition=3[out]\" " +
                $"-map \"[out]\" -c:a aac -b:a 192k " +
                $"-t {mainDuration.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"-y \"{outputPath}\"";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = ffmpeg,
                Arguments              = args,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardError  = true,
                RedirectStandardOutput = true
            };

            try
            {
                using var proc = System.Diagnostics.Process.Start(psi);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && File.Exists(outputPath))
                    return outputPath;
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            return mainAudioPath; // fallback
        }
    }
}
