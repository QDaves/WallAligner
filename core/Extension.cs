using Xabbo;
using Xabbo.GEarth;
using Xabbo.Core;
using Xabbo.Core.Game;
using Xabbo.Core.GameData;

namespace WallAligner.Core;

public class Extension : GEarthExtension
{
    public Hotel CurrentHotel { get; private set; } = Hotel.None;
    public RoomManager Room { get; private set; }
    public GameDataManager Gamedata { get; private set; }
    public bool Gamedataloaded { get; private set; } = false;
    private bool isloading = false;
    private Hotel loadedhotel = Hotel.None;

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
        if (isloading) return;

        try
        {
            isloading = true;

            if (CurrentHotel == Hotel.None) return;

            if (Gamedataloaded && loadedhotel == CurrentHotel) return;

            if (loadedhotel != CurrentHotel)
            {
                Gamedataloaded = false;
                Gamedata = new GameDataManager();
            }

            await Gamedata.LoadAsync(CurrentHotel);

            if (Gamedata.Furni != null && Gamedata.Texts != null)
            {
                Extensions.Initialize(Gamedata);
                Gamedataloaded = true;
                loadedhotel = CurrentHotel;
            }
        }
        catch { }
        finally
        {
            isloading = false;
        }
    }

    protected override void OnConnected(ConnectedEventArgs e)
    {
        base.OnConnected(e);
        CurrentHotel = Hotel.FromGameHost(e.Host);
        Task.Run(() => loadfurnidata());
    }
}
