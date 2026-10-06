namespace VoiceNative;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
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
