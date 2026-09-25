using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace BlueRandom.Tests;

public class ArcGeometryTests
{
    [Fact]
    public void TestRenderArc()
    {
        double cx = 120, cy = 120;
        double rMid = 62.0;
        double thickness = 34.0;
        double startDeg = -165.0;
        double endDeg = -15.0;

        double hT = thickness / 2.0;
        double rIn = rMid - hT;
        double rOut = rMid + hT;

        double radStart = startDeg * Math.PI / 180.0;
        double radEnd = endDeg * Math.PI / 180.0;

        Point pInStart = new Point(cx + rIn * Math.Cos(radStart), cy + rIn * Math.Sin(radStart));
        Point pInEnd = new Point(cx + rIn * Math.Cos(radEnd), cy + rIn * Math.Sin(radEnd));
        Point pOutEnd = new Point(cx + rOut * Math.Cos(radEnd), cy + rOut * Math.Sin(radEnd));
        Point pOutStart = new Point(cx + rOut * Math.Cos(radStart), cy + rOut * Math.Sin(radStart));

        var figure = new PathFigure
        {
            StartPoint = pInStart,
            IsClosed = true,
            IsFilled = true
        };

        // 1. Inner arc: Clockwise from start (-165°) to end (-15°)
        figure.Segments.Add(new ArcSegment(pInEnd, new Size(rIn, rIn), 0, false, SweepDirection.Clockwise, true));
        // 2. Right cap: from pInEnd to pOutEnd -> Counterclockwise bulges outward
        figure.Segments.Add(new ArcSegment(pOutEnd, new Size(hT, hT), 0, false, SweepDirection.Counterclockwise, true));
        // 3. Outer arc: Counterclockwise from end (-15°) back to start (-165°)
        figure.Segments.Add(new ArcSegment(pOutStart, new Size(rOut, rOut), 0, false, SweepDirection.Counterclockwise, true));
        // 4. Left cap: from pOutStart to pInStart -> Counterclockwise bulges outward
        figure.Segments.Add(new ArcSegment(pInStart, new Size(hT, hT), 0, false, SweepDirection.Counterclockwise, true));

        var geom = new PathGeometry();
        geom.Figures.Add(figure);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 240, 240));

            // Endpoints:
            double endLeftX = cx + rMid * Math.Cos(radStart);
            double endRightX = cx + rMid * Math.Cos(radEnd);
            double actionPosY = 150;

            // Cancel button (X) moved under left endpoint: at (endLeftX, 150), radius 20
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x94, 0x94)), new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0xB9)), 2), new Point(endLeftX, actionPosY), 20, 20);

            // Confirm button (✓) moved under right endpoint: at (endRightX, 150), radius 20
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0xFF)), new Pen(new SolidColorBrush(Color.FromRgb(0xA6, 0xE9, 0xFF)), 2), new Point(endRightX, actionPosY), 20, 20);

            // Center button at (120, 120), radius 25
            dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0xFF)), 2), new Point(120, 120), 25, 25);

            // Arc capsule
            dc.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0xFF)), 2), geom);

            // Dividers along 150° arc:
            // Dividers at -130.0° and -50.0°
            double[] divAngles = { -130.0, -50.0 };
            foreach (var a in divAngles)
            {
                double rad = a * Math.PI / 180.0;
                Point p1 = new Point(cx + (rMid - 8) * Math.Cos(rad), cy + (rMid - 8) * Math.Sin(rad));
                Point p2 = new Point(cx + (rMid + 8) * Math.Cos(rad), cy + (rMid + 8) * Math.Sin(rad));
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x4D, 0x66, 0xAA, 0xDC)), 1.5), p1, p2);
            }

            // Buttons along 150° arc:
            double[] btnAngles = { -145.0, -115.0, -90.0, -65.0, -35.0 };
            string[] texts = { "MIN", "−", "10", "+", "MAX" };

            var typeFace = new Typeface("Segoe UI Variable, Microsoft YaHei UI");
            for (int i = 0; i < 5; i++)
            {
                double a = btnAngles[i];
                double rad = a * Math.PI / 180.0;
                double bx = cx + rMid * Math.Cos(rad);
                double by = cy + rMid * Math.Sin(rad);

                if (i == 2)
                {
                    // Count badge (blue rounded rect)
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0xFF)), null, new Rect(bx - 17, by - 13, 34, 26), 8, 8);
                    var ft = new FormattedText("10", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeFace, 16, Brushes.White, 96);
                    dc.DrawText(ft, new Point(bx - ft.Width / 2, by - ft.Height / 2));
                }
                else
                {
                    var brush = (i == 0 || i == 4) ? new SolidColorBrush(Color.FromRgb(0x44, 0x77, 0xAA)) : new SolidColorBrush(Color.FromRgb(0x1A, 0x60, 0x90));
                    double fSize = (i == 1 || i == 3) ? 20 : 11;
                    var ft = new FormattedText(texts[i], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeFace, fSize, brush, 96);
                    dc.DrawText(ft, new Point(bx - ft.Width / 2, by - ft.Height / 2));
                }
            }
        }

        var rtb = new RenderTargetBitmap(240, 240, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        string outDir = Path.Combine(Path.GetTempPath(), "BlueRandomTests");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, $"arc_render_{Guid.NewGuid():N}.png");
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(outPath))
            {
                encoder.Save(fs);
            }

            string artifactDir = @"C:\Users\YunHydrogen\.gemini\antigravity\brain\62a767c9-1dd3-454b-a3b5-7d3fffebb95b";
            string previewPath = Path.Combine(artifactDir, "arc_preview.png");
            var encoder2 = new PngBitmapEncoder();
            encoder2.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(previewPath))
            {
                encoder2.Save(fs);
            }

            geom.Freeze();
            Console.WriteLine($"Geom Bounds: {geom.Bounds}");
            Assert.True(geom.Bounds.Width > 0);
            Assert.True(File.Exists(outPath));
        }
        finally
        {
            if (File.Exists(outPath))
            {
                try { File.Delete(outPath); } catch { }
            }
        }
    }
}
