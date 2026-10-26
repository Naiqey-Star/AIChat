using System;

namespace AIChat.Core
{
    [Flags]
    public enum ResizeEdges { Left = 1, Right = 2, Bottom = 4, Top = 8 }

    public struct WindowBounds
    {
        public float Left, Bottom, Width, Height;
        public WindowBounds(float left, float bottom, float width, float height)
        {
            Left = left;
            Bottom = bottom;
            Width = width;
            Height = height;
        }
    }

    public static class WindowResizeGeometry
    {
        // Bounds and pointer deltas share canvas-local coordinates, with the origin at bottom-left.
        public static WindowBounds Resize(WindowBounds start, float dx, float dy, ResizeEdges edges,
            float canvasWidth, float canvasHeight, float minimumWidth = 500, float minimumHeight = 500, float margin = 12)
        {
            float minWidth = Math.Min(minimumWidth, Math.Max(1, canvasWidth - 2 * margin));
            float minHeight = Math.Min(minimumHeight, Math.Max(1, canvasHeight - 2 * margin));
            float left = start.Left, right = start.Left + start.Width;
            float bottom = start.Bottom, top = start.Bottom + start.Height;
            if ((edges & ResizeEdges.Left) != 0) left = Clamp(left + dx, margin, right - minWidth);
            else if ((edges & ResizeEdges.Right) != 0) right = Clamp(right + dx, left + minWidth, canvasWidth - margin);
            if ((edges & ResizeEdges.Bottom) != 0) bottom = Clamp(bottom + dy, margin, top - minHeight);
            else if ((edges & ResizeEdges.Top) != 0) top = Clamp(top + dy, bottom + minHeight, canvasHeight - margin);
            return new WindowBounds(left, bottom, right - left, top - bottom);
        }

        private static float Clamp(float value, float minimum, float maximum) => Math.Max(minimum, Math.Min(maximum, value));
    }
}
