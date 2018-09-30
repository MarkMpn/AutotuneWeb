using System.Diagnostics;

namespace AutotuneRunner
{
    public static class ShellHelper
    {
        public static void Bash(this string cmd)
        {
            var escapedArgs = cmd.Replace("\"", "\\\"");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{escapedArgs}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new System.Exception($"Command exited with status code {process.ExitCode}");
        }
    }
}
