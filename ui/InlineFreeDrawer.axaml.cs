using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using WallAligner.Core;
using Xabbo.Core;

namespace WallAligner;

public partial class InlineFreeDrawer : UserControl
{
    public event Action? sendtoserver;
    public event Action? cancelled;

    private RoomCanvas? canvas;
    private bool dragging;
    private Avalonia.Point dragstart;
    private Avalonia.Point dragoffset;
    private string selectedfurni = "";
    private DispatcherTimer? statustimer;

    public InlineFreeDrawer()
    {
        InitializeComponent();
        init();

        this.PointerPressed += (s, e) =>
        {
            if (!dragging)
                e.Handled = true;
        };
        this.PointerMoved += (s, e) =>
        {
            if (!dragging)
                e.Handled = true;
        };
        this.PointerReleased += (s, e) =>
        {
            if (!dragging)
                e.Handled = true;
        };
    }

    public void setup(RoomCanvas roomcanvas)
    {
        canvas = roomcanvas;

        statustimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        statustimer.Tick += (s, e) =>
        {
            if (canvas != null)
            {
                var status = canvas.getdrawingstatus();
                updatestatus(status.objectcount, status.pointcount);
            }
        };
        statustimer.Start();
    }

    public void setfurninames(List<string> names)
    {
        var furniinput = this.FindControl<AutoCompleteBox>("furniinput");
        if (furniinput != null)
        {
            furniinput.ItemsSource = names;
        }
    }

    public void updatestatus(int objectcount, int pointcount)
    {
        var pointslabel = this.FindControl<TextBlock>("pointslabel");
        if (pointslabel != null)
        {
            pointslabel.Text = $"Objects: {objectcount} | Points: {pointcount}";
        }
    }

    private void updateitemcount()
    {
        var itemcountlabel = this.FindControl<TextBlock>("itemcountlabel");
        if (itemcountlabel != null && canvas != null && !string.IsNullOrWhiteSpace(selectedfurni))
        {
            var room = canvas.currentroom;
            if (room != null)
            {
                var count = room.WallItems.Count(x => x.GetName()?.Equals(selectedfurni, StringComparison.OrdinalIgnoreCase) == true);
                itemcountlabel.Text = $"Items: {count}";
            }
        }
    }

    private void startdrawing()
    {
        if (canvas == null || string.IsNullOrWhiteSpace(selectedfurni))
            return;

        canvas.startdrawing(selectedfurni);
        canvas.setdrawingshape(0);
    }

    private void addshape(int shapetype)
    {
        if (canvas == null || string.IsNullOrWhiteSpace(selectedfurni))
            return;

        canvas.addshapeatcenter(shapetype, 120);
    }

    private void init()
    {
        var titlebar = this.FindControl<Border>("titlebar");
        var closebtn = this.FindControl<Button>("closebtn");
        var furniinput = this.FindControl<AutoCompleteBox>("furniinput");

        var addrectbtn = this.FindControl<Button>("addrectbtn");
        var addcirclebtn = this.FindControl<Button>("addcirclebtn");
        var addstarbtn = this.FindControl<Button>("addstarbtn");
        var addtribtn = this.FindControl<Button>("addtribtn");
        var addheartbtn = this.FindControl<Button>("addheartbtn");

        if (titlebar != null)
        {
            titlebar.PointerPressed += (s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    dragging = true;
                    dragstart = e.GetPosition(this.Parent as Visual);
                    dragoffset = new Avalonia.Point(Bounds.Left, Bounds.Top);
                    e.Handled = true;
                }
            };
        }

        this.PointerMoved += (s, e) =>
        {
            if (dragging && this.Parent != null)
            {
                var pos = e.GetPosition(this.Parent as Visual);
                var delta = pos - dragstart;
                var newx = dragoffset.X + delta.X;
                var newy = dragoffset.Y + delta.Y;
                Canvas.SetLeft(this, newx);
                Canvas.SetTop(this, newy);
            }
        };

        this.PointerReleased += (s, e) =>
        {
            dragging = false;
        };

        if (closebtn != null)
        {
            closebtn.Click += (s, e) =>
            {
                statustimer?.Stop();
                cancelled?.Invoke();
            };
        }

        if (furniinput != null)
        {
            furniinput.TextChanged += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(furniinput.Text))
                {
                    selectedfurni = furniinput.Text;
                    updateitemcount();
                    startdrawing();
                }
            };
        }

        if (addrectbtn != null)
            addrectbtn.Click += (s, e) => addshape(1);
        if (addcirclebtn != null)
            addcirclebtn.Click += (s, e) => addshape(2);
        if (addstarbtn != null)
            addstarbtn.Click += (s, e) => addshape(3);
        if (addtribtn != null)
            addtribtn.Click += (s, e) => addshape(4);
        if (addheartbtn != null)
            addheartbtn.Click += (s, e) => addshape(5);
    }

    public void send()
    {
        statustimer?.Stop();
        sendtoserver?.Invoke();
    }

    public void clear()
    {
        if (canvas != null)
        {
            canvas.cleardrawingobjects();
        }
    }
}
