using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Wds.SixStars.Api.Forge.Context
{
    /// <summary>
    /// Compact plain-text renderer for LLM context. Card construction and presentation are deliberately separate
    /// so a future Forge transport can render the same card as JSON, a remote packet, or a smaller token-budget view.
    /// </summary>
    public sealed class CompactForgeContextCardRenderer : IForgeContextCardRenderer
    {
        public string Render(ForgeContextCard card)
        {
            if (card == null) return string.Empty;
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(card.Title)) sb.AppendLine(card.Title.Trim());
            foreach (ForgeContextField field in card.Fields ?? new List<ForgeContextField>())
            {
                if (field == null || string.IsNullOrWhiteSpace(field.Name) || string.IsNullOrWhiteSpace(field.Value)) continue;
                sb.AppendLine(field.Name.Trim() + ": " + field.Value.Trim());
            }
            foreach (KeyValuePair<string, List<string>> list in card.Lists ?? new Dictionary<string, List<string>>())
            {
                List<string> values = (list.Value ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (values.Count == 0) continue;
                sb.AppendLine(list.Key + ":");
                foreach (string value in values) sb.AppendLine("- " + value.Trim());
            }
            return sb.ToString().TrimEnd();
        }
    }
}
