using Avalonia.Controls;
using Avalonia.Media;
using WallAligner.Core;
using System.Drawing.Text;
using Xabbo.Core;
using Path = Avalonia.Controls.Shapes.Path;

namespace WallAligner;

public partial class MainWindow : Window
{
    private readonly Extension? ext;
    private bool pinned = false;
    private InlineTextEditor? inlineeditor;
    private InlineSvgLoader? inlinesvgloader;
    private InlineFreeDrawer? inlinedrawer;
    private int zoomstate = 1;
    private string activetool = "";

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(Extension extension) : this()
    {
        ext = extension;
        init();
    }

    private void updateactionbar()
    {
        var actionseparator = this.FindControl<Border>("actionseparator");
        var clearallbtn = this.FindControl<Button>("clearallbtn");
        var sendserverbtn = this.FindControl<Button>("sendserverbtn");

        bool hasactivetool = !string.IsNullOrEmpty(activetool);

        if (actionseparator != null)
            actionseparator.IsVisible = hasactivetool;

        if (clearallbtn != null)
            clearallbtn.IsVisible = activetool == "freedraw";

        if (sendserverbtn != null)
            sendserverbtn.IsVisible = hasactivetool;
    }

    private void closeallinlines()
    {
        var overlaycanvas = this.FindControl<Canvas>("overlaycanvas");
        var roomcanvas = this.FindControl<RoomCanvas>("roomcanvas");

        if (overlaycanvas != null)
        {
            if (inlineeditor != null)
            {
                overlaycanvas.Children.Remove(inlineeditor);
                inlineeditor = null;
            }
            if (inlinesvgloader != null)
            {
                overlaycanvas.Children.Remove(inlinesvgloader);
                inlinesvgloader = null;
            }
            if (inlinedrawer != null)
            {
                overlaycanvas.Children.Remove(inlinedrawer);
                roomcanvas?.stopdrawing();
                inlinedrawer = null;
            }
        }

        roomcanvas?.clearpreview();
        activetool = "";
        updateactionbar();
    }

    private void init()
    {
        var pinbtn = this.FindControl<Button>("pinbtn");
        var pinicon = this.FindControl<Path>("pinicon");
        var roomcanvas = this.FindControl<RoomCanvas>("roomcanvas");
        var autoadjustcheck = this.FindControl<CheckBox>("autoadjustcheck");
        var textwriterbtn = this.FindControl<Button>("textwriterbtn");
        var svgloaderbtn = this.FindControl<Button>("svgloaderbtn");
        var drawerbtn = this.FindControl<Button>("drawerbtn");
        var zoomoutbtn = this.FindControl<Button>("zoomoutbtn");
        var zoomnormalbtn = this.FindControl<Button>("zoomnormalbtn");
        var zoominbtn = this.FindControl<Button>("zoominbtn");
        var zoomlabel = this.FindControl<TextBlock>("zoomlabel");
        var clearallbtn = this.FindControl<Button>("clearallbtn");
        var sendserverbtn = this.FindControl<Button>("sendserverbtn");

        if (pinbtn != null && pinicon != null)
        {
            pinbtn.Click += (s, e) =>
            {
                pinned = !pinned;
                Topmost = pinned;
                pinicon.Fill = pinned ? Brushes.White : Brushes.Gray;
            };
        }

        if (roomcanvas != null && ext != null)
        {
            roomcanvas.extension = ext;
            roomcanvas.setuproom();
        }

        if (autoadjustcheck != null && roomcanvas != null)
        {
            autoadjustcheck.IsCheckedChanged += (s, e) =>
            {
                roomcanvas.autoadjust = autoadjustcheck.IsChecked ?? true;
            };
            roomcanvas.autoadjust = autoadjustcheck.IsChecked ?? true;
        }

        var placeontopcheck = this.FindControl<CheckBox>("placeontopcheck");
        if (placeontopcheck != null && roomcanvas != null)
        {
            placeontopcheck.IsCheckedChanged += (s, e) =>
            {
                roomcanvas.placeontop = placeontopcheck.IsChecked ?? false;
            };
            roomcanvas.placeontop = placeontopcheck.IsChecked ?? false;
        }

        if (zoomoutbtn != null && roomcanvas != null && zoomlabel != null)
        {
            zoomoutbtn.Click += (s, e) =>
            {
                if (zoomstate > 0)
                {
                    zoomstate--;
                    applyzoom(roomcanvas, zoomlabel);
                }
            };
        }

        if (zoomnormalbtn != null && roomcanvas != null && zoomlabel != null)
        {
            zoomnormalbtn.Click += (s, e) =>
            {
                zoomstate = 1;
                applyzoom(roomcanvas, zoomlabel);
            };
        }

        if (zoominbtn != null && roomcanvas != null && zoomlabel != null)
        {
            zoominbtn.Click += (s, e) =>
            {
                if (zoomstate < 2)
                {
                    zoomstate++;
                    applyzoom(roomcanvas, zoomlabel);
                }
            };
        }

        if (textwriterbtn != null && roomcanvas != null)
        {
            textwriterbtn.Click += (s, e) =>
            {
                if (roomcanvas.currentroom == null) return;
                showinlineeditor(roomcanvas);
            };
        }

        if (svgloaderbtn != null && roomcanvas != null)
        {
            svgloaderbtn.Click += (s, e) =>
            {
                if (roomcanvas.currentroom == null) return;
                showinlinesvgloader(roomcanvas);
            };
        }

        if (drawerbtn != null && roomcanvas != null)
        {
            drawerbtn.Click += (s, e) =>
            {
                if (roomcanvas.currentroom == null) return;
                showinlinedrawer(roomcanvas);
            };
        }

        if (clearallbtn != null)
        {
            clearallbtn.Click += (s, e) =>
            {
                if (inlinedrawer != null)
                    inlinedrawer.clear();
            };
        }

        if (sendserverbtn != null && roomcanvas != null)
        {
            sendserverbtn.Click += (s, e) =>
            {
                var overlaycanvas = this.FindControl<Canvas>("overlaycanvas");

                if (inlineeditor != null)
                {
                    inlineeditor.send();
                    if (overlaycanvas != null)
                        overlaycanvas.Children.Remove(inlineeditor);
                    inlineeditor = null;
                    activetool = "";
                    updateactionbar();
                }
                else if (inlinesvgloader != null)
                {
                    inlinesvgloader.send();
                    if (overlaycanvas != null)
                        overlaycanvas.Children.Remove(inlinesvgloader);
                    inlinesvgloader = null;
                    activetool = "";
                    updateactionbar();
                }
                else if (inlinedrawer != null)
                {
                    inlinedrawer.send();
                    if (overlaycanvas != null)
                        overlaycanvas.Children.Remove(inlinedrawer);
                    roomcanvas.stopdrawing();
                    inlinedrawer = null;
                    activetool = "";
                    updateactionbar();
                }
            };
        }
    }

    private void applyzoom(RoomCanvas roomcanvas, TextBlock zoomlabel)
    {
        double[] zoomlevels = { 0.5, 1.0, 2.0 };
        string[] zoomlabels = { "0.5x", "1x", "2x" };
        roomcanvas.setzoom(zoomlevels[zoomstate]);
        zoomlabel.Text = zoomlabels[zoomstate];
    }

    private void showinlineeditor(RoomCanvas roomcanvas)
    {
        var overlaycanvas = this.FindControl<Canvas>("overlaycanvas");
        if (overlaycanvas == null || roomcanvas.currentroom == null) return;

        closeallinlines();

        inlineeditor = new InlineTextEditor();
        inlineeditor.setup(roomcanvas);

        var allnames = new List<string>();
        foreach (var item in roomcanvas.currentroom.WallItems)
        {
            var name = item.GetName();
            if (!string.IsNullOrWhiteSpace(name))
                allnames.Add(name);
        }

        var furninames = allnames.Distinct().OrderBy(x => x).ToList();
        inlineeditor.setfurninames(furninames);

        var fontnames = new List<string>();
        using (var fonts = new InstalledFontCollection())
        {
            foreach (var family in fonts.Families)
            {
                fontnames.Add(family.Name);
            }
        }
        fontnames = fontnames.OrderBy(x => x).ToList();
        inlineeditor.setfontnames(fontnames);

        inlineeditor.previewchanged += (txt, font, furniname) =>
        {
            roomcanvas.previewtext(txt, font, furniname);
        };

        inlineeditor.sendtoserver += (txt, font, furniname) =>
        {
            roomcanvas.applytext(txt, font, furniname);
            if (overlaycanvas != null && inlineeditor != null)
            {
                overlaycanvas.Children.Remove(inlineeditor);
            }
            inlineeditor = null;
            activetool = "";
            updateactionbar();
        };

        inlineeditor.cancelled += () =>
        {
            roomcanvas.clearpreview();
            if (overlaycanvas != null && inlineeditor != null)
            {
                overlaycanvas.Children.Remove(inlineeditor);
            }
            inlineeditor = null;
            activetool = "";
            updateactionbar();
        };

        roomcanvas.showpreviewbox();

        Canvas.SetLeft(inlineeditor, 20);
        Canvas.SetTop(inlineeditor, 20);
        inlineeditor.IsHitTestVisible = true;

        overlaycanvas.Children.Add(inlineeditor);

        activetool = "texteditor";
        updateactionbar();
    }

    private void showinlinesvgloader(RoomCanvas roomcanvas)
    {
        var overlaycanvas = this.FindControl<Canvas>("overlaycanvas");
        if (overlaycanvas == null || roomcanvas.currentroom == null) return;

        closeallinlines();

        inlinesvgloader = new InlineSvgLoader();
        inlinesvgloader.setup(roomcanvas);

        var allnames = new List<string>();
        foreach (var item in roomcanvas.currentroom.WallItems)
        {
            var name = item.GetName();
            if (!string.IsNullOrWhiteSpace(name))
                allnames.Add(name);
        }

        var furninames = allnames.Distinct().OrderBy(x => x).ToList();
        inlinesvgloader.setfurninames(furninames);

        inlinesvgloader.previewchanged += (filepath, pathdata, furniname) =>
        {
            roomcanvas.previewsvg(filepath, pathdata, furniname);
        };

        inlinesvgloader.sendtoserver += (filepath, pathdata, furniname) =>
        {
            roomcanvas.applysvg(filepath, pathdata, furniname);
            if (overlaycanvas != null && inlinesvgloader != null)
            {
                overlaycanvas.Children.Remove(inlinesvgloader);
            }
            inlinesvgloader = null;
            activetool = "";
            updateactionbar();
        };

        inlinesvgloader.cancelled += () =>
        {
            roomcanvas.clearpreview();
            if (overlaycanvas != null && inlinesvgloader != null)
            {
                overlaycanvas.Children.Remove(inlinesvgloader);
            }
            inlinesvgloader = null;
            activetool = "";
            updateactionbar();
        };

        roomcanvas.showpreviewbox();

        Canvas.SetLeft(inlinesvgloader, 20);
        Canvas.SetTop(inlinesvgloader, 20);
        inlinesvgloader.IsHitTestVisible = true;

        overlaycanvas.Children.Add(inlinesvgloader);

        activetool = "svgloader";
        updateactionbar();
    }

    private void showinlinedrawer(RoomCanvas roomcanvas)
    {
        var overlaycanvas = this.FindControl<Canvas>("overlaycanvas");
        if (overlaycanvas == null || roomcanvas.currentroom == null) return;

        closeallinlines();

        inlinedrawer = new InlineFreeDrawer();
        inlinedrawer.setup(roomcanvas);

        var allnames = new List<string>();
        foreach (var item in roomcanvas.currentroom.WallItems)
        {
            var name = item.GetName();
            if (!string.IsNullOrWhiteSpace(name))
                allnames.Add(name);
        }

        var furninames = allnames.Distinct().OrderBy(x => x).ToList();
        inlinedrawer.setfurninames(furninames);

        inlinedrawer.sendtoserver += () =>
        {
            roomcanvas.applydrawing();
            if (overlaycanvas != null && inlinedrawer != null)
            {
                overlaycanvas.Children.Remove(inlinedrawer);
            }
            roomcanvas.stopdrawing();
            inlinedrawer = null;
            activetool = "";
            updateactionbar();
        };

        inlinedrawer.cancelled += () =>
        {
            roomcanvas.stopdrawing();
            roomcanvas.clearpreview();
            if (overlaycanvas != null && inlinedrawer != null)
            {
                overlaycanvas.Children.Remove(inlinedrawer);
            }
            inlinedrawer = null;
            activetool = "";
            updateactionbar();
        };

        Canvas.SetLeft(inlinedrawer, 20);
        Canvas.SetTop(inlinedrawer, 20);
        inlinedrawer.IsHitTestVisible = true;

        overlaycanvas.Children.Add(inlinedrawer);

        activetool = "freedraw";
        updateactionbar();
    }
}
