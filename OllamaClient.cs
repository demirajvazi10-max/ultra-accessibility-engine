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
        private readonly HttpClient _httpClient;
        private readonly string _ollamaUrl = "http://localhost:11434/api/generate";

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
                    return "Greška: Ollama nije pokrenuta. Molimo pokrenite Ollama komandu u terminalu.";
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
                    return $"Greška: {response.StatusCode} - {responseBody}";
                }
            }
            catch (OperationCanceledException)
            {
                return "Operacija otkazana.";
            }
            catch (Exception ex)
            {
                return $"Greška: {ex.Message}. Provjerite da li je Ollama pokrenuta (ollama serve) i da li je model {model} instaliran (ollama pull {model}).";
            }
        }

        public async Task<bool> IsOllamaRunning()
        {
            try
            {
                using var testClient = new HttpClient();
                testClient.Timeout = TimeSpan.FromSeconds(5);
                var response = await testClient.GetAsync("http://localhost:11434/api/tags");
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