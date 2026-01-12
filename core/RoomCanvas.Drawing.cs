using Xabbo.Core;
using APoint = Avalonia.Point;

namespace WallAligner.Core;

public partial class RoomCanvas
{
    public void startdrawing(string furniname)
    {
        var room = currentroom;
        if (room == null || extension == null) return;

        currenttext = null;
        previewfont = null;
        currentfilepath = null;
        isSvgMode = false;
        isDrawingMode = true;
        drawingfurni = furniname;
        drawingobjects.Clear();
        currentsegment.Clear();
        isdrawing = false;

        cachedlocations = null;
        cachedscale = -1;
        cachedstartx = int.MinValue;
        cachedstarty = int.MinValue;
        cachedgeometry = null;
        cachedgeometrykey = null;

        InvalidateVisual();
    }

    public void setdrawingshape(int shapetype)
    {
        drawingshapetype = shapetype;
    }

    public void addshapeatcenter(int shapetype, double size)
    {
        if (!isDrawingMode) return;

        double centerx = Bounds.Width / 2;
        double centery = Bounds.Height / 2;

        var newshape = new ShapeDrawingObject(shapetype, size, new APoint(centerx, centery));
        drawingobjects.Add(newshape);

        foreach (var obj in drawingobjects) obj.Selected = false;
        newshape.Selected = true;
        selectedobject = newshape;

        int totalpoints = drawingobjects.Sum(o => o.GetRawPoints().Count);
        statuschanged?.Invoke($"shape added - {drawingobjects.Count} objects, {totalpoints} points");
        InvalidateVisual();
    }

    public (int objectcount, int pointcount) getdrawingstatus()
    {
        int objects = drawingobjects.Count;
        int points = drawingobjects.Sum(o => o.GetRawPoints().Count);
        return (objects, points);
    }

    public void cleardrawingobjects()
    {
        drawingobjects.Clear();
        selectedobject = null;
        currentsegment.Clear();
        InvalidateVisual();
    }

    public void stopdrawing()
    {
        isDrawingMode = false;
        isdrawing = false;
        drawingobjects.Clear();
        currentsegment.Clear();
        drawingfurni = null;
        selectedobject = null;
        draggingobject = false;
        rotatingobject = false;
        scalingobject = false;
        InvalidateVisual();
    }

    public void applydrawing()
    {
        var room = currentroom;
        if (room == null || extension == null) return;

        if (string.IsNullOrWhiteSpace(drawingfurni))
        {
            statuschanged?.Invoke("no furniture specified");
            return;
        }

        if (drawingobjects.Count == 0)
        {
            statuschanged?.Invoke("add shapes or draw first");
            return;
        }

        var matchingitems = room.WallItems
            .Where(x => x.GetName()?.Equals(drawingfurni, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(x => x.Id)
            .ToArray();

        if (matchingitems.Length == 0)
        {
            statuschanged?.Invoke($"no items found with name: {drawingfurni}");
            return;
        }

        var objectdata = new List<(List<APoint> points, List<double> distances, double length)>();
        double totallength = 0;

        foreach (var obj in drawingobjects)
        {
            var pts = obj.GetTransformedPoints();
            if (pts.Count < 2) continue;

            var dists = new List<double> { 0 };
            double objlen = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double seglen = Math.Sqrt(Math.Pow(pts[i].X - pts[i - 1].X, 2) + Math.Pow(pts[i].Y - pts[i - 1].Y, 2));
                objlen += seglen;
                dists.Add(objlen);
            }
            objectdata.Add((pts, dists, objlen));
            totallength += objlen;
        }

        if (objectdata.Count == 0 || totallength < 0.001)
        {
            statuschanged?.Invoke("add shapes or draw first");
            return;
        }

        var evenpoints = new List<APoint>();
        int itemidx = 0;

        for (int objidx = 0; objidx < objectdata.Count; objidx++)
        {
            var (pts, dists, objlen) = objectdata[objidx];
            double objshare = objlen / totallength;
            int objitemcount = (int)Math.Round(objshare * matchingitems.Length);

            if (objidx == objectdata.Count - 1)
                objitemcount = matchingitems.Length - itemidx;

            if (objitemcount <= 0) continue;

            for (int i = 0; i < objitemcount; i++)
            {
                double targetdist = objitemcount > 1
                    ? (double)i / (objitemcount - 1) * objlen
                    : objlen / 2;

                int segidx = 0;
                for (int j = 1; j < dists.Count; j++)
                {
                    if (dists[j] >= targetdist)
                    {
                        segidx = j - 1;
                        break;
                    }
                    segidx = j - 1;
                }

                var p1 = pts[segidx];
                var p2 = pts[Math.Min(segidx + 1, pts.Count - 1)];
                double segstart = dists[segidx];
                double segend = dists[Math.Min(segidx + 1, dists.Count - 1)];
                double seglen = segend - segstart;

                double t = seglen > 0.001 ? (targetdist - segstart) / seglen : 0;
                t = Math.Max(0, Math.Min(1, t));

                double px = p1.X + t * (p2.X - p1.X);
                double py = p1.Y + t * (p2.Y - p1.Y);
                evenpoints.Add(new APoint(px, py));
                itemidx++;
            }
        }

        var plan = room.FloorPlan;
        int roomscale = plan.Scale;
        int unitsize = 64 / roomscale;

        var locations = new List<WallLocation>();

        for (int i = 0; i < Math.Min(matchingitems.Length, evenpoints.Count); i++)
        {
            var screenpoint = evenpoints[i];
            var roompoint = toroom(screenpoint);

            int pixelx = (int)Math.Round(roompoint.X);
            int pixely = (int)Math.Round(roompoint.Y);

            int adjustedx = pixelx + HALF_W;
            int wx = (int)Math.Floor((double)adjustedx / HALF_W);
            int lx = (int)Math.Round((adjustedx - wx * HALF_W) / (double)unitsize);

            int lxstep = HALF_W / unitsize;
            while (lx < 0) { wx--; lx += lxstep; }
            while (lx >= lxstep) { wx++; lx -= lxstep; }

            var temploc = new WallLocation(new Point(wx, 0), new Point(lx, 0), 'r');
            double heightoffset = calcwallheight(room, temploc);

            int basescreeny = wx * HALF_H;
            int heightpixels = (int)(heightoffset * HALF_W);
            double targetly = (pixely - basescreeny + heightoffset * HALF_W) / (double)unitsize;
            int ly = (int)Math.Round(targetly);

            int finalwx = wx;
            int finalwy = 0;
            int finaly = ly;

            if (placeontop)
            {
                int topwy = plan.Size.Y;
                finalwx = wx + topwy;
                finalwy = topwy;
                finaly = ly - (heightpixels + HALF_W * topwy) / unitsize;
            }

            var loc = new WallLocation(
                new Point(finalwx, finalwy),
                new Point(lx, finaly),
                'r'
            );

            locations.Add(loc);
        }

        statuschanged?.Invoke($"applying items...");

        selected.Clear();
        foreach (var item in matchingitems)
            selected.Add(item.Id);

        drawingobjects.Clear();
        currentsegment.Clear();
        selectedobject = null;
        applypathlocations(matchingitems, locations);
        InvalidateVisual();

        statuschanged?.Invoke($"applied to {locations.Count} items");
    }
}
