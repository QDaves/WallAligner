using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Xabbo.Core;
using Xabbo.Core.Game;
using APoint = Avalonia.Point;
using ARect = Avalonia.Rect;
using WpfPathGeometry = System.Windows.Media.PathGeometry;

namespace WallAligner.Core;

public partial class RoomCanvas : Control
{
    private class DraggedWallItem
    {
        public IWallItem item { get; set; } = null!;
        public WallLocation originallocation { get; set; }
        public WallLocation location { get; set; }
        public double originalheight { get; set; }
        public double originalvisualx { get; set; }
        public double originalvisualy { get; set; }
    }

    private const int HALF_W = 32;
    private const int HALF_H = 16;
    private const double BASE_OFFSET = 3.59375;

    public Extension? extension { get; set; }
    public Action<string>? statuschanged;
    public bool autoadjust { get; set; } = true;
    public bool placeontop { get; set; } = false;
    public IRoom? currentroom => _currentroom;

    private double zoomlevel = 1.0;
    private const double ZOOM_MIN = 0.333;
    private const double ZOOM_MAX = 3.0;

    private IRoom? _currentroom;
    private bool panning;
    private APoint panstart;
    private APoint offset = new APoint(0, 0);
    private bool selecting;
    private APoint selectstart;
    private APoint selectend;
    private HashSet<long> selected = new();
    private APoint dragstart;
    private List<WallLocation>? previewlocations;
    private ARect? previewbox;
    private bool previewboxdragging;
    private bool previewboxresizing;
    private int previewboxhandle = -1;
    private APoint previewboxdragstart;
    private APoint previewmouseoffset = new APoint(0, 0);
    private string? currenttext;
    private string? previewfont;
    private string? previewfurni;
    private string? currentfilepath;
    private string? currentpathdata;
    private bool isSvgMode;
    private bool isPathDataMode;
    private bool isDrawingMode;
    private List<DrawingObject> drawingobjects = new();
    private List<APoint> currentsegment = new();
    private bool isdrawing;
    private string? drawingfurni;
    private int drawingshapetype = 0;
    private DrawingObject? selectedobject;
    private bool draggingobject;
    private APoint objectdragstart;
    private APoint objectoriginalpos;
    private bool rotatingobject;
    private double objectoriginalrotation;
    private bool scalingobject;
    private double objectoriginalscale;
    private DateTime lastpreviewupdate = DateTime.MinValue;
    private const int PREVIEW_THROTTLE_MS = 100;
    public int previewitemlimit = 150;
    private List<WallLocation>? cachedlocations;
    private double cachedscale = -1;
    private int cachedstartx = int.MinValue;
    private int cachedstarty = int.MinValue;
    private string? cachedgeometrykey;
    private WpfPathGeometry? cachedgeometry;
    private HashSet<long> dragids = new();
    private DraggedWallItem[] dragitems = Array.Empty<DraggedWallItem>();
    private int highesttile;
    private List<APoint[]> floorpolygons = new();
    private Queue<(IWallItem[] items, List<WallLocation> locations)> placementqueue = new();
    private bool isplacing = false;
    private readonly object placementlock = new();

    private readonly Pen floorpen = new Pen(new SolidColorBrush(Color.FromArgb(80, 0, 200, 200)), 1);
    private readonly Pen itempen = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 200, 255)), 2);
    private readonly Brush itembrush = new SolidColorBrush(Color.FromArgb(80, 0, 150, 255));
    private readonly Pen selectedpen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 165, 0)), 2);
    private readonly Brush selectedbrush = new SolidColorBrush(Color.FromArgb(100, 255, 165, 0));

    public RoomCanvas()
    {
        ClipToBounds = true;
        Focusable = true;

        PointerPressed += onpointerdown;
        PointerReleased += onpointerup;
        PointerMoved += onpointermove;
        KeyDown += onkeydown;
    }

    public void setuproom()
    {
        if (extension?.Room == null) return;

        if (extension.Room.IsInRoom && extension.Room.Room != null)
        {
            _currentroom = extension.Room.Room;
            updatefloorplan();
            InvalidateVisual();
        }

        extension.Room.Entered += e =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _currentroom = e.Room;
                if (currentroom == null) return;
                updatefloorplan();
                InvalidateVisual();
            });
        };

        extension.Room.Left += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _currentroom = null;
                floorpolygons.Clear();
                selected.Clear();
                InvalidateVisual();
            });
        };

        extension.Room.WallItemAdded += e => Dispatcher.UIThread.Post(() => InvalidateVisual());
        extension.Room.WallItemUpdated += e => Dispatcher.UIThread.Post(() => InvalidateVisual());
        extension.Room.WallItemRemoved += e => Dispatcher.UIThread.Post(() => InvalidateVisual());
    }

    private void updatefloorplan()
    {
        if (currentroom == null) return;

        floorpolygons.Clear();
        highesttile = 0;

        var plan = currentroom.FloorPlan;

        for (int y = 0; y < plan.Size.Y; y++)
        {
            for (int x = 0; x < plan.Size.X; x++)
            {
                int h = plan[x, y];
                if (h > highesttile)
                    highesttile = h;

                if (h >= 0)
                {
                    var pts = gettilepoints(x, y, h);
                    floorpolygons.Add(pts);
                }
            }
        }
    }

    private APoint[] gettilepoints(int x, int y, int h)
    {
        int screenx = (x - y) * HALF_W;
        int screeny = (x + y) * HALF_H - (h * HALF_W);

        return new[]
        {
            new APoint(screenx, screeny - HALF_H),
            new APoint(screenx - HALF_W, screeny),
            new APoint(screenx, screeny + HALF_H),
            new APoint(screenx + HALF_W, screeny)
        };
    }

    private double calcwallheight(IRoom room, WallLocation loc)
    {
        var plan = room.FloorPlan;
        var entry = room.Entry;
        double result = BASE_OFFSET;

        if (plan.WallHeight != -1)
        {
            result = (entry == (loc.Wall.X, loc.Wall.Y))
                ? result + plan.WallHeight
                : result + (plan.WallHeight * 2);
        }
        else if (entry != (loc.Wall.X, loc.Wall.Y))
        {
            result += highesttile;
        }

        int tileh = -1;
        if (loc.Wall.X >= 0 && loc.Wall.Y >= 0 && loc.Wall.X < plan.Size.X && loc.Wall.Y < plan.Size.Y)
        {
            tileh = plan[loc.Wall.X, loc.Wall.Y];
        }
        else if (loc.Wall.X < 0 || loc.Wall.Y < 0 || loc.Wall.X >= plan.Size.X || loc.Wall.Y >= plan.Size.Y)
        {
            tileh = 0;
        }

        if (loc.Wall.X == entry.X && loc.Wall.Y == entry.Y)
        {
            tileh = -1;
        }

        return (tileh >= 0) ? tileh : result;
    }

    private APoint calcitemloc(IRoom room, WallLocation loc)
    {
        var plan = room.FloorPlan;
        int unitsize = 64 / plan.Scale;
        double heightoffset = calcwallheight(room, loc);

        int screenx = (loc.Wall.X - loc.Wall.Y) * HALF_W;
        int screeny = (loc.Wall.X + loc.Wall.Y) * HALF_H;

        screenx += loc.Offset.X * unitsize;
        screeny += loc.Offset.Y * unitsize;

        if (loc.Orientation == 'r')
        {
            screenx -= HALF_W;
        }

        screeny -= (int)(heightoffset * HALF_W);

        return new APoint(screenx, screeny);
    }

    private APoint toscreen(APoint roompt)
    {
        return new APoint(
            (roompt.X - offset.X) * zoomlevel + Bounds.Width / 2,
            (roompt.Y - offset.Y) * zoomlevel + Bounds.Height / 2
        );
    }

    private APoint toroom(APoint screenpt)
    {
        return new APoint(
            (screenpt.X - Bounds.Width / 2) / zoomlevel + offset.X,
            (screenpt.Y - Bounds.Height / 2) / zoomlevel + offset.Y
        );
    }

    public void setzoom(double level)
    {
        zoomlevel = Math.Clamp(level, ZOOM_MIN, ZOOM_MAX);
        offset = new APoint(0, 0);
        InvalidateVisual();
    }
}
