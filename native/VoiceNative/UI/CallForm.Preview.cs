#if !BETA_RELEASE
using System.Text;

namespace VoiceNative;

public sealed partial class CallForm
{
    private static readonly string[] PreviewNames = ["달리는고양이", "한강라이더", "키보드장인", "북쪽마법사", "초록여우"];
    // Explicit screenshot fixture only; never used by a normal call or timer update.
    private void ShowDesignedSampleCall(int members)
    {
        if (members is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(members));
        designedJoin!.Visible = false; designedCall!.Visible = true; designedCall.BringToFront();
        roomHeading!.Text = "금요일 레이드"; participantCount!.Text = members + " / 6명";
        leave.Enabled = true; callState.Text = "미리보기 · 시험 데이터"; selfName!.Text = "플레이어 · 나"; selfState!.Text = "마이크 켜짐";
        selfCard!.Visible = members > 1; waitingCard!.Visible = members == 1;
        foreach (var name in PreviewNames.Take(members - 1))
        {
            var card = CreateParticipantCard(name); card.State.Text = "연결 상태 미리보기";
            card.AudioState.Text = ParticipantAudioStatus.Describe(name == PreviewNames[0], name == PreviewNames[1]); participants.Controls.Add(card.Panel);
        }
        callHint.Text = "화면 배치 검증용 시험 데이터입니다. 실제 통화는 연결하지 않습니다.";
        ArrangeParticipantCards();
    }
    internal void SavePreview(string path, int tab = 0, bool minimum = false, int members = 0, bool settings = false)
    {
        ShowDesignedPage(tab == 1);
        if (minimum) Size = MinimumSize;
        ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000);
        Show(); Application.DoEvents(); RefreshStats(); timer.Stop();
        nickname.Text = "플레이어"; room.Text = "금요일 레이드";
        if (members > 0) ShowDesignedSampleCall(members);
        if (settings) ToggleDesignedSettings(true);
        PerformLayout(); Application.DoEvents();
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        File.WriteAllText(Path.ChangeExtension(path, ".json"), ReportJson(), Encoding.UTF8);
        Hide();
        timer.Stop(); timer.Dispose(); diagnosticsCancellation.Cancel();
        callSession?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
#endif
