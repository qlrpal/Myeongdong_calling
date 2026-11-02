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
        if(args is ["--codec-build-info",var codecInfoPath])
        {
            File.WriteAllText(codecInfoPath,System.Text.Json.JsonSerializer.Serialize(OpusBackend.Snapshot(),DiagnosticJson.Readable));
            return;
        }
        _ = OpusBackend.Snapshot();
        ApplicationConfiguration.Initialize();
#if !BETA_RELEASE
        if (args is ["--ui-preview-call", var callPreview])
        { using var preview = new CallForm(); preview.SavePreview(callPreview, members: 6); return; }
        if (args is ["--ui-preview-call-small", var smallCallPreview])
        { using var preview = new CallForm(); preview.SavePreview(smallCallPreview, minimum: true, members: 6); return; }
        if (args is ["--ui-preview-settings", var settingsPreview])
        { using var preview = new CallForm(); preview.SavePreview(settingsPreview, settings: true); return; }
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
#endif
        try
        {
            string? mediaAddress = null, experiment = null;
            int? receiveReserveMs = null;
            var sharedOutput = false;
            if (args.Length % 2 != 0) throw new ArgumentException("옵션에는 값이 필요합니다.");
            for (var i = 0; i < args.Length; i += 2)
            {
                switch (args[i])
                {
                    case "--media-address": mediaAddress = args[i + 1]; break;
#if !BETA_RELEASE
                    case "--receive-reserve-ms":
                        receiveReserveMs = int.TryParse(args[i + 1], System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var reserve) && reserve is >= 10 and <= 40
                            ? reserve : throw new ArgumentException("수신 여유 비교값은 10–40ms 정수여야 합니다."); break;
                    case "--experiment": experiment = args[i + 1]; break;
                    case "--shared-output": sharedOutput = bool.TryParse(args[i + 1], out var enabled) ? enabled : throw new ArgumentException("공통 출력 옵션에는 true 또는 false가 필요합니다."); break;
#endif
                    default: throw new ArgumentException("알 수 없는 실행 옵션: " + args[i]);
                }
            }
            if (receiveReserveMs is not null && experiment is not (null or "baseline"))
                throw new ArgumentException("수신 여유 단독 비교는 기본 모드에서 실행하세요.");
            Application.Run(new CallForm(mediaAddress, experiment, sharedOutput, receiveReserveMs));
        }
        catch (ArgumentException error) { MessageBox.Show(error.Message, "실행 설정"); }
    }
}
