using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Forms;

namespace VoiceNative;

internal static class UiReadingChecks
{
    internal static void Run(string directory)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Check(directory); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Reading UI check deadline");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Check(string directory)
    {
        Directory.CreateDirectory(directory);
        Application.EnableVisualStyles();
        using var form = new Form { ClientSize = new Size(640, 360), ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        using var view = new ReadingTextView { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(10) };
        controls.Controls.Add(new CallToggle { Text = "저지연 설정", Checked = true, AutoSize = true });
        controls.Controls.Add(new CallToggle { Text = "잠긴 설정", Checked = true, Enabled = false, AutoSize = true });
        controls.Controls.Add(new CallToggle { Text = "꺼진 설정", Enabled = false, AutoSize = true });
        var combo = new CallComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false, Width = 210 };
        combo.Items.Add("비활성화된 선택 항목"); combo.SelectedIndex = 0; controls.Controls.Add(combo);
        form.Controls.Add(controls); CallTheme.Apply(form);
        var lines = Enumerable.Range(0, 300).Select(i => $"행 {i:D4} · 진단값과 읽기 위치 검증 " + new string('x', 160)).ToArray();
        var document = string.Join("\r\n", lines) + "\r\n";
        view.ReplaceReadingText(document); form.Show(); Application.DoEvents();
        Require(view.HasVerticalScroll, "Long document requires visible custom scroll thumb");
        view.ViewPosition = new Point(80, 720); Application.DoEvents();
        var wheelStart = view.ViewPosition;
        var editor = view.Controls.OfType<RichTextBox>().Single();
        CallNativeUi.Message(editor.Handle, 0x20A, (nint)(-120 << 16), 0); Application.DoEvents();
        Require(view.ViewPosition.Y > wheelStart.Y, "Vertical wheel input failed");
        CallNativeUi.Message(editor.Handle, 0x20E, (nint)(120 << 16), 0); Application.DoEvents();
        Require(view.ViewPosition.X > wheelStart.X, "Horizontal wheel input failed");
        var before = view.ViewPosition;
        Require(before.Y > 0 && before.X > 0, "Fixture must actually scroll both axes");
        view.SelectContent(view.Content.IndexOf("행 0050", StringComparison.Ordinal), 7);
        var selected = view.SelectedContent;
        // Selecting must not be mistaken for the reader asking to jump to the caret.
        view.ViewPosition = before;
        var top = view.FirstVisibleLine;
        for (var i = 0; i < 30; i++) { view.ReplaceReadingText(document.Replace("행 0000", $"값 {i:D4}", StringComparison.Ordinal)); Application.DoEvents(); }
        Require(view.ViewPosition == before && view.SelectedContent == selected, "Refresh moved reading position/selection: " + before + " -> " + view.ViewPosition + "; selection=" + view.SelectedContent);
        using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height)); image.Save(Path.Combine(directory, "controls.png")); }
        view.Visible = false; view.ReplaceReadingText(document.Replace("행 0000", "값 9999", StringComparison.Ordinal));
        Application.DoEvents(); view.Visible = true; Application.DoEvents();
        Require(view.ViewPosition == before, "Hidden updates lost the reading position");
        view.AppendReadingText("추가 로그\n"); Application.DoEvents();
        Require(view.ViewPosition == before && view.FirstVisibleLine == top, "Append pulled a reader to tail");
        var firstLength = lines[0].Length+1;
        view.ReplaceReadingText(view.Content[firstLength..], followTail: true, removedLines: 1, removedCharacters: firstLength);
        Application.DoEvents();
        Require(view.FirstVisibleLine == top && view.SelectedContent == selected, "Bounded log eviction lost the reading anchor");
        view.ShowTail(); Application.DoEvents();
        var tail = view.ViewPosition;
        view.AppendReadingText("맨 아래에서 따라가기\n"); Application.DoEvents();
        Require(view.ViewPosition.Y > tail.Y, "Tail reader failed to follow append: " + tail + " -> " + view.ViewPosition);
        view.ReplaceReadingText("짧은 문서\n"); Application.DoEvents();
        Require(view.ViewPosition.Y == 0 && !view.HasVerticalScroll, "Short content did not clamp scroll bounds");
        File.WriteAllText(Path.Combine(directory, "reading.json"), JsonSerializer.Serialize(new { refreshes = 30, preservedBothAxes = true, wheelBothAxes = true, hiddenRefresh = true, preservedSelection = true,
            boundedEviction = true, tailFollow = true, shortContentClamped = true }, DiagnosticJson.Readable));
        form.Hide();
        Console.WriteLine("PASS: actual RichEdit both-axis reading/selection across 30 refreshes, append/eviction, tail follow and range clamp.");
    }
}
