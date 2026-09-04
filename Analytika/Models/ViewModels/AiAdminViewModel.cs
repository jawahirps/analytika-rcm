using Analytika.Services;

namespace Analytika.Models.ViewModels;

public class AiAdminViewModel
{
    public AiSettings Settings { get; set; } = new();

    // Usage governance snapshot
    public int RequestsToday { get; set; }
    public long TokensThisMonth { get; set; }
    public int RequestsThisMonth { get; set; }
    public int TotalRequests { get; set; }
    public double SuccessRate { get; set; }   // 0..100

    public List<AiUsageLog> RecentLogs { get; set; } = new();

    /// <summary>
    /// Local Ollama model settings (SystemSettings category "Ollama"). These are a
    /// SEPARATE brain from the cloud agent above: BixAssistantService and
    /// DenialAnalystService read this category, while Settings/AiSettings drives the
    /// cloud (NVIDIA) agent. Without this section the local model ran on hardcoded
    /// fallbacks with no admin visibility, so the page disagreed with reality.
    /// </summary>
    public OllamaAdminSettings Ollama { get; set; } = new();
}

public class OllamaAdminSettings
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:7b-instruct";
    public double Temperature { get; set; } = 0.1;
    public int NumCtx { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 120;
    /// <summary>True when values come from hardcoded defaults (no rows saved yet).</summary>
    public bool IsUsingDefaults { get; set; }
    public bool? Reachable { get; set; }
}
