using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Wds.SixStars.Api.Models;
using Wds.SixStars.Api.Stars;

namespace Wds.SixStars.Api.Ai
{
    /// <summary>
    /// Small append-only audit log for the foreground delimiter stream after mediation.
    /// The ordinary LLM exchange log already contains the complete raw SSE result; this trace
    /// adds the missing diagnostic layer: which § chunks became speech, which ~ controls were
    /// accepted/rejected, and which information needs actually reached runtime selection.
    /// Logging is best-effort and must never interfere with live conversation.
    /// </summary>
    internal static class StreamedResponseTraceLogger
    {
        private static readonly object Gate = new object();

        public static string Append(
            StarProfile profile,
            StarInteractionRequest request,
            string purpose,
            string status,
            string rawStream,
            IReadOnlyList<string>? spokenParts,
            string outgoingUnresolvedVector,
            IReadOnlyList<InformationNeedVectorItem>? informationNeeds,
            IReadOnlyList<string>? mediationTrace,
            string? error = null)
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "logs", "streamed-responses"));
                Directory.CreateDirectory(root);
                DateTimeOffset now = DateTimeOffset.Now;
                string path = Path.Combine(root, now.ToString("yyyy-MM-dd") + ".log");

                List<InformationNeedVectorItem> needs = (informationNeeds ?? Array.Empty<InformationNeedVectorItem>()).ToList();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("====================================================================================================");
                sb.AppendLine("STREAMED STAR RESPONSE TRACE");
                sb.AppendLine("LoggedAt: " + now.ToString("O"));
                sb.AppendLine("Star: " + (profile?.StarId ?? request?.StarId ?? string.Empty));
                sb.AppendLine("RequestType: " + (request == null ? string.Empty : request.RequestType.ToString()));
                sb.AppendLine("Purpose: " + (purpose ?? string.Empty));
                sb.AppendLine("Status: " + (status ?? string.Empty));
                sb.AppendLine("PlayerText: " + OneLine(request?.PlayerText));
                bool rawContainsInfo = (rawStream ?? string.Empty).IndexOf("~i^", StringComparison.OrdinalIgnoreCase) >= 0
                    || (rawStream ?? string.Empty).IndexOf("~info^", StringComparison.OrdinalIgnoreCase) >= 0;
                sb.AppendLine("RawContainsInfoControl: " + (rawContainsInfo ? "true" : "false"));
                sb.AppendLine("ParsedInformationNeedCount: " + needs.Count);
                if (!string.IsNullOrWhiteSpace(error)) sb.AppendLine("Error: " + OneLine(error));
                sb.AppendLine("----------------------------------------------------------------------------------------------------");
                sb.AppendLine("RAW OUTGOING STREAM");
                sb.AppendLine(rawStream ?? string.Empty);
                sb.AppendLine("----------------------------------------------------------------------------------------------------");
                sb.AppendLine("MEDIATED SPEECH CHUNKS");
                if (spokenParts == null || spokenParts.Count == 0) sb.AppendLine("(none)");
                else
                {
                    for (int i = 0; i < spokenParts.Count; i++) sb.AppendLine((i + 1) + ": " + spokenParts[i]);
                }

                sb.AppendLine("----------------------------------------------------------------------------------------------------");
                sb.AppendLine("MEDIATION TRACE");
                if (mediationTrace == null || mediationTrace.Count == 0) sb.AppendLine("(none)");
                else
                {
                    foreach (string item in mediationTrace) sb.AppendLine("- " + item);
                }

                sb.AppendLine("----------------------------------------------------------------------------------------------------");
                sb.AppendLine("PARSED INFORMATION NEEDS");
                if (needs.Count == 0) sb.AppendLine("(none)");
                else
                {
                    foreach (InformationNeedVectorItem need in needs)
                    {
                        sb.AppendLine("- source=" + OneLine(need.DataType) + "; subject=" + OneLine(need.Query) + "; score=" + need.Score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                sb.AppendLine("QuestionsToLastCatVector: " + (outgoingUnresolvedVector ?? string.Empty));
                sb.AppendLine("======================================== END STREAM TRACE ==========================================");

                lock (Gate)
                {
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                }

                return path;
            }
            catch
            {
                return string.Empty;
            }
        }


        public static void AppendInformationRoutingDecision(
            StarProfile profile,
            StarInteractionRequest request,
            IReadOnlyList<InformationNeedVectorItem>? needs,
            IReadOnlyList<InformationNeedAvailabilityItem>? availability,
            InformationNeedSelectionResult? decision,
            int attachedContextChars)
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "logs", "streamed-responses"));
                Directory.CreateDirectory(root);
                DateTimeOffset now = DateTimeOffset.Now;
                string path = Path.Combine(root, now.ToString("yyyy-MM-dd") + ".log");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine();
                sb.AppendLine("------------------------------- INFORMATION ROUTING DECISION --------------------------------------");
                sb.AppendLine("LoggedAt: " + now.ToString("O"));
                sb.AppendLine("Star: " + (profile?.StarId ?? request?.StarId ?? string.Empty));
                sb.AppendLine("PlayerText: " + OneLine(request?.PlayerText));
                sb.AppendLine("Needs: " + string.Join(" | ", (needs ?? Array.Empty<InformationNeedVectorItem>()).Select(x => OneLine(x.DataType) + "^" + OneLine(x.Query) + "^" + x.Score.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))));
                sb.AppendLine("Availability: " + string.Join(" | ", (availability ?? Array.Empty<InformationNeedAvailabilityItem>()).Select(x => OneLine(x.DataType) + "=" + (x.Available ? "available" : "missing") + "#" + x.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                if (decision == null)
                {
                    sb.AppendLine("Decision: (none)");
                }
                else
                {
                    sb.AppendLine("Decision.ShouldRequestMoreInformation: " + (decision.ShouldRequestMoreInformation ? "true" : "false"));
                    sb.AppendLine("Decision.SelectedDataType: " + OneLine(decision.SelectedDataType));
                    sb.AppendLine("Decision.Subject: " + OneLine(decision.Subject));
                    sb.AppendLine("Decision.Reason: " + OneLine(decision.Reason));
                }
                sb.AppendLine("AttachedContextChars: " + attachedContextChars);
                sb.AppendLine("----------------------------- END INFORMATION ROUTING DECISION ------------------------------------");

                lock (Gate)
                {
                    File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch
            {
                // Diagnostics must never interfere with the reply path.
            }
        }

        private static string OneLine(string? value)
        {
            string text = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ");
            return text;
        }
    }
}
