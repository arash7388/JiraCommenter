using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace JiraCommenter.Documentation
{
    public class BitbucketClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly BitbucketSettings _settings;
        private bool _disposed;

        public BitbucketClient(BitbucketSettings settings)
        {
            _settings = settings;
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(settings.BaseUrl)
            };
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        }

        public async Task<List<BitbucketPullRequest>> GetMergedPullRequestsAsync(string repoSlug)
        {
            var pullRequests = new List<BitbucketPullRequest>();
            var url = $"/rest/api/1.0/projects/{_settings.ProjectKey}/repos/{repoSlug}/pull-requests?state=MERGED";

            var response = await _httpClient.GetFromJsonAsync<BitbucketPRResponse>(url);

            if (response?.Values != null)
            {
                pullRequests.AddRange(response.Values.Select(pr => new BitbucketPullRequest
                {
                    Id = pr.Id,
                    Title = pr.Title ?? "",
                    Description = pr.Description ?? "",
                    Author = pr.Author?.User?.DisplayName ?? "",
                    MergedDate = DateTimeOffset.FromUnixTimeMilliseconds(pr.UpdatedDate).DateTime
                }));
            }

            return pullRequests;
        }

        public async Task<List<string>> GetPRFilesAsync(string repoSlug, int prId)
        {
            var files = new List<string>();
            var url = $"/rest/api/1.0/projects/{_settings.ProjectKey}/repos/{repoSlug}/pull-requests/{prId}/changes";

            var response = await _httpClient.GetFromJsonAsync<BitbucketChangesResponse>(url);

            if (response?.Values != null)
            {
                files.AddRange(response.Values.Select(c => c.Path?.Name ?? ""));
            }

            return files.Where(f => !string.IsNullOrEmpty(f)).ToList();
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

    public class BitbucketPullRequest
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Author { get; set; } = "";
        public DateTime MergedDate { get; set; }
    }

    // Internal API response models
    internal class BitbucketPRResponse
    {
        public List<BitbucketApiPR>? Values { get; set; }
    }

    internal class BitbucketApiPR
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public BitbucketAuthor? Author { get; set; }
        public long UpdatedDate { get; set; }
    }

    internal class BitbucketAuthor
    {
        public BitbucketUser? User { get; set; }
    }

    internal class BitbucketUser
    {
        public string? DisplayName { get; set; }
    }

    internal class BitbucketChangesResponse
    {
        public List<BitbucketChange>? Values { get; set; }
    }

    internal class BitbucketChange
    {
        public BitbucketPath? Path { get; set; }
    }

    internal class BitbucketPath
    {
        public string? Name { get; set; }
    }
}
