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
            };

            try
            {
                var proc = Process.Start(psi);
                proc.WaitForExit();
                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ERROR: {ex.Message}");
                Console.ResetColor();
                Console.WriteLine();
                Console.Write("  Press any key to exit...");
                Console.ReadKey(true);
                return 1;
            }
        }
    }
}
