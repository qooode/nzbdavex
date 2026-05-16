namespace NzbWebDAV.Config;

public class ProfileConfig
{
    public List<Profile> Profiles { get; set; } = [];

    public class Profile
    {
        public required string Token { get; set; }
        public required string Name { get; set; }
        public List<string> IndexerNames { get; set; } = [];
    }
}
