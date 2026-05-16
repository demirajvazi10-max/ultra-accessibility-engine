using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace UltraVideoEditor
{
    public class OllamaClient
    {
        // Language helper
        private static string _LangCode => (System.Windows.Application.Current?.MainWindow as MainWindow)?._currentLanguage ?? "sr";
        private static string L(string key) => LanguageManager.GetText(key, _LangCode);
        private static string LF(string key, params object[] args) => string.Format(LanguageManager.GetText(key, _LangCode), args);

        private readonly HttpClient _httpClient;
        private readonly string _ollamaUrl = "http://localhost:11434/api/generate";

        // Statički HttpClient za provjere dostupnosti — sprečava socket exhaustion
        // kada se IsOllamaRunning poziva 30+ puta po video generisanju
        private static readonly HttpClient _checkClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        public OllamaClient()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(300); // 5 minuta — Qwen2.5-VL na CPU moze biti spor
        }

        public async Task<string> GenerateAsync(string prompt, string model = "llama3.2", CancellationToken ct = default)
        {
            try
            {
                // Provjeri da li Ollama radi prije slanja zahtjeva
                if (!await IsOllamaRunning())
                {
                    return L("ol_not_running");
                }

                var request = new
                {
                    model = model,
                    prompt = prompt,
                    stream = false,
                    options = new
                    {
                        temperature = 0.3,
                        num_predict = 2000,  // Povećano za duže odgovore
                        top_p = 0.9,
                        top_k = 40
                    }
                };

                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_ollamaUrl, content, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonConvert.DeserializeObject<OllamaResponse>(
                        responseBody,
                        new JsonSerializerSettings
                        {
                            MissingMemberHandling = MissingMemberHandling.Ignore,
                            Error = (sender, args) => { args.ErrorContext.Handled = true; }
                        });
                    return result?.response?.Trim() ?? "";
                }
                else
                {
                    return LF("ol_status_error", response.StatusCode, responseBody);
                }
            }
            catch (OperationCanceledException)
            {
                return "Operacija otkazana.";
            }
            catch (Exception ex)
            {
                return LF("ol_connection_error", ex.Message);
            }
        }

        public async Task<bool> IsOllamaRunning()
        {
            try
            {
                // Koristimo statički _checkClient — ne kreiramo novu instancu pri svakom pozivu
                var response = await _checkClient.GetAsync("http://localhost:11434/api/tags");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Šalje sliku (Base64) Qwen2-VL modelu i vraća opis/analizu slike.
        /// imagePath: putanja do PNG/JPG fajla (ekstrahovanog frejma iz videa).
        /// prompt: šta da analizira, npr. "Describe the scene in this image. Is it outdoor or indoor?
        ///         Are there children, faces, animals? Rate image quality 1-10. List key visual elements."
        /// </summary>
        /// <summary>
        /// Vraca par (odgovor, greska) — greska je null ako je sve ok.
        /// Koristiti VisionAsync za backward compat, VisionAsyncEx za debug info.
        /// </summary>
        public async Task<(string response, string error)> VisionAsyncEx(
            string imagePath,
            string prompt,
            string model = "qwen2-vl",
            CancellationToken ct = default)
        {
            // imagePath == null → tekstualni warm-up ping (bez slike), samo ucitava model
            string[] imageArray = null;
            if (imagePath != null)
            {
                if (!File.Exists(imagePath))
                    return (null, $"Fajl ne postoji: {imagePath}");

                byte[] imageBytes;
                try
                {
                    imageBytes = await File.ReadAllBytesAsync(imagePath, ct);
                }
                catch (Exception ex)
                {
                    return (null, $"Greska citanja slike: {ex.Message}");
                }

                if (imageBytes.Length < 100)
                    return (null, $"Slika previse mala ({imageBytes.Length}B) — preskacemo");

                imageArray = new[] { Convert.ToBase64String(imageBytes) };
            }

            var request = new OllamaVisionRequest
            {
                model  = model,
                prompt = string.IsNullOrEmpty(prompt) ? "Hello" : prompt,
                images = imageArray,   // null = tekst-only poziv za warm-up
                stream = false,
                options = new
                {
                    temperature = 0.1,
                    num_predict = imageArray == null ? 10 : 1000,
                    top_p       = 0.9
                }
            };

            string json = JsonConvert.SerializeObject(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            string responseBody;
            try
            {
                response     = await _httpClient.PostAsync(_ollamaUrl, content, ct);
                responseBody = await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (null, "Korisnik otkazao analizu");
            }
            catch (OperationCanceledException)
            {
                // Nije korisnik — Qwen timeout (model jos procesira)
                return (null, $"Qwen timeout — model nije odgovorio na vrijeme. " +
                              $"Pokusaj smanjiti interval analize ili restartovati Ollamu.");
            }
            catch (HttpRequestException ex)
            {
                return (null, $"HTTP greska: {ex.Message} — da li Ollama radi?");
            }
            catch (Exception ex)
            {
                return (null, $"Neocekivana greska: {ex.Message}");
            }

            if (!response.IsSuccessStatusCode)
            {
                // Uzmi kratki dio body-ja za log
                string snippet = responseBody?.Length > 200
                    ? responseBody.Substring(0, 200) + "..."
                    : responseBody;
                return (null, $"HTTP {(int)response.StatusCode}: {snippet}");
            }

            if (string.IsNullOrWhiteSpace(responseBody))
                return (null, "Ollama vratila prazan body");

            OllamaResponse result;
            try
            {
                result = JsonConvert.DeserializeObject<OllamaResponse>(
                    responseBody,
                    new JsonSerializerSettings
                    {
                        // Ako Ollama doda novo polje ili tip ne odgovara — ignorisi, ne pucaj
                        MissingMemberHandling = MissingMemberHandling.Ignore,
                        Error = (sender, args) => { args.ErrorContext.Handled = true; }
                    });
            }
            catch (Exception ex)
            {
                return (null, $"JSON parse greska: {ex.Message}");
            }

            string text = result?.response?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return (null, "Ollama odgovorila ali 'response' polje je prazno");

            return (text, null);
        }

        public async Task<string> VisionAsync(
            string imagePath,
            string prompt,
            string model = "qwen2-vl",
            CancellationToken ct = default)
        {
            var (response, _) = await VisionAsyncEx(imagePath, prompt, model, ct);
            return response;
        }

        /// <summary>
        /// Provjeri da li je određeni model dostupan u Ollama.
        /// </summary>
        public async Task<bool> IsModelAvailable(string modelName)
        {
            try
            {
                var response = await _checkClient.GetAsync("http://localhost:11434/api/tags");
                if (!response.IsSuccessStatusCode) return false;

                string body = await response.Content.ReadAsStringAsync();
                return body.Contains(modelName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public class OllamaResponse
    {
        public string response      { get; set; }
        public bool   done          { get; set; }
        public long?  eval_count    { get; set; }  // moze biti velik broj
        public long?  eval_duration { get; set; }  // nanosekunde — mora biti long, ne int!
        public long?  total_duration { get; set; }
        public long?  load_duration  { get; set; }
        public long?  prompt_eval_duration { get; set; }
    }

    // Ollama vision request (sa slikom)
    public class OllamaVisionRequest
    {
        public string   model   { get; set; }
        public string   prompt  { get; set; }
        public string[] images  { get; set; }
        public bool     stream  { get; set; } = false;
        public object   options { get; set; }
    }
}