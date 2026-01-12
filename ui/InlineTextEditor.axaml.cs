using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WallAligner.Core;
using Xabbo.Core;

namespace WallAligner;

public partial class InlineTextEditor : UserControl
{
    public event Action<string, string, string>? previewchanged;
    public event Action<string, string, string>? sendtoserver;
    public event Action? cancelled;

    private RoomCanvas? canvas;
    private bool dragging;
    private Avalonia.Point dragstart;
    private Avalonia.Point dragoffset;
    private string selectedfurni = "";
    private string selectedfont = "Arial";
    private List<string> allfontnames = new();

    public InlineTextEditor()
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
    }

    public void setfurninames(List<string> names)
    {
        var furniinput = this.FindControl<AutoCompleteBox>("furniinput");
        if (furniinput != null)
        {
            furniinput.ItemsSource = names;
        }
    }

    public void setfontnames(List<string> names)
    {
        allfontnames = names;
        populatefontlist(names);
    }

    private void populatefontlist(List<string> fonts)
    {
        var fontlist = this.FindControl<ListBox>("fontlist");
        if (fontlist == null) return;

        fontlist.Items.Clear();
        foreach (var fontname in fonts)
        {
            var tb = new TextBlock
            {
                Text = fontname,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#ddd")),
                FontFamily = new FontFamily(fontname)
            };
            fontlist.Items.Add(tb);
        }

        var arialidx = fonts.FindIndex(f => f.Equals("Arial", StringComparison.OrdinalIgnoreCase));
        if (arialidx >= 0)
            fontlist.SelectedIndex = arialidx;
        else if (fonts.Count > 0)
            fontlist.SelectedIndex = 0;
    }

    private void filterfontlist(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            populatefontlist(allfontnames);
            return;
        }

        var filtered = allfontnames
            .Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        populatefontlist(filtered);
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

    private void triggerpreview()
    {
        var textinput = this.FindControl<TextBox>("textinput");

        if (textinput == null)
            return;

        var txt = textinput.Text ?? "";

        if (string.IsNullOrWhiteSpace(txt) || string.IsNullOrWhiteSpace(selectedfurni))
            return;

        previewchanged?.Invoke(txt, selectedfont, selectedfurni);
    }

    private void init()
    {
        var titlebar = this.FindControl<Border>("titlebar");
        var closebtn = this.FindControl<Button>("closebtn");
        var textinput = this.FindControl<TextBox>("textinput");
        var fontsearch = this.FindControl<TextBox>("fontsearch");
        var fontlist = this.FindControl<ListBox>("fontlist");
        var furniinput = this.FindControl<AutoCompleteBox>("furniinput");
        var sendbtn = this.FindControl<Button>("sendbtn");

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
            closebtn.Click += (s, e) => cancelled?.Invoke();
        }

        if (furniinput != null)
        {
            furniinput.TextChanged += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(furniinput.Text))
                {
                    selectedfurni = furniinput.Text;
                    updateitemcount();
                    triggerpreview();
                }
            };
        }

        if (textinput != null)
        {
            textinput.TextChanged += (s, e) => triggerpreview();
        }

        if (fontsearch != null)
        {
            fontsearch.TextChanged += (s, e) =>
            {
                filterfontlist(fontsearch.Text ?? "");
            };
        }

        if (fontlist != null)
        {
            fontlist.SelectionChanged += (s, e) =>
            {
                if (fontlist.SelectedItem is TextBlock tb)
                {
                    selectedfont = tb.Text ?? "Arial";
                    triggerpreview();
                }
            };
        }
    }

    public void send()
    {
        var textinput = this.FindControl<TextBox>("textinput");
        if (textinput == null) return;

        var txt = textinput.Text ?? "";
        sendtoserver?.Invoke(txt, selectedfont, selectedfurni);
    }
}
