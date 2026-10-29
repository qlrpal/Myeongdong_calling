using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VoiceNative;

internal static class CallNativeUi
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern nint Message(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern nint PointMessage(nint hwnd, uint message, nint wParam, ref Point point);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetWindowTheme(nint hwnd, string theme, string? idList);
    internal static void Dark(Control control)
    {
        if (control.IsHandleCreated) _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        else control.HandleCreated += (_, _) => _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
    }
    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90); path.AddArc(bounds.Right-d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right-d, bounds.Bottom-d, d, d, 0, 90); path.AddArc(bounds.Left, bounds.Bottom-d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}

internal sealed class CallComboBox : ComboBox
{
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (!IsHandleCreated || Width < 24 || Height < 8) return;
        if (message.Msg == 0xF) { using var graphics = CreateGraphics(); PaintArrow(graphics); }
        else if (message.Msg is 0x317 or 0x318 && message.WParam != 0)
        { using var graphics = Graphics.FromHdc(message.WParam); PaintArrow(graphics); }
    }
    private void PaintArrow(Graphics graphics)
    {
        var width = SystemInformation.VerticalScrollBarWidth + 3;
        using var fill = new SolidBrush(CallTheme.Raised);
        graphics.FillRectangle(fill, Width-width, 1, width-1, Height-2);
        using var arrow = new SolidBrush(CallTheme.Secondary);
        var x = Width-width/2; var y = Height/2;
        graphics.FillPolygon(arrow, [new Point(x-4,y-2),new Point(x+4,y-2),new Point(x,y+3)]);
    }
}

internal sealed class CallToggle : CheckBox
{
    private bool hover;
    internal CallToggle()
    { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); Cursor = Cursors.Hand; }
    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        return Appearance == Appearance.Button ? new(text.Width + 24, Math.Max(36, text.Height + 14)) : new(text.Width + 52, Math.Max(30, text.Height + 10));
    }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var foreground = Enabled ? CallTheme.Text : CallTheme.Secondary;
        Rectangle text;
        if (Appearance == Appearance.Button)
        {
            using var path = CallNativeUi.Rounded(new RectangleF(1, 1, Width-3, Height-3), 6);
            using var fill = new SolidBrush(Checked ? Color.FromArgb(58, 32, 35) : hover ? CallTheme.Border : CallTheme.Raised);
            using var border = new Pen(Focused ? CallTheme.Accent : CallTheme.Border);
            e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
            foreground = Checked && Enabled ? CallTheme.Danger : foreground;
            text = new Rectangle(6, 0, Width-12, Height);
        }
        else
        {
            var track = new RectangleF(1, (Height-20)/2f, 36, 20);
            using var path = CallNativeUi.Rounded(track, 10);
            using var fill = new SolidBrush(Checked && Enabled ? CallTheme.Accent : CallTheme.Border);
            e.Graphics.FillPath(fill, path);
            using var knob = new SolidBrush(Checked && Enabled ? Color.FromArgb(16, 34, 25) : CallTheme.Secondary);
            e.Graphics.FillEllipse(knob, track.Left + (Checked ? 19 : 3), track.Top+3, 14, 14);
            text = new Rectangle(46, 0, Math.Max(0, Width-46), Height);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, text, foreground,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (Appearance == Appearance.Button ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left));
        if (Focused && Appearance != Appearance.Button) ControlPaint.DrawFocusRectangle(e.Graphics, text, CallTheme.Accent, BackColor);
    }
}

internal sealed class CallScrollBar : Control
{
    private int maximum, page, value, grab;
    private bool dragging, hover;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Horizontal { get; init; }
    internal event Action<int>? Moved;
    internal CallScrollBar() { DoubleBuffered = true; TabStop = false; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.ScrollBar; }
    internal void UpdateRange(int max, int viewport, int position)
    { maximum = Math.Max(0, max); page = Math.Max(1, viewport); value = Math.Clamp(position, 0, maximum); Visible = maximum > 0; Invalidate(); }
    private int Length => Horizontal ? Width : Height;
    private int ThumbLength => Math.Min(Length, Math.Max(24, (int)(Length * (page / (double)(maximum+page)))));
    private int ThumbStart => maximum == 0 ? 0 : (int)((Length-ThumbLength) * (value/(double)maximum));
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(CallTheme.Base);
        if (Length < 4 || maximum == 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = Horizontal ? new RectangleF(ThumbStart+2, 3, Math.Max(2, ThumbLength-4), Math.Max(3, Height-6)) : new RectangleF(3, ThumbStart+2, Math.Max(3, Width-6), Math.Max(2, ThumbLength-4));
        using var path = CallNativeUi.Rounded(rect, 4);
        using var brush = new SolidBrush(dragging || hover ? CallTheme.Secondary : Color.FromArgb(69, 78, 90));
        e.Graphics.FillPath(brush, path);
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
        var at = Horizontal ? e.X : e.Y;
        if (at >= ThumbStart && at <= ThumbStart+ThumbLength) { grab = at-ThumbStart; dragging = Capture = true; }
        else Moved?.Invoke(Math.Clamp(value + (at < ThumbStart ? -page : page), 0, maximum));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging && Length > ThumbLength)
            Moved?.Invoke(Math.Clamp((int)(((Horizontal ? e.X : e.Y)-grab) * (maximum/(double)(Length-ThumbLength))), 0, maximum));
    }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = Capture = false; Invalidate(); }
}

// Read-only RichEdit with pixel scroll preservation; no timer and no audio-path work.
internal sealed class ReadingTextView : UserControl
{
    private const uint GetPosition = 0x400+221, SetPosition = 0x400+222;
    private readonly RichTextBox editor;
    private readonly CallScrollBar vertical = new() { Dock = DockStyle.Right, Width = 12 };
    private readonly CallScrollBar horizontal = new() { Dock = DockStyle.Bottom, Height = 12, Horizontal = true };
    private bool syncing, syncQueued, followTailOnShow, extentDirty = true;
    private Point savedPosition;
    private Size contentExtent;
    internal ReadingTextView(string name = "진단 텍스트")
    {
        editor = new ReadEditor(this) { Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, WordWrap = false, HideSelection = false, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.None };
        editor.AccessibleName = name; vertical.AccessibleName = name + " 세로 스크롤"; horizontal.AccessibleName = name + " 가로 스크롤";
        BackColor = editor.BackColor = CallTheme.Base; editor.ForeColor = CallTheme.Text;
        Controls.Add(editor); Controls.Add(vertical); Controls.Add(horizontal);
        vertical.Moved += value => { var point = ViewPosition; point.Y = value; ViewPosition = point; SyncBars(); };
        horizontal.Moved += value => { var point = ViewPosition; point.X = value; ViewPosition = point; SyncBars(); };
        editor.VScroll += (_, _) => SyncBars(); editor.HScroll += (_, _) => SyncBars();
        editor.Resize += (_, _) => { extentDirty = true; SyncBars(); ScheduleSync(); }; editor.TextChanged += (_, _) => { extentDirty = true; SyncBars(); ScheduleSync(); };
        editor.FontChanged += (_, _) => { extentDirty = true; SyncBars(); };
        editor.HandleCreated += (_, _) => ScheduleSync(); VisibleChanged += (_, _) => ScheduleSync();
    }
    private sealed class ReadEditor(ReadingTextView owner) : RichTextBox
    {
        protected override void WndProc(ref Message message)
        {
            if (message.Msg is 0x20A or 0x20E)
            {
                var flags = (int)((long)message.WParam & 0xffff);
                if ((flags & 8) == 0)
                {
                    var delta = (short)((long)message.WParam >> 16);
                    owner.Wheel(delta, message.Msg == 0x20E || (flags & 4) != 0, message.Msg == 0x20E);
                }
                message.Result = 0; return;
            }
            base.WndProc(ref message);
        }
    }
    private void Wheel(int delta, bool horizontalAxis, bool rightward)
    {
        var lines = SystemInformation.MouseWheelScrollLines;
        if (lines == 0) return;
        var page = horizontalAxis ? editor.ClientSize.Width : editor.ClientSize.Height;
        var distance = (int)Math.Round(delta / 120d * (lines < 0 ? page : editor.Font.Height * lines));
        var point = ViewPosition; MeasureExtent();
        if (horizontalAxis) point.X = Math.Clamp(point.X + (rightward ? distance : -distance), 0, Math.Max(0, contentExtent.Width-editor.ClientSize.Width));
        else point.Y = Math.Clamp(point.Y-distance, 0, VerticalMaximum);
        ViewPosition = point; SyncBars();
    }
    private void ScheduleSync()
    {
        if (syncQueued || !IsHandleCreated || IsDisposed) return;
        syncQueued = true;
        BeginInvoke(() =>
        {
            syncQueued = false; if (IsDisposed || !editor.Visible) return;
            extentDirty = true;
            if (followTailOnShow) { editor.Select(editor.TextLength, 0); editor.ScrollToCaret(); followTailOnShow = false; }
            else ViewPosition = savedPosition;
            SyncBars();
        });
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Point ViewPosition
    {
        get { if (editor.IsHandleCreated && editor.Visible) { var point = Point.Empty; CallNativeUi.PointMessage(editor.Handle, GetPosition, 0, ref point); savedPosition = point; } return savedPosition; }
        set { savedPosition = value; if (editor.IsHandleCreated) CallNativeUi.PointMessage(editor.Handle, SetPosition, 0, ref value); }
    }
    internal string Content => editor.Text;
    internal string FirstVisibleLine => editor.Lines.ElementAtOrDefault(editor.GetLineFromCharIndex(editor.GetCharIndexFromPosition(Point.Empty))) ?? "";
    internal bool HasVerticalScroll => vertical.Visible;
    internal string SelectedContent => editor.SelectedText;
    internal void ShowTail() { editor.Select(editor.TextLength, 0); editor.ScrollToCaret(); SyncBars(); }
    internal void SelectContent(int start, int count) => editor.Select(start, count);
    private void MeasureExtent()
    {
        if (!extentDirty || !editor.Visible) return;
        var position = ViewPosition; var last = editor.GetPositionFromCharIndex(editor.TextLength);
        var height = last.Y + position.Y + editor.Font.Height + 4;
        var width = 0; var index = 0;
        foreach (var line in editor.Lines)
        {
            var end = editor.GetPositionFromCharIndex(Math.Min(editor.TextLength, index+line.Length));
            width = Math.Max(width, end.X + position.X + 8); index += line.Length+1;
        }
        contentExtent = new Size(width, height); extentDirty = false;
    }
    private int VerticalMaximum { get { MeasureExtent(); return Math.Max(0, contentExtent.Height-editor.ClientSize.Height); } }
    private void SyncBars()
    {
        if (syncing || !editor.IsHandleCreated || !editor.Visible || IsDisposed) return;
        syncing = true;
        try
        {
            for (var pass = 0; pass < 3; pass++)
            {
            MeasureExtent();
            var maximumY = VerticalMaximum; var maximumX = Math.Max(0, contentExtent.Width-editor.ClientSize.Width);
            var position = ViewPosition;
            if (position.Y > maximumY || position.X > maximumX) ViewPosition = new Point(Math.Min(position.X, maximumX), Math.Min(position.Y, maximumY));
            position = ViewPosition;
            vertical.UpdateRange(maximumY, editor.ClientSize.Height, position.Y);
            horizontal.UpdateRange(maximumX, editor.ClientSize.Width, position.X);
            }
        }
        finally { syncing = false; }
    }
    internal void ReplaceReadingText(string text, bool followTail = false, int removedLines = 0, int removedCharacters = 0)
    {
        text = text.ReplaceLineEndings("\n");
        if (editor.Text == text) return;
        UpdateReading(() => editor.Text = text, followTail, removedLines, removedCharacters);
    }
    internal void AppendReadingText(string text) => UpdateReading(() => editor.AppendText(text.ReplaceLineEndings("\n")), true, 0, 0);
    private void UpdateReading(Action update, bool followTail, int removedLines, int removedCharacters)
    {
        if (!editor.IsHandleCreated) { update(); return; }
        var point = ViewPosition; var maximum = VerticalMaximum;
        var tail = followTail && editor.SelectionLength == 0 && point.Y >= maximum-editor.Font.Height-2;
        var topLine = editor.GetLineFromCharIndex(editor.GetCharIndexFromPosition(Point.Empty));
        var selection = Math.Max(0, editor.SelectionStart-removedCharacters); var length = editor.SelectionLength;
        CallNativeUi.Message(editor.Handle, 0xB, 0, 0);
        try
        {
            update(); editor.Select(Math.Min(selection, editor.TextLength), Math.Min(length, Math.Max(0, editor.TextLength-selection)));
            if (tail) { editor.Select(editor.TextLength, 0); editor.ScrollToCaret(); followTailOnShow = !editor.Visible; }
            else
            {
                var index = removedLines > 0 ? editor.GetFirstCharIndexFromLine(Math.Max(0, topLine-removedLines)) : -1;
                if (index >= 0) { var current = ViewPosition; point.Y = editor.GetPositionFromCharIndex(index).Y+current.Y; }
                ViewPosition = point;
            }
        }
        finally { CallNativeUi.Message(editor.Handle, 0xB, 1, 0); SyncBars(); editor.Invalidate(); }
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (editor is not null) editor.Font = Font; }
}
