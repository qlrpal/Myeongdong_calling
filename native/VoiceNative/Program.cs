namespace VoiceNative;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if(args is ["--sdk-build-info",var sdkInfoPath])
        {
            File.WriteAllText(sdkInfoPath,System.Text.Json.JsonSerializer.Serialize(SdkBuildIdentity.Read(),DiagnosticJson.Readable));
            return;
        }
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
        try
        {
            string? mediaAddress = null, experiment = null;
            if (args.Length % 2 != 0) throw new ArgumentException("옵션에는 값이 필요합니다.");
            for (var i = 0; i < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--media-address": mediaAddress = args[i + 1]; break;
                    case "--experiment": experiment = args[i + 1]; break;
                    default: throw new ArgumentException("알 수 없는 실행 옵션: " + args[i]);
                }
            }
            Application.Run(new CallForm(mediaAddress, experiment));
        }
        catch (ArgumentException error) { MessageBox.Show(error.Message, "실행 설정"); }
    }
}
