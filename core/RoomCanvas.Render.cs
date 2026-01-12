using Avalonia.Media;
using APoint = Avalonia.Point;
using ARect = Avalonia.Rect;
using AMatrix = Avalonia.Matrix;

namespace WallAligner.Core;

public partial class RoomCanvas
{
    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);

        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new ARect(0, 0, Bounds.Width, Bounds.Height));

        var room = currentroom;
        if (room == null)
            return;

        var transform = AMatrix.CreateTranslation(-offset.X, -offset.Y) *
                        AMatrix.CreateScale(zoomlevel, zoomlevel) *
                        AMatrix.CreateTranslation(Bounds.Width / 2, Bounds.Height / 2);

        using (ctx.PushTransform(transform))
        {
            foreach (var poly in floorpolygons)
            {
                var geometry = new PolylineGeometry(poly, true);
                ctx.DrawGeometry(null, floorpen, geometry);
            }

            foreach (var item in room.WallItems)
            {
                var pos = calcitemloc(room, item.Location);
                bool isselected = selected.Contains(item.Id);
                bool isdragging = dragids.Contains(item.Id);

                var pen = isselected ? selectedpen : itempen;
                var brush = isselected ? selectedbrush : itembrush;

                if (isdragging)
                {
                    pen = new Pen(new SolidColorBrush(Color.FromArgb(255, 255, 165, 0)), 3);
                    brush = new SolidColorBrush(Color.FromArgb(200, 255, 165, 0));
                }

                var geometry = new EllipseGeometry(new ARect(pos.X - 4, pos.Y - 4, 8, 8));
                ctx.DrawGeometry(brush, pen, geometry);
            }
        }

        if (selecting)
        {
            var selbrush = new SolidColorBrush(Color.FromArgb(40, 100, 150, 255));
            var selpen = new Pen(new SolidColorBrush(Color.FromArgb(180, 100, 150, 255)), 1);

            double x = Math.Min(selectstart.X, selectend.X);
            double y = Math.Min(selectstart.Y, selectend.Y);
            double w = Math.Abs(selectend.X - selectstart.X);
            double h = Math.Abs(selectend.Y - selectstart.Y);

            ctx.DrawRectangle(selbrush, selpen, new ARect(x, y, w, h));
        }

        if (isDrawingMode)
        {
            var objpen = new Pen(new SolidColorBrush(Color.FromArgb(200, 50, 120, 255)), 2.5);
            var selectedobjpen = new Pen(new SolidColorBrush(Color.FromArgb(255, 255, 180, 50)), 3);
            var handlebrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            var handlepen = new Pen(new SolidColorBrush(Color.FromArgb(255, 50, 120, 255)), 2);
            var rotatehandlebrush = new SolidColorBrush(Color.FromArgb(255, 50, 200, 100));
            var scalehandlebrush = new SolidColorBrush(Color.FromArgb(255, 200, 100, 50));

            foreach (var obj in drawingobjects)
            {
                var points = obj.GetTransformedPoints();
                var pen = obj.Selected ? selectedobjpen : objpen;

                for (int i = 0; i < points.Count - 1; i++)
                {
                    ctx.DrawLine(pen, points[i], points[i + 1]);
                }

                if (obj.Selected)
                {
                    var bounds = obj.GetBounds();
                    var center = obj.GetCenter();

                    ctx.DrawEllipse(handlebrush, handlepen, center, 8, 8);

                    double rotatehandley = bounds.miny - 30;
                    ctx.DrawLine(handlepen, center, new APoint(center.X, rotatehandley));
                    ctx.DrawEllipse(rotatehandlebrush, handlepen, new APoint(center.X, rotatehandley), 7, 7);

                    ctx.DrawEllipse(scalehandlebrush, handlepen, new APoint(bounds.maxx + 10, center.Y), 6, 6);
                }
            }

            if (currentsegment.Count > 1)
            {
                for (int i = 0; i < currentsegment.Count - 1; i++)
                {
                    ctx.DrawLine(objpen, currentsegment[i], currentsegment[i + 1]);
                }
            }
        }

        if (previewbox.HasValue)
        {
            var box = previewbox.Value;
            var center = new APoint(box.Center.X, box.Center.Y);
            double scalewidth = box.Width;

            var glowpen = new Pen(new SolidColorBrush(Color.FromArgb(40, 100, 200, 255)), 20);
            ctx.DrawEllipse(null, glowpen, center, 12, 12);

            var linepen2 = new Pen(new SolidColorBrush(Color.FromArgb(80, 100, 180, 255)), 1);
            ctx.DrawLine(linepen2, new APoint(center.X - 40, center.Y), new APoint(center.X - 15, center.Y));
            ctx.DrawLine(linepen2, new APoint(center.X + 15, center.Y), new APoint(center.X + 40, center.Y));
            ctx.DrawLine(linepen2, new APoint(center.X, center.Y - 40), new APoint(center.X, center.Y - 15));
            ctx.DrawLine(linepen2, new APoint(center.X, center.Y + 15), new APoint(center.X, center.Y + 40));

            double pointsize = 12;
            var pointbrush = new SolidColorBrush(Color.FromArgb(255, 80, 160, 255));
            var pointpen = new Pen(new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), 2);
            ctx.DrawEllipse(pointbrush, pointpen, center, pointsize / 2, pointsize / 2);

            var scalepen = new Pen(new SolidColorBrush(Color.FromArgb(180, 80, 160, 255)), 2);
            var scalepenlight = new Pen(new SolidColorBrush(Color.FromArgb(80, 80, 160, 255)), 1);

            ctx.DrawLine(scalepenlight, new APoint(center.X - scalewidth / 2, center.Y + 35), new APoint(center.X + scalewidth / 2, center.Y + 35));
            ctx.DrawLine(scalepen, new APoint(center.X - scalewidth / 2, center.Y + 30), new APoint(center.X - scalewidth / 2, center.Y + 40));
            ctx.DrawLine(scalepen, new APoint(center.X + scalewidth / 2, center.Y + 30), new APoint(center.X + scalewidth / 2, center.Y + 40));

            var handlebrush = new SolidColorBrush(Color.FromArgb(255, 80, 160, 255));
            var handlepen = new Pen(new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), 1.5);
            ctx.DrawEllipse(handlebrush, handlepen, new APoint(center.X - scalewidth / 2, center.Y + 35), 5, 5);
            ctx.DrawEllipse(handlebrush, handlepen, new APoint(center.X + scalewidth / 2, center.Y + 35), 5, 5);
        }
    }
}
