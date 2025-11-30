using JiraCommenter;
using JiraCommenter.Documentation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

class Program
{
    static AppSettings _config;

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        LoadConfig();

        Console.WriteLine("Bitbucket AI Commenter started...");
        Console.WriteLine($"Monitoring {_config.Bitbucket.RepoSlugs.Count} repositories");

        // Get specific Jira task number from user
        Console.Write("Enter task number (e.g., 28667 for WEPOD-28667) or press Enter to process all: ");
        
        string input = Console.ReadLine()?.Trim();

        string specificJiraKey = null;

        if (!string.IsNullOrEmpty(input))
        {
            if (!input.Contains("-"))
            {
                specificJiraKey = $"WEPOD-{input}".ToUpperInvariant();
            }
            else
            {
                specificJiraKey = input.ToUpperInvariant();
            }
        }

        if (!string.IsNullOrEmpty(specificJiraKey))
        {
            Console.WriteLine($"Processing only PRs related to: {specificJiraKey}");
            await ProcessSpecificTask(specificJiraKey);
        }
        else
        {
            Console.WriteLine("Processing all recent PRs...");
            DateTime lastCheck = DateTime.UtcNow.AddMinutes(-10);
            while (true)
            {
                try
                {
                    await CheckMergedPRsAcrossRepos(lastCheck);
                    lastCheck = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromMinutes(_config.App.CheckIntervalMinutes));
            }
        }
    }

    static void LoadConfig()
    {
        var builder = new ConfigurationBuilder()
            //.SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false);

        var configuration = builder.Build();
        _config = configuration.Get<AppSettings>();
    }

    static async Task ProcessSpecificTask(string jiraKey)
    {
        try
        {
            // Collect all PRs for this specific Jira task across all repositories
            var prsForTask = await CollectPRsForJiraTaskAcrossRepos(jiraKey);

            if (prsForTask.Count == 0)
            {
                Console.WriteLine($"⚠️ No PR found for Jira task: {jiraKey}");
            }
            else
            {
                Console.WriteLine($"📦 Found {prsForTask.Count} PR(s) for {jiraKey} across all repositories");

                // Process all PRs together
                await ProcessJiraTask(jiraKey, prsForTask);
            }

            Console.WriteLine($"\n✅ Processing complete for {jiraKey}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error processing {jiraKey}: {ex.Message}");
        }

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }

    // 🔄 بررسی PRهای merge شده در تمام ریپوزیتوری‌ها
    static async Task CheckMergedPRsAcrossRepos(DateTime lastCheck)
    {
        // Group PRs by Jira task across all repositories
        var prsByJiraKey = new Dictionary<string, List<PRInfo>>();

        foreach (var repoSlug in _config.Bitbucket.RepoSlugs)
        {
            Console.WriteLine($"\n🔍 Checking repository: {repoSlug}");
            var prs = await GetMergedPRsFromRepo(repoSlug, lastCheck);

            foreach (var pr in prs)
            {
                string jiraKey = ExtractJiraKey(pr.Title, pr.Description);

                if (string.IsNullOrEmpty(jiraKey))
                {
                    Console.WriteLine($"⚠️ No JIRA issue key found in PR: {pr.Title} ({repoSlug})");
                    continue;
                }

                // Group PRs by Jira key
                if (!prsByJiraKey.ContainsKey(jiraKey))
                {
                    prsByJiraKey[jiraKey] = new List<PRInfo>();
                }

                prsByJiraKey[jiraKey].Add(pr);
            }
        }

        // Process each Jira task with its PRs
        foreach (var kvp in prsByJiraKey)
        {
            string jiraKey = kvp.Key;
            var prs = kvp.Value;

            Console.WriteLine($"\n🎯 Processing Jira task: {jiraKey} with {prs.Count} PR(s)");
            await ProcessJiraTask(jiraKey, prs);
        }
    }

    // 📋 جمع‌آوری تمام PRهای مربوط به یک تسک جیرا از تمام ریپوزیتوری‌ها
    static async Task<List<PRInfo>> CollectPRsForJiraTaskAcrossRepos(string jiraKey)
    {
        var allPRs = new List<PRInfo>();

        foreach (var repoSlug in _config.Bitbucket.RepoSlugs)
        {
            Console.WriteLine($"🔍 Searching in repository: {repoSlug}");
            var prs = await GetMergedPRsFromRepo(repoSlug, DateTime.MinValue);

            foreach (var pr in prs)
            {
                string prJiraKey = ExtractJiraKey(pr.Title, pr.Description);

                if (prJiraKey == jiraKey)
                {
                    allPRs.Add(pr);
                    Console.WriteLine($"  ✅ Found PR: {pr.Title}");
                }
            }
        }

        return allPRs;
    }

    // 🔍 دریافت PRهای merge شده از یک ریپوزیتوری خاص
    static async Task<List<PRInfo>> GetMergedPRsFromRepo(string repoSlug, DateTime lastCheck)
    {
        var prs = new List<PRInfo>();

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Bitbucket.Token);

            var url = $"{_config.Bitbucket.BaseUrl}/rest/api/latest/projects/{_config.Bitbucket.ProjectKey}/repos/{repoSlug}/pull-requests/?state=MERGED";
            var res = await http.GetStringAsync(url);

            using var jsonDoc = JsonDocument.Parse(res);

            foreach (var pr in jsonDoc.RootElement.GetProperty("values").EnumerateArray())
            {
                long updated = pr.GetProperty("updatedDate").GetInt64();
                DateTime updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(updated).UtcDateTime;

                // Check time filter
                if (lastCheck != DateTime.MinValue && updatedAt <= lastCheck)
                {
                    continue;
                }

                long prId = pr.GetProperty("id").GetInt64();
                string prTitle = pr.GetProperty("title").GetString() ?? "";
                string prDescription = pr.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                string author = pr.GetProperty("author").GetProperty("user").GetProperty("name").GetString();

                prs.Add(new PRInfo
                {
                    Id = prId,
                    Title = prTitle,
                    Description = prDescription,
                    Author = author,
                    UpdatedAt = updatedAt,
                    RepoSlug = repoSlug
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Error fetching PRs from {repoSlug}: {ex.Message}");
        }

        return prs;
    }

    // 🔄 پردازش یک تسک جیرا با تمام PRهای آن
    static async Task ProcessJiraTask(string jiraKey, List<PRInfo> prs)
    {
        var allCommits = new List<CommitInfo>();
        var allDiffs = new StringBuilder();
        var prSummaries = new StringBuilder();

        // Group PRs by repository
        var prsByRepo = prs.GroupBy(p => p.RepoSlug).ToList();

        Console.WriteLine($"\n📊 Summary: {prs.Count} PR(s) across {prsByRepo.Count} repository/repositories");

        // Process each PR
        for (int i = 0; i < prs.Count; i++)
        {
            var pr = prs[i];
            Console.WriteLine($"  ✅ Processing PR {i + 1}/{prs.Count}: {pr.Title} ({pr.RepoSlug}) by {pr.Author}");

            // Add PR summary
            prSummaries.AppendLine($"--- Pull Request {i + 1}: {pr.Title} ---");
            prSummaries.AppendLine($"Repository: {pr.RepoSlug}");
            prSummaries.AppendLine($"Author: {pr.Author}");
            prSummaries.AppendLine($"PR ID: #{pr.Id}");
            if (!string.IsNullOrWhiteSpace(pr.Description))
            {
                prSummaries.AppendLine($"Description: {pr.Description}");
            }
            prSummaries.AppendLine();

            // Get commits for this PR
            var commits = await GetPRCommits(pr.Id, pr.RepoSlug);
            Console.WriteLine($"    📦 Found {commits.Count} commit(s)");

            // Add commits with PR context
            foreach (var commit in commits)
            {
                commit.PRTitle = pr.Title;
                commit.RepoSlug = pr.RepoSlug;
                commit.PRID = pr.Id;
                allCommits.Add(commit);
            }

            // Get diff for this PR
            string prDiff = await GetPRDiff(pr.Id, pr.RepoSlug);
            allDiffs.AppendLine($"\n\n========== DIFF for PR #{pr.Id}: {pr.Title} (Repository: {pr.RepoSlug}) ==========\n");
            allDiffs.AppendLine(prDiff);
        }

        // Extract authorization claims from all diffs
        var allClaims = ExtractAuthorizationClaims(allDiffs.ToString());

        if (allClaims.Any())
        {
            Console.WriteLine($"🔐 Found {allClaims.Count} authorization claim(s): {string.Join(", ", allClaims)}");
        }
        else
        {
            Console.WriteLine("🔐 No authorization claims found in changes");
        }

        // Merge all information
        string consolidatedDescription = MergeAllPRInformation(prSummaries.ToString(), allCommits, allClaims);
        string consolidatedDiff = allDiffs.ToString();

        // Generate single AI comment for all PRs
        Console.WriteLine("\n🤖 Generating AI summary for all PRs...");
        string aiComment = await GenerateAIComment(
            $"{jiraKey} - {prs.Count} PR(s) across {prsByRepo.Count} repo(s)",
            consolidatedDescription,
            consolidatedDiff
        );

        // Save and post
        await File.WriteAllTextAsync($"output_{jiraKey}.txt", aiComment, Encoding.UTF8);
        Console.WriteLine($"💾 Output saved to: output_{jiraKey}.txt\n");

        // Check if we should post to Jira
        Console.Write("Post this comment to Jira? (y/n): ");
        var response = Console.ReadLine()?.Trim().ToLower();
        if (response == "y" || response == "yes")
        {
            // Use PR IDs instead of titles for the header
            string prIds = string.Join(", ", prs.Select(p => $"#{p.Id}"));
            await PostToJira(jiraKey, aiComment, prIds);
        }
    }

    // 🔐 استخراج Claim Requirements از diff
    static HashSet<string> ExtractAuthorizationClaims(string diff)
    {
        var claims = new HashSet<string>();

        // Only look for claims in added lines (starting with +)
        var diffLines = diff.Split('\n');
        var addedLines = diffLines.Where(line => line.TrimStart().StartsWith("+")).ToList();
        var addedContent = string.Join("\n", addedLines);

        // Pattern to match ClaimRequirement("claim_name") or similar patterns
        var patterns = new[]
        {
            @"ClaimRequirement\s*\(\s*""([^""]+)""\s*\)",  // ClaimRequirement("claim_name")
            @"ClaimRequirement\s*\(\s*'([^']+)'\s*\)",      // ClaimRequirement('claim_name')
            @"\[Claim\s*\(\s*""([^""]+)""\s*\)\]",          // [Claim("claim_name")]
            @"\[Authorize\s*\(\s*Policy\s*=\s*""([^""]+)""\s*\)\]" // [Authorize(Policy = "claim_name")]
        };

        foreach (var pattern in patterns)
        {
            var matches = Regex.Matches(addedContent, pattern, RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    string claim = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(claim))
                    {
                        claims.Add(claim);
                    }
                }
            }
        }

        return claims;
    }

    // 🔀 ادغام اطلاعات تمام PRها
    static string MergeAllPRInformation(string prSummaries, List<CommitInfo> allCommits, HashSet<string> authClaims)
    {
        var sb = new StringBuilder();

        // Add PR summaries
        sb.AppendLine("=== Pull Requests Overview ===");
        sb.AppendLine(prSummaries);

        // Add authorization claims if any found
        if (authClaims.Any())
        {
            sb.AppendLine("=== Authorization Claims (New APIs) ===");
            sb.AppendLine("The following authorization claims are required for new/modified APIs:");
            foreach (var claim in authClaims.OrderBy(c => c))
            {
                sb.AppendLine($"  • {claim}");
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("=== Authorization Claims ===");
            sb.AppendLine("NO new authorization claims detected in this change.");
            sb.AppendLine();
        }

        // Add all commits grouped by PR and repository
        if (allCommits.Any())
        {
            sb.AppendLine("=== All Commits ===");

            var commitsByPRAndRepo = allCommits
                .GroupBy(c => new { c.RepoSlug, c.PRTitle, c.PRID })
                .OrderBy(g => g.Key.RepoSlug);

            foreach (var group in commitsByPRAndRepo)
            {
                sb.AppendLine($"\nRepository: {group.Key.RepoSlug}");
                sb.AppendLine($"PR #{group.Key.PRID}: {group.Key.PRTitle ?? "Unknown PR"}");
                int index = 1;
                foreach (var commit in group)
                {
                    string shortCommitId = commit.Id.Length > 8 ? commit.Id.Substring(0, 8) : commit.Id;
                    sb.AppendLine($"  {index}. [{shortCommitId}] {commit.Message.Trim()}");
                    if (!string.IsNullOrEmpty(commit.Author))
                    {
                        sb.AppendLine($"     Author: {commit.Author}");
                    }
                    index++;
                }
            }
        }

        return sb.ToString();
    }

    // 🔍 دریافت تمام کامیت‌های یک PR
    static async Task<List<CommitInfo>> GetPRCommits(long prId, string repoSlug)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Bitbucket.Token);

        var url = $"{_config.Bitbucket.BaseUrl}/rest/api/latest/projects/{_config.Bitbucket.ProjectKey}/repos/{repoSlug}/pull-requests/{prId}/commits";
        var res = await http.GetStringAsync(url);

        var commits = new List<CommitInfo>();
        using var jsonDoc = JsonDocument.Parse(res);

        foreach (var commit in jsonDoc.RootElement.GetProperty("values").EnumerateArray())
        {
            string message = commit.GetProperty("message").GetString() ?? "";
            string commitId = commit.GetProperty("id").GetString() ?? "";
            string authorName = "";

            if (commit.TryGetProperty("author", out var author) &&
                author.TryGetProperty("name", out var name))
            {
                authorName = name.GetString() ?? "";
            }

            commits.Add(new CommitInfo
            {
                Id = commitId,
                Message = message,
                Author = authorName
            });
        }

        return commits;
    }

    // 🧠 گرفتن diff واقعی هر فایل
    static async Task<string> GetPRDiff(long prId, string repoSlug)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Bitbucket.Token);

        var url = $"{_config.Bitbucket.BaseUrl}/rest/api/latest/projects/{_config.Bitbucket.ProjectKey}/repos/{repoSlug}/pull-requests/{prId}/diff";
        var diffResponse = await http.GetStringAsync(url);

        // Filter and simplify the diff to reduce size
        return SimplifyBitbucketDiff(diffResponse);
    }

    // 🔧 تبدیل diff پیچیده Bitbucket به فرمت ساده و کوچک
    static string SimplifyBitbucketDiff(string bitbucketDiff)
    {
        try
        {
            var jsonDoc = JsonDocument.Parse(bitbucketDiff);
            var sb = new StringBuilder();

            if (!jsonDoc.RootElement.TryGetProperty("diffs", out var diffs))
            {
                return bitbucketDiff; // Return original if not JSON format
            }

            foreach (var diff in diffs.EnumerateArray())
            {
                // Get file path - handle both new and deleted files
                string filePath = "unknown";

                // Try destination first (for new/modified files)
                if (diff.TryGetProperty("destination", out var destination) &&
                    destination.ValueKind != JsonValueKind.Null &&
                    destination.TryGetProperty("toString", out var destFilePathElement))
                {
                    filePath = destFilePathElement.GetString() ?? "unknown";
                }
                // Fall back to source for deleted files
                else if (diff.TryGetProperty("source", out var source) &&
                         source.ValueKind != JsonValueKind.Null &&
                         source.TryGetProperty("toString", out var srcFilePathElement))
                {
                    filePath = srcFilePathElement.GetString() ?? "unknown";
                }

                var fileContentBuilder = new StringBuilder();
                bool hasRelevantChanges = false;

                // Process hunks
                if (diff.TryGetProperty("hunks", out var hunks))
                {
                    foreach (var hunk in hunks.EnumerateArray())
                    {
                        var hunkBuilder = new StringBuilder();
                        bool hunkHasContent = false;

                        // Store hunk header temporarily
                        string hunkHeader = "";
                        if (hunk.TryGetProperty("sourceLine", out var sourceLine) &&
                            hunk.TryGetProperty("destinationLine", out var destLine) &&
                            hunk.TryGetProperty("sourceSpan", out var sourceSpan) &&
                            hunk.TryGetProperty("destinationSpan", out var destSpan))
                        {
                            hunkHeader = $"@@ -{sourceLine.GetInt32()},{sourceSpan.GetInt32()} +{destLine.GetInt32()},{destSpan.GetInt32()} @@";
                        }

                        // Process segments
                        if (hunk.TryGetProperty("segments", out var segments))
                        {
                            foreach (var segment in segments.EnumerateArray())
                            {
                                if (!segment.TryGetProperty("type", out var typeElement))
                                    continue;

                                string segmentType = typeElement.GetString() ?? "";

                                // Skip CONTEXT lines if configured
                                if (segmentType == "CONTEXT" && !_config.AI.IncludeContextLines)
                                    continue;

                                if (segment.TryGetProperty("lines", out var lines))
                                {
                                    foreach (var lineObj in lines.EnumerateArray())
                                    {
                                        if (lineObj.TryGetProperty("line", out var lineText))
                                        {
                                            string line = lineText.GetString() ?? "";

                                            // Skip non-meaningful lines
                                            if (IsNonMeaningfulLine(line))
                                                continue;

                                            // Add hunk header only when we have the first meaningful line
                                            if (!hunkHasContent && !string.IsNullOrEmpty(hunkHeader))
                                            {
                                                hunkBuilder.AppendLine(hunkHeader);
                                            }

                                            // Add appropriate prefix based on type
                                            string prefix = segmentType switch
                                            {
                                                "ADDED" => "+",
                                                "REMOVED" => "-",
                                                "CONTEXT" => " ",
                                                _ => " "
                                            };

                                            // Remove leading whitespace and add prefix
                                            hunkBuilder.AppendLine($"{prefix}{line.TrimStart()}");
                                            hunkHasContent = true;
                                        }
                                    }
                                }
                            }
                        }

                        if (hunkHasContent)
                        {
                            fileContentBuilder.Append(hunkBuilder.ToString());
                            hasRelevantChanges = true;
                        }
                    }
                }

                // Only add file if it has relevant changes
                if (hasRelevantChanges)
                {
                    sb.AppendLine($"--- {filePath}");
                    sb.AppendLine($"+++ {filePath}");
                    sb.Append(fileContentBuilder.ToString());
                    sb.AppendLine(); // Blank line between files
                }
            }

            return sb.ToString();
        }
        catch (JsonException)
        {
            // If parsing fails, assume it's already in plain text format
            return bitbucketDiff;
        }
    }

    // 🔍 بررسی اینکه آیا خط معنی‌دار است یا نه
    static bool IsNonMeaningfulLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        string trimmed = line.Trim();

        // Skip XML documentation comments
        if (trimmed.StartsWith("///"))
            return true;

        // Skip using statements
        if (trimmed.StartsWith("using ") && trimmed.EndsWith(";"))
            return true;

        // Skip import statements (for other languages)
        if (trimmed.StartsWith("import ") && trimmed.EndsWith(";"))
            return true;

        // Skip single braces
        if (trimmed == "{" || trimmed == "}" || trimmed == "};")
            return true;

        // Skip empty comments
        if (trimmed == "//" || trimmed == "/*" || trimmed == "*/" || trimmed == "*")
            return true;

        // Skip whitespace-only lines with common characters
        if (trimmed.All(c => char.IsWhiteSpace(c) || c == '{' || c == '}' || c == ';'))
            return true;

        return false;
    }

    // 🧠 فراخوانی AI برای ساخت خلاصه معنی‌دار
    static async Task<string> GenerateAIComment(string title, string description, string diff)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.AI.Token);

        var body = new
        {
            model = _config.AI.Model, // gpt-4o-mini
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = _config.AI.SystemPrompt
                },
                new
                {
                    role = "user",
                    content = string.Format(_config.AI.UserPromptTemplate, title, description, diff)
                }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await http.PostAsync(_config.AI.Endpoint, content);

        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        string text = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        return text.Trim();
    }

    static string ExtractJiraKey(string title, string description)
    {
        var text = (title + " " + description).ToUpperInvariant();
        var match = Regex.Match(text, @"[A-Z]+-\d+");
        return match.Success ? match.Value : null;
    }

    // 🔍 بررسی وجود کامنت تکراری
    static async Task<bool> HasDuplicateComment(string issueKey, string prIds)
    {
        try
        {
            using var http = new HttpClient();
            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_config.Jira.User}:{_config.Jira.Password}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

            var url = $"{_config.Jira.BaseUrl}/rest/api/2/issue/{issueKey}/comment";
            var res = await http.GetStringAsync(url);

            var json = JsonDocument.Parse(res);
            var comments = json.RootElement.GetProperty("comments");

            foreach (var comment in comments.EnumerateArray())
            {
                string body = comment.GetProperty("body").GetString() ?? "";

                // Check if comment is from AI bot
                if (body.Contains("🤖 *AI Product Summary:*"))
                {
                    // Check if it contains any of the PR IDs
                    var ids = prIds.Split(", ");
                    if (ids.Any(id => body.Contains(id)))
                    {
                        Console.WriteLine($"⚠️ Duplicate comment detected for task {issueKey}");
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Error checking for duplicates: {ex.Message}");
            return false; // Proceed with posting if we can't check
        }
    }

    // ✏️ ارسال خلاصه به JIRA Server
    static async Task PostToJira(string issueKey, string comment, string prIds)
    {
        // Check for duplicate comments first
        if (await HasDuplicateComment(issueKey, prIds))
        {
            Console.WriteLine($"⏭️ Skipping duplicate comment for {issueKey}");
            return;
        }

        using var http = new HttpClient();
        var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_config.Jira.User}:{_config.Jira.Password}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

        // Include PR IDs in the comment header instead of titles
        var payload = new { body = $"🤖 *AI Product Summary:*\n*PRs:* {prIds}\n\n{comment}" };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var url = $"{_config.Jira.BaseUrl}/rest/api/2/issue/{issueKey}/comment";
        var res = await http.PostAsync(url, content);

        if (res.IsSuccessStatusCode)
            Console.WriteLine($"📝 Comment added to {issueKey}");
        else
        {
            string error = await res.Content.ReadAsStringAsync();
            Console.WriteLine($"❌ Failed to add comment to {issueKey}: {res.StatusCode} | {error}");
        }
    }

    static async Task GenerateDocumentation(AppSettings settings)
    {
        Console.WriteLine("🚀 Starting documentation generation...");

        var bitbucketClient = new BitbucketClient(settings.Bitbucket);
        var jiraClient = new JiraClient(settings.Jira);
        var aiClient = new GeminiAIClient(settings.AI);

        var generator = new DocumentationGenerator(
            bitbucketClient,
            jiraClient,
            aiClient,
            settings.Documentation);

        var features = await generator.GenerateDocumentationAsync();

        Console.WriteLine($"✅ Generated documentation for {features.Count} features");
        Console.WriteLine($"📁 Output saved to: {settings.Documentation.OutputPath}");
    }

    // 📝 Helper class for PR information
    public class PRInfo
    {
        public long Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string Author { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string RepoSlug { get; set; }
    }

    // 📝 Helper class for commit information
    public class CommitInfo
    {
        public string Id { get; set; }
        public string Message { get; set; }
        public string Author { get; set; }
        public string PRTitle { get; set; }
        public string RepoSlug { get; set; }
        public long PRID { get; set; }
    }
}