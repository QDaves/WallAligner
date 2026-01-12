using Xabbo;
using Xabbo.GEarth;
using Xabbo.Core;
using Xabbo.Core.Game;
using Xabbo.Core.GameData;

namespace WallAligner.Core;

public class Extension : GEarthExtension
{
    public string Region { get; private set; } = "com";
    public RoomManager Room { get; private set; }
    public GameDataManager Gamedata { get; private set; }
    public bool Gamedataloaded { get; private set; } = false;

    public Extension() : base(new GEarthOptions
    {
        Name = "WallAligner",
        Description = "advanced wall item alignment tool",
        Author = "QDave",
        Version = "1.0.0"
    })
    {
        Room = new RoomManager(this);
        Gamedata = new GameDataManager();
    }

    private async void loadfurnidata()
    {
        try
        {
            var hotel = Hotel.All[Region];
            await Gamedata.LoadAsync(hotel);

            if (Gamedata.Furni != null && Gamedata.Texts != null)
            {
                Extensions.Initialize(Gamedata);
                Gamedataloaded = true;
            }
        }
        catch { }
    }

    protected override void OnConnected(ConnectedEventArgs e)
    {
        base.OnConnected(e);

        Region = e.Host switch
        {
            var host when host.Contains("game-br.") => "br",
            var host when host.Contains("game-tr.") => "tr",
            var host when host.Contains("game-es.") => "es",
            var host when host.Contains("game-fi.") => "fi",
            var host when host.Contains("game-it.") => "it",
            var host when host.Contains("game-nl.") => "nl",
            var host when host.Contains("game-de.") => "de",
            var host when host.Contains("game-fr.") => "fr",
            _ => "us"
        };

        Task.Run(() => loadfurnidata());
    }
}
