using System;
using System.Diagnostics;

namespace MTGAESInstaller
{
    class Program
    {
        static int Main(string[] args)
        {
            Console.Title = "MTGA+ Installer";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();
            Console.WriteLine("  ==============================");
            Console.WriteLine("       MTGA+ Installer");
            Console.WriteLine("  ==============================");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("  Running install script...");
            Console.WriteLine();

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-ExecutionPolicy Bypass -Command \"irm https://raw.githubusercontent.com/MayerDaniel/MTGA-Enhancement-Suite/main/install.ps1 | iex\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                var proc = Process.Start(psi);

                // Read output in real-time
                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) Console.WriteLine(e.Data);
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(e.Data);
                        Console.ResetColor();
                    }
                };

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                Console.WriteLine();
                if (proc.ExitCode == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("  ==============================");
                    Console.WriteLine("    Installation successful!");
                    Console.WriteLine("  ==============================");
                    Console.ResetColor();
                    Console.WriteLine();
                    Console.WriteLine("  Launch MTGA and look for the MTGA+ button");
                    Console.WriteLine("  in the top navigation bar.");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  ==============================");
                    Console.WriteLine("    Installation failed!");
                    Console.WriteLine("  ==============================");
                    Console.ResetColor();
                    Console.WriteLine();
                    Console.WriteLine("  Check the output above for errors.");
                    Console.WriteLine("  You may need to run this installer as Administrator.");
                }

                Console.WriteLine();
                Console.Write("  Press any key to exit...");
                Console.ReadKey(true);
                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ERROR: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("  Make sure PowerShell is installed on your system.");
                Console.WriteLine();
                Console.Write("  Press any key to exit...");
                Console.ReadKey(true);
                return 1;
            }
        }
    }
}
