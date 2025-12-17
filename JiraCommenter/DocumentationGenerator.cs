using JiraCommenter.Models;
using JiraCommenter.Services;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace JiraCommenter.Documentation
{
    public class DocumentationGenerator
    {
        private readonly BitbucketClient _bitbucketClient;
        private readonly JiraClient _jiraClient;
        private readonly AIClient _aiClient;
        private readonly DocumentationSettings _settings;

        public DocumentationGenerator(
            BitbucketClient bitbucketClient,
            JiraClient jiraClient,
            AIClient aiClient,
            DocumentationSettings settings)
        {
            _bitbucketClient = bitbucketClient;
            _jiraClient = jiraClient;
            _aiClient = aiClient;
            _settings = settings;
        }

        public async Task<List<FeatureDocumentation>> GenerateDocumentationAsync()
        {
            var features = new List<FeatureDocumentation>();

            // 1. Get all epics/features from Jira
            var epics = await _jiraClient.GetEpicsAsync(_settings.EpicKeysToDocument);

            foreach (var epic in epics)
            {
                var feature = new FeatureDocumentation
                {
                    FeatureName = epic.Summary,
                    EpicKey = epic.Key,
                    Description = epic.Description
                };

                // 2. Get all child issues (stories, tasks, bugs)
                var childIssues = await _jiraClient.GetIssuesInEpicAsync(epic.Key);
                feature.TaskHistory = childIssues.Select(MapToTaskHistory).ToList();

                // 3. Get related PRs from Bitbucket
                var relatedPRs = await GetRelatedPullRequestsAsync(childIssues);
                feature.RelatedPRs = relatedPRs;

                // 4. Generate AI summary
                feature.AIGeneratedSummary = await GenerateAISummaryAsync(feature);
                feature.BusinessContext = await GenerateBusinessContextAsync(feature);

                features.Add(feature);
            }

            // 5. Generate output files
            await GenerateOutputFilesAsync(features);

            return features;
        }

        private async Task<List<PullRequestSummary>> GetRelatedPullRequestsAsync(
            List<JiraIssue> issues)
        {
            var prSummaries = new List<PullRequestSummary>();
            var jiraKeys = issues.Select(i => i.Key).ToHashSet();

            // Get merged PRs from all repos
            foreach (var repoSlug in _settings.RepoSlugs)
            {
                var mergedPRs = await _bitbucketClient.GetMergedPullRequestsAsync(repoSlug);

                foreach (var pr in mergedPRs)
                {
                    // Check if PR title or description contains Jira keys
                    var linkedKeys = ExtractJiraKeys(pr.Title + " " + pr.Description)
                        .Where(k => jiraKeys.Contains(k))
                        .ToList();

                    if (linkedKeys.Any())
                    {
                        prSummaries.Add(new PullRequestSummary
                        {
                            PrId = pr.Id,
                            Title = pr.Title,
                            Description = pr.Description,
                            Author = pr.Author,
                            MergedDate = pr.MergedDate,
                            LinkedJiraKeys = linkedKeys,
                            FilesChanged = await _bitbucketClient.GetPRFilesAsync(repoSlug, pr.Id)
                        });
                    }
                }
            }

            return prSummaries.OrderBy(p => p.MergedDate).ToList();
        }

        private List<string> ExtractJiraKeys(string text)
        {
            // Match patterns like "PROJ-123", "ABC-456"
            var regex = new System.Text.RegularExpressions.Regex(@"\b[A-Z]+-\d+\b");
            return regex.Matches(text).Select(m => m.Value).Distinct().ToList();
        }

        private async Task<string> GenerateAISummaryAsync(FeatureDocumentation feature)
        {
            var prompt = $@"
You are a technical writer creating documentation for new team members.
Based on the following feature information, create a clear, human-friendly summary.

## Feature: {feature.FeatureName}
## Epic Key: {feature.EpicKey}

### Original Description:
{feature.Description}

### Completed Tasks:
{JsonSerializer.Serialize(feature.TaskHistory, new JsonSerializerOptions { WriteIndented = true })}

### Related Code Changes (PRs):
{JsonSerializer.Serialize(feature.RelatedPRs.Select(pr => new { pr.Title, pr.Description, pr.MergedDate }), new JsonSerializerOptions { WriteIndented = true })}

Please provide:
1.  A clear 2-3 paragraph summary of what this feature does from a business perspective
2. Key functionality points (bullet list)
3. Technical implementation notes that would help developers understand the codebase
4. Any important business rules or edge cases mentioned in the tasks
";

            return await _aiClient.GenerateAsync(prompt);
        }

        private async Task<string> GenerateBusinessContextAsync(FeatureDocumentation feature)
        {
            var prompt = $@"
Based on the Jira tickets and PR descriptions for feature '{feature.FeatureName}', 
extract and explain the business context:

- Why was this feature built?
- What problem does it solve for users?
- What are the main user workflows? 

Tasks: {JsonSerializer.Serialize(feature.TaskHistory.Select(t => new { t.Title, t.Description }))}
";

            return await _aiClient.GenerateAsync(prompt);
        }

        private TaskHistory MapToTaskHistory(JiraIssue issue)
        {
            return new TaskHistory
            {
                JiraKey = issue.Key,
                Title = issue.Summary,
                Description = issue.Description,
                Type = issue.IssueType,
                Status = issue.Status
            };
        }

        private async Task GenerateOutputFilesAsync(List<FeatureDocumentation> features)
        {
            switch (_settings.OutputFormat.ToLowerInvariant())
            {
                case "markdown":
                default:
                    var markdownWriter = new MarkdownDocumentWriter(_settings.OutputPath);
                    await markdownWriter.WriteFeatureDocumentationAsync(features);
                    break;
                case "html":
                    // Generate HTML output
                    await GenerateHtmlOutputAsync(features);
                    break;
            }
        }

        private async Task GenerateHtmlOutputAsync(List<FeatureDocumentation> features)
        {
            Directory.CreateDirectory(_settings.OutputPath);
            
            foreach (var feature in features)
            {
                var html = new StringBuilder();
                html.AppendLine("<!DOCTYPE html>");
                html.AppendLine("<html><head><title>" + feature.FeatureName + "</title></head>");
                html.AppendLine("<body>");
                html.AppendLine($"<h1>{feature.FeatureName}</h1>");
                html.AppendLine($"<p><strong>Epic:</strong> {feature.EpicKey}</p>");
                html.AppendLine($"<h2>Overview</h2><p>{feature.AIGeneratedSummary}</p>");
                html.AppendLine($"<h2>Business Context</h2><p>{feature.BusinessContext}</p>");
                html.AppendLine("</body></html>");

                var fileName = feature.FeatureName.Replace(" ", "-").ToLowerInvariant() + ".html";
                await File.WriteAllTextAsync(Path.Combine(_settings.OutputPath, fileName), html.ToString());
            }
        }
    }
}