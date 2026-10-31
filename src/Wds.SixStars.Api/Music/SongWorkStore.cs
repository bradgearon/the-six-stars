using System;
using System.IO;
using System.Text.Json;

namespace Wds.SixStars.Api.Music
{
    public sealed class SongWork
    {
        public string Notation { get; set; } = "";
        public string OriginalBrief { get; set; } = "";
        public string LastRequest { get; set; } = "";
        public int Bars { get; set; } = 8;
        public int Tempo { get; set; } = 96;
        public string Meter { get; set; } = "4/4";
        public bool UseAhd { get; set; } = true;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
    public sealed class SongWorkStore
    {
        private readonly string _path;
        public SongWorkStore(string folder) { Directory.CreateDirectory(folder); _path = Path.Combine(folder, "current-song.json"); }
        public SongWork? Load() => File.Exists(_path) ? JsonSerializer.Deserialize<SongWork>(File.ReadAllText(_path)) : null;
        public void Save(SongWork song)
        {
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temporary, JsonSerializer.Serialize(song, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public void Clear() { if (File.Exists(_path)) File.Delete(_path); }
    }
}
