namespace VoiceNative;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args is ["--ui-preview", var path])
        {
            using var preview = new CallForm();
            preview.SavePreview(path);
            return;
        }
        Application.Run(new CallForm());
    }
}
