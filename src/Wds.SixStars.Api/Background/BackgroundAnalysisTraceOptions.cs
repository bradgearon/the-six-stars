namespace Wds.SixStars.Api.Background
{
    /// <summary>
    /// Optional disk tracing for the primary background memory-review lifecycle. This is deliberately
    /// off by default because normal LLM exchange logging already captures request/response bodies;
    /// this trace focuses on scheduler, cancellation, wait, and durable-commit timing.
    /// </summary>
    public sealed class BackgroundAnalysisTraceOptions
    {
        public bool Enabled { get; set; }
        public string LogFilePath { get; set; } = "logs/background-analysis-trace.log";
    }
}
