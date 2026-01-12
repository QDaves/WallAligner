using APoint = Avalonia.Point;

namespace WallAligner.Core;

public abstract class DrawingObject
{
    private static int nextid = 1;

    public int Id { get; }
    public APoint Position { get; set; }
    public double Rotation { get; set; }
    public double Scale { get; set; } = 1.0;
    public bool Selected { get; set; }

    protected DrawingObject()
    {
        Id = nextid++;
    }

    public abstract List<APoint> GetRawPoints();

    public List<APoint> GetTransformedPoints()
    {
        var raw = GetRawPoints();
        var result = new List<APoint>();

        double cos = Math.Cos(Rotation * Math.PI / 180);
        double sin = Math.Sin(Rotation * Math.PI / 180);

        foreach (var p in raw)
        {
            double x = p.X * Scale;
            double y = p.Y * Scale;

            double rx = x * cos - y * sin;
            double ry = x * sin + y * cos;

            result.Add(new APoint(rx + Position.X, ry + Position.Y));
        }

        return result;
    }

    public APoint GetCenter()
    {
        var points = GetTransformedPoints();
        if (points.Count == 0) return Position;

        double sumx = 0, sumy = 0;
        foreach (var p in points)
        {
            sumx += p.X;
            sumy += p.Y;
        }
        return new APoint(sumx / points.Count, sumy / points.Count);
    }

    public (double minx, double miny, double maxx, double maxy) GetBounds()
    {
        var points = GetTransformedPoints();
        if (points.Count == 0) return (Position.X, Position.Y, Position.X, Position.Y);

        double minx = double.MaxValue, miny = double.MaxValue;
        double maxx = double.MinValue, maxy = double.MinValue;

        foreach (var p in points)
        {
            if (p.X < minx) minx = p.X;
            if (p.Y < miny) miny = p.Y;
            if (p.X > maxx) maxx = p.X;
            if (p.Y > maxy) maxy = p.Y;
        }

        return (minx, miny, maxx, maxy);
    }

    public bool HitTest(APoint testpoint, double tolerance = 10)
    {
        var points = GetTransformedPoints();

        for (int i = 0; i < points.Count - 1; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];

            double dist = distancetoliinesegment(testpoint, p1, p2);
            if (dist < tolerance)
                return true;
        }

        return false;
    }

    private double distancetoliinesegment(APoint p, APoint a, APoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthsq = dx * dx + dy * dy;

        if (lengthsq < 0.001)
            return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

        double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthsq));
        double projx = a.X + t * dx;
        double projy = a.Y + t * dy;

        return Math.Sqrt((p.X - projx) * (p.X - projx) + (p.Y - projy) * (p.Y - projy));
    }
}

public class ShapeDrawingObject : DrawingObject
{
    public int ShapeType { get; set; }
    public double BaseSize { get; set; }

    public ShapeDrawingObject(int shapetype, double size, APoint position)
    {
        ShapeType = shapetype;
        BaseSize = size;
        Position = position;
    }

    public override List<APoint> GetRawPoints()
    {
        return GenerateShapePoints(ShapeType, BaseSize);
    }

    public static List<APoint> GenerateShapePoints(int shapetype, double size)
    {
        var points = new List<APoint>();
        int segments = 60;

        switch (shapetype)
        {
            case 1:
                double halfsize = size / 2;
                for (int i = 0; i <= segments / 4; i++)
                    points.Add(new APoint(-halfsize + (i * size / (segments / 4)), -halfsize));
                for (int i = 0; i <= segments / 4; i++)
                    points.Add(new APoint(halfsize, -halfsize + (i * size / (segments / 4))));
                for (int i = 0; i <= segments / 4; i++)
                    points.Add(new APoint(halfsize - (i * size / (segments / 4)), halfsize));
                for (int i = 0; i <= segments / 4; i++)
                    points.Add(new APoint(-halfsize, halfsize - (i * size / (segments / 4))));
                break;

            case 2:
                for (int i = 0; i <= segments; i++)
                {
                    double angle = 2 * Math.PI * i / segments;
                    points.Add(new APoint(
                        (size / 2) * Math.Cos(angle),
                        (size / 2) * Math.Sin(angle)
                    ));
                }
                break;

            case 3:
                int starpoints = 5;
                double outerradius = size / 2;
                double innerradius = size / 4;
                for (int i = 0; i <= starpoints * 2; i++)
                {
                    double angle = Math.PI / 2 + (Math.PI * i / starpoints);
                    double radius = (i % 2 == 0) ? outerradius : innerradius;
                    points.Add(new APoint(
                        radius * Math.Cos(angle),
                        -radius * Math.Sin(angle)
                    ));
                }
                points.Add(points[0]);
                break;

            case 4:
                double triradius = size / 2;
                for (int i = 0; i <= 3; i++)
                {
                    double angle = Math.PI / 2 + (2 * Math.PI * i / 3);
                    points.Add(new APoint(
                        triradius * Math.Cos(angle),
                        -triradius * Math.Sin(angle)
                    ));
                }
                break;

            case 5:
                for (int i = 0; i <= segments; i++)
                {
                    double t = 2 * Math.PI * i / segments;
                    double scale = size / 32;
                    double x = 16 * Math.Pow(Math.Sin(t), 3);
                    double y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
                    points.Add(new APoint(x * scale, -y * scale));
                }
                break;
        }

        return points;
    }
}

public class FreehandDrawingObject : DrawingObject
{
    public List<APoint> RawPoints { get; set; } = new();

    public FreehandDrawingObject(List<APoint> points, APoint position)
    {
        Position = position;

        if (points.Count > 0)
        {
            double cx = 0, cy = 0;
            foreach (var p in points)
            {
                cx += p.X;
                cy += p.Y;
            }
            cx /= points.Count;
            cy /= points.Count;

            foreach (var p in points)
            {
                RawPoints.Add(new APoint(p.X - cx, p.Y - cy));
            }
        }
    }

    public override List<APoint> GetRawPoints()
    {
        return RawPoints;
    }
}
