using System.Text;

namespace JiraCommenter.Documentation
{
    public class MarkdownDocumentWriter
    {
        private readonly string _outputPath;

        public MarkdownDocumentWriter(string outputPath)
        {
            _outputPath = outputPath;
            Directory.CreateDirectory(_outputPath);
        }

        public async Task WriteFeatureDocumentationAsync(List<FeatureDocumentation> features)
        {
            // Generate index/table of contents
            await WriteIndexAsync(features);

            // Generate individual feature pages
            foreach (var feature in features)
            {
                await WriteFeaturePageAsync(feature);
            }

            // Generate changelog
            await WriteChangeLogAsync(features);
        }

        private async Task WriteIndexAsync(List<FeatureDocumentation> features)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Product Documentation");
            sb.AppendLine();
            sb.AppendLine("## Features Overview");
            sb.AppendLine();
            sb.AppendLine("| Feature | Epic | Last Updated | Tasks Completed |");
            sb.AppendLine("|---------|------|--------------|-----------------|");

            foreach (var feature in features.OrderBy(f => f.FeatureName))
            {
                var fileName = SanitizeFileName(feature.FeatureName);
                sb.AppendLine($"| [{feature.FeatureName}](. /{fileName}. md) | {feature.EpicKey} | {feature.LastUpdated:yyyy-MM-dd} | {feature.TaskHistory.Count} |");
            }

            await File.WriteAllTextAsync(Path.Combine(_outputPath, "README.md"), sb.ToString());
        }

        private async Task WriteFeaturePageAsync(FeatureDocumentation feature)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# {feature.FeatureName}");
            sb.AppendLine();
            sb.AppendLine($"**Epic:** [{feature.EpicKey}](your-jira-url/browse/{feature.EpicKey})");
            sb.AppendLine($"**Last Updated:** {feature.LastUpdated:yyyy-MM-dd}");
            sb.AppendLine();

            sb.AppendLine("## Overview");
            sb.AppendLine();
            sb.AppendLine(feature.AIGeneratedSummary);
            sb.AppendLine();

            sb.AppendLine("## Business Context");
            sb.AppendLine();
            sb.AppendLine(feature.BusinessContext);
            sb.AppendLine();

            sb.AppendLine("## Implementation History");
            sb.AppendLine();
            sb.AppendLine("### Tasks & Stories");
            sb.AppendLine();

            foreach (var task in feature.TaskHistory.OrderByDescending(t => t.CompletedDate))
            {
                sb.AppendLine($"#### [{task.JiraKey}] {task.Title}");
                sb.AppendLine($"- **Type:** {task.Type}");
                sb.AppendLine($"- **Status:** {task.Status}");
                sb.AppendLine($"- **Completed:** {task.CompletedDate:yyyy-MM-dd}");
                sb.AppendLine($"- **Developer:** {task.Developer}");
                if (!string.IsNullOrEmpty(task.Description))
                {
                    sb.AppendLine($"- **Description:** {task.Description}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("### Related Pull Requests");
            sb.AppendLine();

            foreach (var pr in feature.RelatedPRs.OrderByDescending(p => p.MergedDate))
            {
                sb.AppendLine($"- **PR #{pr.PrId}:** {pr.Title}");
                sb.AppendLine($"  - Merged: {pr.MergedDate:yyyy-MM-dd} by {pr.Author}");
                sb.AppendLine($"  - Files changed: {pr.FilesChanged.Count}");
                sb.AppendLine();
            }

            var fileName = SanitizeFileName(feature.FeatureName);
            await File.WriteAllTextAsync(
                Path.Combine(_outputPath, $"{fileName}.md"),
                sb.ToString());
        }

        private async Task WriteChangeLogAsync(List<FeatureDocumentation> features)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Changelog");
            sb.AppendLine();

            var allPRs = features
                .SelectMany(f => f.RelatedPRs.Select(pr => new { Feature = f.FeatureName, PR = pr }))
                .OrderByDescending(x => x.PR.MergedDate)
                .GroupBy(x => x.PR.MergedDate.ToString("yyyy-MM"));

            foreach (var month in allPRs)
            {
                sb.AppendLine($"## {month.Key}");
                sb.AppendLine();

                foreach (var item in month)
                {
                    sb.AppendLine($"- **{item.Feature}**: {item.PR.Title} ({item.PR.MergedDate:yyyy-MM-dd})");
                }
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(Path.Combine(_outputPath, "CHANGELOG. md"), sb.ToString());
        }

        private string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries))
                .Replace(" ", "-")
                .ToLowerInvariant();
        }
    }
}