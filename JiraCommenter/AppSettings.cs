using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JiraCommenter
{
    public class AppSettings
    {
        public BitbucketSettings Bitbucket { get; set; } = new();
        public JiraSettings Jira { get; set; } = new();
        public AISettings AI { get; set; } = new();
        public AppConfig App { get; set; } = new();
        public DocumentationSettings Documentation { get; set; } = new(); 
    }

    public class BitbucketSettings
    {
        public string BaseUrl { get; set; }
        public string Token { get; set; }
        public string ProjectKey { get; set; }
        public List<string> RepoSlugs { get; set; } = new();
    }

    public class JiraSettings
    {
        public string BaseUrl { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
    }

    public class AISettings
    {
        public string Endpoint { get; set; }
        public string Token { get; set; }
        public string Model { get; set; }
        public string SystemPrompt { get; set; }
        public string UserPromptTemplate { get; set; }
        public bool IncludeContextLines { get; set; } = false;
    }

    public class AppConfig
    {
        public int CheckIntervalMinutes { get; set; }
    }

    public class DocumentationSettings
    {
        public string OutputPath { get; set; } = "./generated-docs";
        public string OutputFormat { get; set; } = "markdown";
        public bool GroupByEpic { get; set; } = true;
        public bool IncludePRHistory { get; set; } = true;
        public bool GenerateChangeLog { get; set; } = true;
        public List<string> EpicKeysToDocument { get; set; } = new(); // Filter specific epics
        public string DocumentationPromptTemplate { get; set; }
    }

}