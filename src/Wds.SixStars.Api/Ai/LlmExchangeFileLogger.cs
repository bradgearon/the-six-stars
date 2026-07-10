using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Durable append-only logger for local LLM traffic. It writes one plain .log file per local day.
    /// The log intentionally stores the full formatted request/response text so prompt bloat can be reviewed later.
    /// </summary>
    public sealed class LlmExchangeFileLogger
    {
        private readonly object _gate = new object();
        private long _sequence;

        public LlmExchangeFileLogger(LocalChatModelOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            Enabled = options.LogExchangesToDisk;
            RootDirectory = ResolveLogDirectory(options.ExchangeLogDirectory);
            MaxTextChars = Math.Max(1000, options.ExchangeLogMaxTextChars);
        }

        public bool Enabled { get; }
        public string RootDirectory { get; }
        public int MaxTextChars { get; }

        public static string CreateExchangeId(string prefix)
        {
            string safePrefix = string.IsNullOrWhiteSpace(prefix) ? "llm" : SanitizePathToken(prefix);
            return safePrefix + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        public void Log(LocalChatModelExchangeEventArgs args)
        {
            if (!Enabled || args == null)
            {
                return;
            }

            try
            {
                DateTimeOffset loggedAt = args.LoggedAt;
                string day = loggedAt.ToLocalTime().ToString("yyyy-MM-dd");
                string logPath = Path.Combine(RootDirectory, day + ".log");
                string text = Truncate(args.FormattedText ?? string.Empty, MaxTextChars);
                long sequence = Interlocked.Increment(ref _sequence);
                string block = BuildLogBlock(args, loggedAt, sequence, text);

                lock (_gate)
                {
                    Directory.CreateDirectory(RootDirectory);
                    File.AppendAllText(logPath, block, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Logging must never break the live companion path.
            }
        }

        private static string BuildLogBlock(LocalChatModelExchangeEventArgs args, DateTimeOffset loggedAt, long sequence, string text)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("====================================================================================================");
            sb.AppendLine("LLM LOG ENTRY");
            sb.AppendLine("LoggedAt: " + loggedAt.ToString("O"));
            sb.AppendLine("Sequence: " + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine("RequestId: " + (args.RequestId ?? string.Empty));
            sb.AppendLine("Kind: " + (args.Kind ?? string.Empty));
            sb.AppendLine("Title: " + (args.Title ?? string.Empty));
            sb.AppendLine("DebugLabel: " + (args.DebugLabel ?? string.Empty));
            sb.AppendLine("ResponseFormatMode: " + (args.ResponseFormatMode ?? string.Empty));
            sb.AppendLine("TextChars: " + (args.FormattedText?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.AppendLine("====================================================================================================");
            sb.AppendLine(text ?? string.Empty);
            sb.AppendLine("========================================== END LLM LOG ENTRY =======================================");
            return sb.ToString();
        }

        private static string ResolveLogDirectory(string configured)
        {
            string value = string.IsNullOrWhiteSpace(configured) ? "logs/llm-exchanges" : configured.Trim();
            if (Path.IsPathRooted(value))
            {
                return Path.GetFullPath(value);
            }

            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, value));
        }

        private static string Truncate(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text ?? string.Empty;
            }

            return text.Substring(0, maxChars) + Environment.NewLine + "[llm log entry truncated at " + maxChars.ToString(System.Globalization.CultureInfo.InvariantCulture) + " chars]";
        }

        private static string SanitizePathToken(string value)
        {
            StringBuilder sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else sb.Append('-');
            }

            string result = sb.ToString().Trim('-');
            return string.IsNullOrWhiteSpace(result) ? "llm" : result;
        }
    }
}
