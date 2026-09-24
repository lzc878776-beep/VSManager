using System;
using System.Collections.Generic;
using System.Drawing;

namespace VSManager
{
    /// <summary>窗格排列方式。/ How the panes are arranged.</summary>
    public enum PaneArrangement
    {
        /// <summary>横向均布：一行排开，放不下时自动换行。/ Side by side in one row, wrapping when they do not fit.</summary>
        Horizontal,
        /// <summary>网格：行列数尽量接近。/ Grid with roughly equal rows and columns.</summary>
        Grid,
    }

    /// <summary>排列结果。/ Layout result.</summary>
    public sealed class PaneGridResult
    {
        public readonly List<Rectangle> Cells = new List<Rectangle>();
        public int Columns, Rows;
        /// <summary>高度低于最小高度（窗格过多）。/ Cells are shorter than the minimum height (too many panes).</summary>
        public bool Cramped;
    }

    /// <summary>
    /// 一键布局的纯计算逻辑：按屏幕工作区等分出各窗格的位置（与 Win32 无关，便于测试）。
    /// Pure layout math for the one-click layout: splits a screen work area into pane cells (no Win32, easy to test).
    /// </summary>
    public static class PaneGrid
    {
        /// <summary>解析排列方式，无法识别时返回 null。/ Parses the arrangement; null when unrecognized.</summary>
        public static PaneArrangement? ParseArrangement(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "": case "horizontal": case "columns": case "row": case "横向": case "横向均布": case "均布": case "水平":
                    return PaneArrangement.Horizontal;
                case "grid": case "tile": case "tiles": case "网格": case "平铺":
                    return PaneArrangement.Grid;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 按编号选择屏幕（1 起，与「屏幕1、屏幕2…」一致）；0 表示自动：有多块屏幕时取第二块，否则取第一块。编号越界返回 -1。
        /// Picks a screen by its 1-based number ("Screen 1, Screen 2…"); 0 = auto: the second screen when there are several,
        /// otherwise the first. Returns -1 when the number is out of range.
        /// </summary>
        public static int PickScreenIndex(int number, int screenCount)
        {
            if (screenCount <= 0) return -1;
            if (number <= 0) return screenCount >= 2 ? 1 : 0;
            return number <= screenCount ? number - 1 : -1;
        }

        /// <summary>
        /// 计算 <paramref name="count"/> 个窗格在 <paramref name="area"/> 中的位置：每格宽度不小于 <paramref name="minWidth"/>，
        /// 一行放不下时换行，各行平均分配行数，最后一行的窗格同样铺满整行宽度。
        /// Computes the cells of <paramref name="count"/> panes in <paramref name="area"/>: each cell is at least
        /// <paramref name="minWidth"/> wide; panes wrap to new rows when a row is full, rows are balanced and the last row
        /// also spans the full width.
        /// </summary>
        public static PaneGridResult Compute(Rectangle area, int count, int minWidth, int minHeight, PaneArrangement arrangement)
        {
            var r = new PaneGridResult();
            if (count <= 0 || area.Width <= 0 || area.Height <= 0) return r;
            int maxCols = Math.Max(1, area.Width / Math.Max(1, minWidth));
            int cols = arrangement == PaneArrangement.Grid ? (int)Math.Ceiling(Math.Sqrt(count)) : count;
            cols = Math.Max(1, Math.Min(cols, Math.Min(count, maxCols)));
            int rows = (count + cols - 1) / cols;
            cols = (count + rows - 1) / rows; // 行间平均分配 / balance items across rows
            r.Columns = cols;
            r.Rows = rows;
            r.Cramped = area.Height / rows < minHeight;

            int placed = 0;
            for (int row = 0; row < rows; row++)
            {
                int inRow = Math.Min(cols, count - placed);
                int top = area.Y + (int)((long)area.Height * row / rows);
                int bottom = area.Y + (int)((long)area.Height * (row + 1) / rows);
                for (int c = 0; c < inRow; c++)
                {
                    int left = area.X + (int)((long)area.Width * c / inRow);
                    int right = area.X + (int)((long)area.Width * (c + 1) / inRow);
                    r.Cells.Add(Rectangle.FromLTRB(left, top, right, bottom));
                }
                placed += inRow;
            }
            return r;
        }
    }
}
