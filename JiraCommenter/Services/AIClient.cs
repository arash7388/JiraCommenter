namespace JiraCommenter.Services
{
    public abstract class AIClient
    {
        public abstract Task<string> GenerateAsync(string prompt);
    }
}
