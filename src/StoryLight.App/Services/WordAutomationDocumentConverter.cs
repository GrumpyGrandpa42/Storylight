using System.Diagnostics;

namespace StoryLight.App.Services;

internal static class WordAutomationDocumentConverter
{
    public static async Task<bool> TryConvertDocToDocxAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var script = """
            $source = %SOURCE%
            $destination = %DESTINATION%
            $word = $null
            $document = $null
            try {
                $word = New-Object -ComObject Word.Application
                $word.Visible = $false
                $document = $word.Documents.Open($source)
                $document.SaveAs2($destination, 16)
                $document.Close()
                $word.Quit()
                exit 0
            }
            catch {
                if ($document -ne $null) { $document.Close($false) }
                if ($word -ne $null) { $word.Quit() }
                exit 1
            }
            """;

        var escapedSource = $"'{sourcePath.Replace("'", "''", StringComparison.Ordinal)}'";
        var escapedDestination = $"'{destinationPath.Replace("'", "''", StringComparison.Ordinal)}'";
        script = script
            .Replace("%SOURCE%", escapedSource, StringComparison.Ordinal)
            .Replace("%DESTINATION%", escapedDestination, StringComparison.Ordinal);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(script);

        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 && File.Exists(destinationPath);
    }
}
