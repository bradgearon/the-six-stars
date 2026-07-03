namespace Wds.SixStars.Api.Models
{
    public sealed class RelationshipDelta
    {
        public int Heart { get; set; }
        public int Trust { get; set; }
        public int Romance { get; set; }
        public int BenaveilInfluence { get; set; }

        public static RelationshipDelta Zero()
        {
            return new RelationshipDelta();
        }
    }
}
