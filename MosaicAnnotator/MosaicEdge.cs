using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace MosaicAnnotator
{
    public class MosaicEdge
    {
        public bool IsVertical { get; private set; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public double Length { get; set; }

        public Line VisibleLine { get; private set; }
        public Line HitboxLine { get; private set; }
        public Rectangle StartHandle { get; private set; }
        public Rectangle EndHandle { get; private set; }

        public MosaicEdge(bool isVertical, double startX, double startY, double length)
        {
            IsVertical = isVertical;
            X = startX;
            Y = startY;
            Length = length;

            VisibleLine = new Line { Stroke = new SolidColorBrush(Colors.Lime), StrokeThickness = 2 };

            // Thick transparent line for body dragging. Tag=this links the UI shape back to this class.
            HitboxLine = new Line { Stroke = new SolidColorBrush(Colors.Transparent), StrokeThickness = 16, Tag = this };

            // Invisible grab handles for extending/shortening the ends
            StartHandle = new Rectangle { Width = 20, Height = 20, Fill = new SolidColorBrush(Colors.Transparent), Tag = this };
            EndHandle = new Rectangle { Width = 20, Height = 20, Fill = new SolidColorBrush(Colors.Transparent), Tag = this };

            UpdateRender();
        }

        public void ToggleOrientation()
        {
            IsVertical = !IsVertical;
            UpdateRender();
        }

        public void UpdateRender()
        {
            if (IsVertical)
            {
                VisibleLine.X1 = X; VisibleLine.X2 = X;
                VisibleLine.Y1 = Y; VisibleLine.Y2 = Y + Length;
                HitboxLine.X1 = X; HitboxLine.X2 = X;
                HitboxLine.Y1 = Y; HitboxLine.Y2 = Y + Length;
            }
            else
            {
                VisibleLine.X1 = X; VisibleLine.X2 = X + Length;
                VisibleLine.Y1 = Y; VisibleLine.Y2 = Y;
                HitboxLine.X1 = X; HitboxLine.X2 = X + Length;
                HitboxLine.Y1 = Y; HitboxLine.Y2 = Y;
            }

            // Center the 20x20 invisible handles on the exact endpoints
            Canvas.SetLeft(StartHandle, X - 10);
            Canvas.SetTop(StartHandle, Y - 10);
            Canvas.SetLeft(EndHandle, (IsVertical ? X : X + Length) - 10);
            Canvas.SetTop(EndHandle, (IsVertical ? Y + Length : Y) - 10);
        }

        public void Move(double dx, double dy)
        {
            // Allow free 2D movement by default so multi-selected grids don't deform
            X += dx;
            Y += dy;
        }

        public void ResizeStart(double deltaX, double deltaY)
        {
            if (IsVertical) { Y += deltaY; Length -= deltaY; }
            else { X += deltaX; Length -= deltaX; }
            if (Length < 10) Length = 10; // Prevent inverting the line
            UpdateRender();
        }

        public void ResizeEnd(double deltaX, double deltaY)
        {
            if (IsVertical) Length += deltaY;
            else Length += deltaX;
            if (Length < 10) Length = 10;
            UpdateRender();
        }

        public void AddToCanvas(Canvas canvas)
        {
            canvas.Children.Add(VisibleLine);
            canvas.Children.Add(HitboxLine);
            canvas.Children.Add(StartHandle);
            canvas.Children.Add(EndHandle);
        }

        public void RemoveFromCanvas(Canvas canvas)
        {
            canvas.Children.Remove(VisibleLine);
            canvas.Children.Remove(HitboxLine);
            canvas.Children.Remove(StartHandle);
            canvas.Children.Remove(EndHandle);
        }

        public void SnapStart(double targetCoordinate)
        {
            if (IsVertical)
            {
                double endY = Y + Length;
                if (targetCoordinate < endY) { Y = targetCoordinate; Length = endY - targetCoordinate; }
                else { Y = endY; Length = targetCoordinate - endY; } // Prevents line inversion
            }
            else
            {
                double endX = X + Length;
                if (targetCoordinate < endX) { X = targetCoordinate; Length = endX - targetCoordinate; }
                else { X = endX; Length = targetCoordinate - endX; }
            }
            UpdateRender();
        }

        public void SnapEnd(double targetCoordinate)
        {
            if (IsVertical)
            {
                if (targetCoordinate > Y) { Length = targetCoordinate - Y; }
                else { Length = Y - targetCoordinate; Y = targetCoordinate; }
            }
            else
            {
                if (targetCoordinate > X) { Length = targetCoordinate - X; }
                else { Length = X - targetCoordinate; X = targetCoordinate; }
            }
            UpdateRender();
        }
    }
}