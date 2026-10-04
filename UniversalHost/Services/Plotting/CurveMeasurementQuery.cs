using System;
using UniversalHost.Models;

namespace UniversalHost.Services.Plotting;

/// <summary>按绘图使用的物理采样索引查询，避免误用最新值为零的历史索引。</summary>
internal static class CurveMeasurementQuery
{
    public static bool TryGetIntersection(CurvePlotBuffer history, double x, out double y)
    {
        y = double.NaN;
        int count = history.Count;
        if (!double.IsFinite(x) || x < 0 || x > count - 1) return false;

        int first = (int)Math.Floor(x);
        double y1 = history.GetPhysicalValue(first);
        if (!double.IsFinite(y1)) return false;
        double fraction = x - first;
        if (fraction == 0)
        {
            y = y1;
            return true;
        }

        double y2 = history.GetPhysicalValue(first + 1);
        if (!double.IsFinite(y2)) return false;
        // 与原始点折线一致；数据间隙和采样范围外不插值、不外推。
        y = y1 * (1 - fraction) + y2 * fraction;
        return double.IsFinite(y);
    }

    public static bool TryGetNearestPoint(CurvePlotBuffer history, double mouseX, double mouseY,
        double xUnitsPerPixel, double yUnitsPerPixel, double radius,
        out int index, out double value)
    {
        index = -1;
        value = double.NaN;
        if (history.Count == 0 || !double.IsFinite(mouseX) || !double.IsFinite(mouseY) ||
            !double.IsFinite(xUnitsPerPixel) || xUnitsPerPixel <= 0 ||
            !double.IsFinite(yUnitsPerPixel) || yUnitsPerPixel <= 0 ||
            !double.IsFinite(radius) || radius <= 0) return false;

        double left = Math.Max(0, Math.Ceiling(mouseX - radius * xUnitsPerPixel));
        double right = Math.Min(history.Count - 1, Math.Floor(mouseX + radius * xUnitsPerPixel));
        if (left > right) return false;

        int first = (int)left;
        int last = (int)right;
        int bestIndex = -1;
        double bestValue = double.NaN;
        double bestDistance = radius * radius;

        void CheckPoint(int i)
        {
            double y = history.GetPhysicalValue(i);
            if (!double.IsFinite(y)) return;
            double dx = (i - mouseX) / xUnitsPerPixel;
            double dy = (y - mouseY) / yUnitsPerPixel;
            double distance = dx * dx + dy * dy;
            if (distance > bestDistance || (distance == bestDistance && bestIndex >= 0 && i >= bestIndex)) return;
            bestDistance = distance;
            bestIndex = i;
            bestValue = y;
        }

        double GetDistanceBound(int start, int end)
        {
            CurvePlotRange range = history.GetRange(start, end);
            if (!range.HasValues) return double.PositiveInfinity;
            double dx = (Math.Clamp(mouseX, start, end) - mouseX) / xUnitsPerPixel;
            double dy = (Math.Clamp(mouseY, range.Minimum, range.Maximum) - mouseY) / yUnitsPerPixel;
            return dx * dx + dy * dy;
        }

        void Search(int start, int end, double bound)
        {
            if (bound > bestDistance) return;
            if (end - start < 32)
            {
                for (int i = start; i <= end; i++) CheckPoint(i);
                return;
            }

            int middle = start + (end - start) / 2;
            double leftBound = GetDistanceBound(start, middle);
            double rightBound = GetDistanceBound(middle + 1, end);
            if (leftBound <= rightBound)
            {
                Search(start, middle, leftBound);
                Search(middle + 1, end, rightBound);
            }
            else
            {
                Search(middle + 1, end, rightBound);
                Search(start, middle, leftBound);
            }
        }

        // 先取得横向最近的候选，再用现有极值索引剪枝，避免每次鼠标移动扫描百万点。
        CheckPoint((int)Math.Clamp(Math.Round(mouseX), first, last));
        Search(first, last, GetDistanceBound(first, last));
        index = bestIndex;
        value = bestValue;
        return index >= 0;
    }
}
