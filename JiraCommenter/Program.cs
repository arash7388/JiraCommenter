using JiraCommenter.Documentation;
using JiraCommenter.Models;
using JiraCommenter.Services;
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

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        LoadConfig();

        //if (args.Contains("--generate-docs"))
        //{
        //    Console.WriteLine("Starting documentation generation...");
        //    var documentationGenerator = new DocumentationGenerator(_config);
        //    await documentationGenerator.GenerateDocumentationAsync();
        //    Console.WriteLine("Documentation generation complete.");
        //    return;
        //}

        Console.WriteLine("Bitbucket AI Commenter started...");
        Console.WriteLine($"Monitoring {_config.Bitbucket.RepoSlugs.Count} repositories");

        // Get mode selection
        Console.WriteLine("\nSelect mode:");
        Console.WriteLine("1. Full PR Analysis (default)");
        Console.WriteLine("2. Deployment Tips Only");
        Console.Write("Enter mode (1 or 2): ");
        string modeInput = Console.ReadLine()?.Trim();
        bool deploymentTipsOnly = modeInput == "2";

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
            await ProcessSpecificTask(specificJiraKey, deploymentTipsOnly);
        }
        else
        {
            Console.WriteLine("Processing all recent PRs...");
            DateTime lastCheck = DateTime.UtcNow.AddMinutes(-10);
            while (true)
            {
                try
                {
                    await CheckMergedPRsAcrossRepos(lastCheck, deploymentTipsOnly);
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

    static async Task ProcessSpecificTask(string jiraKey, bool deploymentTipsOnly = false)
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
                await ProcessJiraTask(jiraKey, prsForTask, deploymentTipsOnly);
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
    static async Task CheckMergedPRsAcrossRepos(DateTime lastCheck, bool deploymentTipsOnly = false)
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
            await ProcessJiraTask(jiraKey, prs, deploymentTipsOnly);
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
    static async Task ProcessJiraTask(string jiraKey, List<PRInfo> prs, bool deploymentTipsOnly = false)
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

        // Generate AI comment
        string aiComment;
        if (deploymentTipsOnly)
        {
            Console.WriteLine("\n🚀 Generating deployment tips only...");
            aiComment = await GenerateDeploymentTips(
                $"{jiraKey} - {prs.Count} PR(s) across {prsByRepo.Count} repo(s)",
                consolidatedDescription,
                consolidatedDiff
            );
        }
        else
        {
            Console.WriteLine("\n🤖 Generating AI summary for all PRs...");
            aiComment = await GenerateAIComment(
                $"{jiraKey} - {prs.Count} PR(s) across {prsByRepo.Count} repo(s)",
                consolidatedDescription,
                consolidatedDiff
            );
        }

        // Save and post
        string outputFileName = deploymentTipsOnly ? $"deployment_{jiraKey}.txt" : $"output_{jiraKey}.txt";
        await File.WriteAllTextAsync(outputFileName, aiComment, Encoding.UTF8);
        Console.WriteLine($"💾 Output saved to: {outputFileName}\n");

        // Automatically post to Jira (no confirmation needed)
        string prIds = string.Join(", ", prs.Select(p => $"#{p.Id}"));
        await PostToJira(jiraKey, aiComment, prIds);
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
            model = _config.AI.Model, // gpt-5-mini
            messages = new[]
            {
                new { role = "system", content = _config.AI.SystemPrompt },
                new { role = "user", content = string.Format(_config.AI.UserPromptTemplate, title, description, diff) }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await http.PostAsync(_config.AI.Endpoint, content);

        var raw = await res.Content.ReadAsStringAsync();

        // Validate HTTP status
        if (!res.IsSuccessStatusCode)
        {
            var snippet = raw.Length > 300 ? raw.Substring(0, 300) + "..." : raw;
            Console.WriteLine($"❌ AI API error: {(int)res.StatusCode} {res.ReasonPhrase}");
            Console.WriteLine($"   Response snippet: {snippet}");
            return $"AI request failed with status {(int)res.StatusCode} {res.ReasonPhrase}.";
        }

        // Validate content type
        var contentType = res.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var snippet = raw.Length > 300 ? raw.Substring(0, 300) + "..." : raw;
            Console.WriteLine("❌ AI API returned non-JSON content.");
            Console.WriteLine($"   Content-Type: {contentType}");
            Console.WriteLine($"   Response snippet: {snippet}");
            return "AI response was not JSON; unable to parse.";
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            var snippet = raw.Length > 300 ? raw.Substring(0, 300) + "..." : raw;
            Console.WriteLine($"❌ Failed to parse AI JSON: {ex.Message}");
            Console.WriteLine($"   Response snippet: {snippet}");
            return "Failed to parse AI response JSON.";
        }

        // Extract text for OpenAI and Azure OpenAI variants
        string text = null;

        // OpenAI-style: choices[0].message.content
        if (json.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var contentProp) &&
                contentProp.ValueKind == JsonValueKind.String)
            {
                text = contentProp.GetString();
            }
            // Some responses may use 'text' instead of 'content'
            else if (first.TryGetProperty("text", out var textProp) &&
                     textProp.ValueKind == JsonValueKind.String)
            {
                text = textProp.GetString();
            }
        }

        // Azure OpenAI chat completions sometimes use 'choices[0].messages' or different shapes; if needed, add more guards here.

        if (string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine("⚠️ AI response did not contain expected content.");
            return "AI response missing 'content' in choices.";
        }

        // Parse usage and print estimated cost
        try
        {
            if (json.RootElement.TryGetProperty("usage", out var usage))
            {
                int promptTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                int completionTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                int totalTokens = usage.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : promptTokens + completionTokens;

                var pricing = new Dictionary<string, (double inputPer1M, double outputPer1M, string note)>(StringComparer.OrdinalIgnoreCase)
                {
                    { "gpt-5-mini", (0.15, 0.60, "estimated") },
                    { "gpt-4o-mini", (0.15, 0.60, "estimated") },
                    { "gpt-4o", (2.50, 5.00, "estimated") },
                    { "gpt-4.1-mini", (0.15, 0.60, "estimated") },
                    { "gpt-4.1", (5.00, 15.00, "estimated") }
                };

                var model = _config.AI.Model ?? "";
                if (!pricing.TryGetValue(model, out var price))
                {
                    price = (0.15, 0.60, "estimated");
                }

                double inputCost = (promptTokens / 1_000_000.0) * price.inputPer1M;
                double outputCost = (completionTokens / 1_000_000.0) * price.outputPer1M;
                double totalCost = inputCost + outputCost;

                Console.WriteLine($"💲 OpenAI usage — Model: {model}");
                Console.WriteLine($"   Prompt tokens: {promptTokens}, Completion tokens: {completionTokens}, Total: {totalTokens}");
                Console.WriteLine($"   Estimated cost: ${totalCost:F6} USD (input ${inputCost:F6} + output ${outputCost:F6}) [{price.note}]");
            }
            else
            {
                Console.WriteLine("ℹ️ OpenAI response did not include usage; cost cannot be estimated.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to compute OpenAI cost: {ex.Message}");
        }

        return text.Trim();
    }

    // 🚀 تولید تیپس مربوط به استقرار فقط
    static async Task<string> GenerateDeploymentTips(string title, string description, string diff)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.AI.Token);

        // Deployment-focused system prompt
        string deploymentSystemPrompt = @"You are a deployment specialist. Analyze the code changes and provide ONLY deployment-related tips in Persian. Focus on:

1. **تنظیمات (Configuration)**: Any appsettings.json or configuration file changes
2. **دسترسی‌ها (Authorization)**: New claims, policies, or authentication requirements
3. **متغیرهای محیطی (Environment Variables)**: Required environment variables
4. **نوع احراز هویت (Authentication Type)**: Find [Authorize(AuthenticationSchemes = ***)] and mention the service schema (Thing, XBank, Signature, Wepod)

Format the output as:
**🚀 ملاحظات استقرار**

List each deployment consideration as a bullet point with clear, actionable information.

IMPORTANT ABOUT CLAIMS: 
- Carefully check the 'Authorization Claims' section in the input
- If it says 'NO new authorization claims detected', DO NOT mention any claims
- ONLY mention claims if specific claim names are listed
- Format: 'دسترسی‌های جدید مورد نیاز: claim1, claim2'

If NO deployment-related changes are found, simply respond with:
'**🚀 ملاحظات استقرار**
- تغییرات این PR نیازی به اقدام خاصی در زمان استقرار ندارد.'";

        var body = new
        {
            model = _config.AI.Model,
            messages = new[]
            {
                new { role = "system", content = deploymentSystemPrompt },
                new { role = "user", content = $"Task: {title}\n\nPR Information:\n{description}\n\nCode Diffs:\n{diff}" }
            }
        };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await http.PostAsync(_config.AI.Endpoint, content);

        var raw = await res.Content.ReadAsStringAsync();

        // Validate HTTP status
        if (!res.IsSuccessStatusCode)
        {
            var snippet = raw.Length > 300 ? raw.Substring(0, 300) + "..." : raw;
            Console.WriteLine($"❌ AI API error: {(int)res.StatusCode} {res.ReasonPhrase}");
            Console.WriteLine($"   Response snippet: {snippet}");
            return $"AI request failed with status {(int)res.StatusCode} {res.ReasonPhrase}.";
        }

        // Validate content type
        var contentType = res.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var snippet = raw.Length > 300 ? raw.Substring(0, 300) + "..." : raw;
            Console.WriteLine("❌ AI API returned non-JSON content.");
            Console.WriteLine($"   Content-Type: {contentType}");
            Console.WriteLine($"   Response snippet: {snippet}");
            return "AI response was not JSON; unable to parse.";
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            var snippet = raw.Length > 300 ? raw.Substring(0, 300) + "..." : raw;
            Console.WriteLine($"❌ Failed to parse AI JSON: {ex.Message}");
            Console.WriteLine($"   Response snippet: {snippet}");
            return "Failed to parse AI response JSON.";
        }

        // Extract text
        string text = null;

        if (json.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var contentProp) &&
                contentProp.ValueKind == JsonValueKind.String)
            {
                text = contentProp.GetString();
            }
            else if (first.TryGetProperty("text", out var textProp) &&
                     textProp.ValueKind == JsonValueKind.String)
            {
                text = textProp.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine("⚠️ AI response did not contain expected content.");
            return "AI response missing 'content' in choices.";
        }

        // Parse usage and print cost
        try
        {
            if (json.RootElement.TryGetProperty("usage", out var usage))
            {
                int promptTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                int completionTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                int totalTokens = usage.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : promptTokens + completionTokens;

                var pricing = new Dictionary<string, (double inputPer1M, double outputPer1M, string note)>(StringComparer.OrdinalIgnoreCase)
                {
                    { "gpt-5-mini", (0.15, 0.60, "estimated") },
                    { "gpt-4o-mini", (0.15, 0.60, "estimated") },
                    { "gpt-4o", (2.50, 5.00, "estimated") },
                    { "gpt-4.1-mini", (0.15, 0.60, "estimated") },
                    { "gpt-4.1", (5.00, 15.00, "estimated") }
                };

                var model = _config.AI.Model ?? "";
                if (!pricing.TryGetValue(model, out var price))
                {
                    price = (0.15, 0.60, "estimated");
                }

                double inputCost = (promptTokens / 1_000_000.0) * price.inputPer1M;
                double outputCost = (completionTokens / 1_000_000.0) * price.outputPer1M;
                double totalCost = inputCost + outputCost;

                Console.WriteLine($"💲 OpenAI usage — Model: {model}");
                Console.WriteLine($"   Prompt tokens: {promptTokens}, Completion tokens: {completionTokens}, Total: {totalTokens}");
                Console.WriteLine($"   Estimated cost: ${totalCost:F6} USD (input ${inputCost:F6} + output ${outputCost:F6}) [{price.note}]");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Failed to compute OpenAI cost: {ex.Message}");
        }

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
                if (body.Contains("🤖 *AI Product Summary:*") || body.Contains("🚀 *Deployment Tips:*"))
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

        // Determine comment header based on content type
        string header = comment.Contains("🚀 ملاحظات استقرار")
            ? $"🚀 *Deployment Tips:*\n*PRs:* {prIds}\n\n"
            : $"🤖 *AI Product Summary:*\n*PRs:* {prIds}\n\n";

        var payload = new { body = header + comment };
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

    static async Task GenerateDocumentation(AppSettings settings, BitbucketClient bitbucketClient, JiraClient jiraClient, AIClient aiClient)
    {
        Console.WriteLine("🚀 Starting documentation generation...");

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