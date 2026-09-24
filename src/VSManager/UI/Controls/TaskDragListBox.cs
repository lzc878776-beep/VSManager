using System;
using System.Drawing;
using System.Windows.Forms;

namespace VSManager
{
    /// <summary>任务专用拖拽列表，其他 VS 列表的点击语义不变。/ Task-only drag list; other VS lists keep their click semantics.</summary>
    internal sealed class TaskDragListBox : VsListBox
    {
        private sealed class DragSession
        {
            internal object Payload;
        }

        internal Func<object, object> CreateDrag;
        internal Func<object, Point, int> PreviewDrop;
        internal Action<object, Point> CommitDrop;
        internal Action<string> HeaderClick;
        internal Func<IDataObject, DragDropEffects> DragRunner;
        private object _pending;
        private Point _origin, _dragPoint;
        private string _header;
        private bool _headerPress, _cancelled;
        private DragSession _session;
        private int _slot = -1;
        private readonly Timer _scroll = new Timer { Interval = 100 };
        internal int InsertionSlot => _slot;

        internal TaskDragListBox()
        {
            AllowDrop = true;
            _scroll.Tick += (s, e) => ScrollDrag();
        }

        private object ItemAt(Point point)
        {
            int i = IndexFromPoint(point);
            return i >= 0 && i < Items.Count && GetItemRectangle(i).Contains(point) ? Items[i] : null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _pending = null;
            _header = null;
            if (e.Button == MouseButtons.Left)
            {
                var item = ItemAt(e.Location);
                _pending = CreateDrag?.Invoke(item);
                _header = (item as TaskGroupHeader)?.Key;
                _origin = e.Location;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_pending == null || e.Button != MouseButtons.Left) return;
            var size = SystemInformation.DragSize;
            if (new Rectangle(_origin.X - size.Width / 2, _origin.Y - size.Height / 2, size.Width, size.Height).Contains(e.Location)) return;
            _session = new DragSession { Payload = _pending };
            _pending = null;
            _header = null;
            _cancelled = false;
            Capture = false;
            try
            {
                var data = new DataObject(typeof(DragSession).FullName, _session);
                if (DragRunner != null) DragRunner(data);
                else DoDragDrop(data, DragDropEffects.Move);
            }
            finally
            {
                _session = null;
                _headerPress = false;
                ClearFeedback();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            string header = _header;
            _pending = null;
            _header = null;
            if (e.Button == MouseButtons.Left && header != null && (ItemAt(e.Location) as TaskGroupHeader)?.Key == header)
                HeaderClick?.Invoke(header);
            base.OnMouseUp(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture && _session == null) { _pending = null; _header = null; }
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { _pending = null; _header = null; ClearFeedback(); }
            base.OnKeyDown(e);
        }

        protected override void OnQueryContinueDrag(QueryContinueDragEventArgs e)
        {
            base.OnQueryContinueDrag(e);
            if (e.EscapePressed) { _cancelled = true; e.Action = DragAction.Cancel; ClearFeedback(); }
        }

        private bool OwnDrag(IDataObject data) => !_cancelled && _session != null && data != null
            && data.GetDataPresent(typeof(DragSession)) && ReferenceEquals(data.GetData(typeof(DragSession)), _session);

        protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); UpdateDrag(e); }
        protected override void OnDragOver(DragEventArgs e) { base.OnDragOver(e); UpdateDrag(e); }

        private void UpdateDrag(DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (!OwnDrag(e.Data) || (e.AllowedEffect & DragDropEffects.Move) == 0) { ClearFeedback(); return; }
            _dragPoint = PointToClient(new Point(e.X, e.Y));
            _slot = PreviewDrop?.Invoke(_session.Payload, _dragPoint) ?? -1;
            if (_slot >= 0) e.Effect = DragDropEffects.Move;
            _scroll.Start();
            Invalidate();
        }

        protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); ClearFeedback(); }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            UpdateDrag(e);
            if (e.Effect == DragDropEffects.Move) CommitDrop?.Invoke(_session.Payload, _dragPoint);
            ClearFeedback();
        }

        internal void ScrollDrag()
        {
            if (_session == null || !ClientRectangle.Contains(_dragPoint) || Items.Count == 0) return;
            int margin = Math.Min(Dpi.S(32), ClientSize.Height / 3);
            int direction = _dragPoint.Y < margin ? -1 : _dragPoint.Y >= ClientSize.Height - margin ? 1 : 0;
            if (direction == 0) return;
            ScrollByWheel(-direction * 120);
            _slot = PreviewDrop?.Invoke(_session.Payload, _dragPoint) ?? -1;
            Invalidate();
        }

        internal void ClearFeedback()
        {
            _scroll.Stop();
            _slot = -1;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_slot < 0 || Items.Count == 0) return;
            int y = _slot < Items.Count ? GetItemRectangle(_slot).Top : GetItemRectangle(Items.Count - 1).Bottom;
            y = Math.Max(2, Math.Min(ClientSize.Height - 3, y));
            using (var pen = new Pen(Theme.Accent, Dpi.S(3)))
            {
                e.Graphics.DrawLine(pen, Dpi.S(8), y, ClientSize.Width - Dpi.S(8), y);
                e.Graphics.DrawLine(pen, Dpi.S(8), y - Dpi.S(4), Dpi.S(8), y + Dpi.S(4));
                e.Graphics.DrawLine(pen, ClientSize.Width - Dpi.S(8), y - Dpi.S(4), ClientSize.Width - Dpi.S(8), y + Dpi.S(4));
            }
        }

        protected override void WndProc(ref Message m)
        {
            // 使用本条鼠标消息的按钮状态，避免原生列表在启动拖拽前更改选择。/ Use this mouse message's button state, avoiding native selection changes before dragging.
            if (m.Msg == 0x0200 && _pending != null && ((long)m.WParam & 1) != 0)
            {
                var p = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, p.X, p.Y, 0));
                m.Result = IntPtr.Zero;
                return;
            }
            // 原生列表会在按下标题时吞掉鼠标事件；仅本列表延迟到松开再折叠。/ Native lists swallow header presses; defer collapse to release only here.
            if (m.Msg == 0x0201 || m.Msg == 0x0203)
            {
                var p = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                if (ItemAt(p) is TaskGroupHeader)
                {
                    Focus();
                    _headerPress = true;
                    Capture = true;
                    OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
                    m.Result = IntPtr.Zero;
                    return;
                }
            }
            if (m.Msg == 0x0202 && _headerPress)
            {
                _headerPress = false;
                var p = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
                OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
                Capture = false;
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _scroll.Dispose(); _pending = null; _session = null; }
            base.Dispose(disposing);
        }
    }
}
