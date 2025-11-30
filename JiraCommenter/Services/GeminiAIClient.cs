using System.Net.Http.Json;
using System.Text.Json;

namespace JiraCommenter.Documentation
{
    public class GeminiAIClient : AIClient
    {
        private readonly HttpClient _httpClient;
        private readonly AISettings _settings;

        public GeminiAIClient(AISettings settings)
        {
            _settings = settings;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(settings.Endpoint)
            };
        }

        public override async Task<string> GenerateAsync(string prompt)
        {
            var request = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.7,
                    maxOutputTokens = 2048
                }
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"/v1beta/models/{_settings.Model}:generateContent? key={_settings.Token}",
                request);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<GeminiResponse>();
            return result?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? "";
        }
    }

    public class GeminiResponse
    {
        public List<Candidate> Candidates { get; set; }
    }

    public class Candidate
    {
        public Content Content { get; set; }
    }

    public class Content
    {
        public List<Part> Parts { get; set; }
    }

    public class Part
    {
        public string Text { get; set; }
    }
}