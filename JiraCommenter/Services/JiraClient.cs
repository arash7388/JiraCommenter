using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace JiraCommenter.Documentation
{
    public class JiraClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly JiraSettings _settings;
        private bool _disposed;

        public JiraClient(JiraSettings settings)
        {
            _settings = settings;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(settings.BaseUrl)
            };

            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{settings.User}:{settings.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        public async Task<List<JiraIssue>> GetEpicsAsync(List<string> epicKeys)
        {
            var issues = new List<JiraIssue>();

            if (epicKeys == null || !epicKeys.Any())
            {
                return issues;
            }

            var jql = $"key in ({string.Join(",", epicKeys)})";
            var response = await _httpClient.GetFromJsonAsync<JiraSearchResponse>($"/rest/api/2/search?jql={Uri.EscapeDataString(jql)}");

            if (response?.Issues != null)
            {
                issues.AddRange(response.Issues.Select(MapToJiraIssue));
            }

            return issues;
        }

        public async Task<List<JiraIssue>> GetIssuesInEpicAsync(string epicKey)
        {
            var jql = $"\"Epic Link\" = {epicKey}";
            var response = await _httpClient.GetFromJsonAsync<JiraSearchResponse>($"/rest/api/2/search?jql={Uri.EscapeDataString(jql)}");

            if (response?.Issues != null)
            {
                return response.Issues.Select(MapToJiraIssue).ToList();
            }

            return new List<JiraIssue>();
        }

        private JiraIssue MapToJiraIssue(JiraApiIssue apiIssue)
        {
            return new JiraIssue
            {
                Key = apiIssue.Key,
                Summary = apiIssue.Fields?.Summary ?? "",
                Description = apiIssue.Fields?.Description ?? "",
                Status = apiIssue.Fields?.Status?.Name ?? "",
                IssueType = apiIssue.Fields?.IssueType?.Name ?? ""
            };
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _httpClient?.Dispose();
                }
                _disposed = true;
            }
        }
    }

    public class JiraIssue
    {
        public string Key { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "";
        public string IssueType { get; set; } = "";
    }

    // Internal API response models
    internal class JiraSearchResponse
    {
        public List<JiraApiIssue> Issues { get; set; } = new();
    }

    internal class JiraApiIssue
    {
        public string Key { get; set; } = "";
        public JiraFields? Fields { get; set; }
    }

    internal class JiraFields
    {
        public string? Summary { get; set; }
        public string? Description { get; set; }
        public JiraStatus? Status { get; set; }
        public JiraIssueType? IssueType { get; set; }
    }

    internal class JiraStatus
    {
        public string? Name { get; set; }
    }

    internal class JiraIssueType
    {
        public string? Name { get; set; }
    }
}
