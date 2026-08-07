namespace Wds.SixStars.Api.Models
{
    public enum RequestType
    {
        Chat = 0,
        Story = 1,
        Letter = 2,
        BattleUpdate = 3,
        Debug = 4,
        Photo = 5,
        /// <summary>System-initiated check asking whether the Star has a state-backed at-rest line to say. This is not a player message and must not change relationship/emotion state.</summary>
        StarInitiative = 6
    }
}
