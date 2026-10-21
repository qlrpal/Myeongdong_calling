namespace VoiceNative;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args is ["--ui-preview-small", var smallPath])
        {
            using var preview = new CallForm(); preview.SavePreview(smallPath, minimum: true); return;
        }
        if (args is ["--ui-preview-diagnostics", var diagnosticsPath])
        {
            using var preview = new CallForm(); preview.SavePreview(diagnosticsPath, tab: 1); return;
        }
        if (args is ["--ui-preview", var path])
        {
            using var preview = new CallForm();
            preview.SavePreview(path);
            return;
        }
        if (args is ["--media-address", var mediaAddress])
        {
            try { Application.Run(new CallForm(mediaAddress)); }
            catch (ArgumentException error) { MessageBox.Show(error.Message, "미디어 네트워크 선택"); }
            return;
        }
        Application.Run(new CallForm());
    }
}
