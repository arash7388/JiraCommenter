namespace JiraCommenter.Models
{
    public class FeatureDocumentation
    {
        public string FeatureName { get; set; }
        public string EpicKey { get; set; }
        public string Description { get; set; }
        public string BusinessContext { get; set; }
        public List<TaskHistory> TaskHistory { get; set; } = new();
        public List<PullRequestSummary> RelatedPRs { get; set; } = new();
        public string AIGeneratedSummary { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class TaskHistory
    {
        public string JiraKey { get; set; }
        public string Title { get; set; }
        public string Type { get; set; } // Story, Bug, Task
        public string Status { get; set; }
        public DateTime CompletedDate { get; set; }
        public string Description { get; set; }
        public List<string> AcceptanceCriteria { get; set; } = new();
        public string Developer { get; set; }
    }

    public class PullRequestSummary
    {
        public int PrId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public List<string> FilesChanged { get; set; } = new();
        public string Author { get; set; }
        public DateTime MergedDate { get; set; }
        public List<string> LinkedJiraKeys { get; set; } = new();
    }

    public class DocumentationConfig
    {
        public string OutputFormat { get; set; } = "markdown"; // markdown, html, confluence
        public string OutputPath { get; set; } = "./docs";
        public bool GroupByEpic { get; set; } = true;
        public bool IncludeCodeChangeSummary { get; set; } = true;
        public bool GenerateGlossary { get; set; } = true;
    }
}