namespace Wds.SixStars.Api.Models
{
    /// <summary>
    /// One fully received non-speech control chunk from a delimiter-streamed Star response.
    /// The host never acts on a partial vector; the streaming mediator only publishes this after §.
    /// </summary>
    public sealed class StarStreamAction
    {
        public string ActionType { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Vector { get; set; } = string.Empty;
    }
}
