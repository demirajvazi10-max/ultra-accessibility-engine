using System;
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
            _httpClient.Timeout = TimeSpan.FromSeconds(180); // Povećano na 180s za veće modele
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
                    var result = JsonConvert.DeserializeObject<OllamaResponse>(responseBody);
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
    }

    public class OllamaResponse
    {
        public string response     { get; set; }
        public bool   done         { get; set; }
        public int?   eval_count   { get; set; }
        public int?   eval_duration { get; set; }
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