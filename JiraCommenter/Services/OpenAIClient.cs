using JiraCommenter.Models;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace JiraCommenter.Services
{
    public class OpenAIClient : AIClient
    {
        private readonly HttpClient _httpClient;
        private readonly AISettings _settings;

        public OpenAIClient(AISettings settings)
        {
            _settings = settings;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token);
        }

        public override async Task<string> GenerateAsync(string prompt)
        {
            var requestBody = new
            {
                model = _settings.Model,
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0.7,
            };

            var response = await _httpClient.PostAsJsonAsync(_settings.Endpoint, requestBody);

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OpenAIResponse>();
            return result?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
        }
    }

    // OpenAI-specific response models
    public class OpenAIResponse
    {
        public Choice[] Choices { get; set; }
    }

    public class Choice
    {
        public Message Message { get; set; }
    }

    public class Message
    {
        public string Content { get; set; }
    }
}
