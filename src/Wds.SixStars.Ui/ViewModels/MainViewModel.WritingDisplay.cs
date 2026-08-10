using System;
using System.Linq;
using System.Text;
using Wds.SixStars.Api.Writings;

namespace Wds.SixStars.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private static bool IsConstellationStory(StarWritingCreateResult result)
    {
        return result != null
            && string.Equals(
                StarWritingOutputTypes.Normalize(result.OutputType),
                StarWritingOutputTypes.ConstellationStory,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildWritingGenerationStatus(string starId, StarWritingCreateResult result, string prefix = "")
    {
        string repairMessage = result.RepairedRatingCount > 0
            ? " Re-rated " + result.RepairedRatingCount + " older writings whose vectors were incomplete."
            : string.Empty;

        if (IsConstellationStory(result))
        {
            string persistence = result.PersistedCount > 0
                ? "The finished Constellation Story passed her persistence rules and was saved to the shelf."
                : "The finished Constellation Story was reviewed but did not pass her persistence rules.";
            return prefix + starId + " used " + result.WritingMethod
                + " and completed " + result.CompletedPassCount + " collaborative development phases. "
                + persistence + " " + result.State.PendingCount + " compact Sparks are waiting to be told."
                + repairMessage;
        }

        return prefix + starId + " used " + result.WritingMethod
            + " and completed " + result.CompletedPassCount + " of " + result.SelectedPassCount
            + " writing passes with " + result.AppliedImprovementCount + " accepted replacements; "
            + result.PersistedCount + " Sparks passed her persistence rules. "
            + result.State.PendingCount + " are new and waiting to be told."
            + repairMessage;
    }

    private static string BuildWritingGenerationDisplay(string starId, StarWritingCreateResult result)
    {
        StringBuilder output = new StringBuilder();
        if (IsConstellationStory(result))
        {
            StarWritingHistoryItem? item = result.Batch.Items.FirstOrDefault();
            output.AppendLine(starId + " created a collaborative Constellation Story:");
            if (item != null)
            {
                if (!string.IsNullOrWhiteSpace(item.Title))
                {
                    output.AppendLine(item.Title.Trim());
                    output.AppendLine();
                }
                output.AppendLine(item.Writing ?? string.Empty);
                output.AppendLine();
                output.AppendLine(item.Persisted
                    ? "Saved to the writing shelf by: " + string.Join(", ", item.MatchedPersistenceRuleIds ?? new System.Collections.Generic.List<string>())
                    : "Not persisted by this Star's current value rules.");
                if (item.ContributingStarIds != null && item.ContributingStarIds.Count > 0)
                {
                    output.AppendLine("Contributors: " + string.Join(", ", item.ContributingStarIds));
                }
            }
            return output.ToString().TrimEnd();
        }

        output.AppendLine(starId + " created five new Sparks:");
        output.AppendLine();
        for (int i = 0; i < result.Batch.Items.Count; i++)
        {
            StarWritingHistoryItem item = result.Batch.Items[i];
            output.Append(i + 1).Append(". [").Append(string.Join(" + ", item.WordsUsed)).AppendLine("]");
            output.AppendLine(item.Writing);
            output.AppendLine(item.Persisted
                ? "Saved to the writing shelf by: " + string.Join(", ", item.MatchedPersistenceRuleIds)
                : "Not persisted by this Star's current value rules.");
            output.AppendLine();
        }
        return output.ToString().TrimEnd();
    }
}
