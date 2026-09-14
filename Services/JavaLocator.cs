using System.Diagnostics;
using System.IO;

namespace MinecraftHostApp.Services;

// Ищет java.exe на этом ПК - раньше путь был зашит намертво под один конкретный JDK на компе
// разработчика, из-за чего у любого друга при установке хостинг сломался бы сразу.
public static class JavaLocator
{
    public static string? Find()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            var candidate = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(candidate)) return candidate;
        }

        try
        {
            var psi = new ProcessStartInfo("where", "java") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                var first = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && File.Exists(l));
                if (first != null) return first;
            }
        }
        catch { /* "where" может отсутствовать в редких урезанных системах - тогда просто идём дальше */ }

        var roots = new[]
        {
            @"C:\Program Files\Java",
            @"C:\Program Files\Eclipse Adoptium",
            @"C:\Program Files\Microsoft",
            @"C:\Program Files (x86)\Java",
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var candidate = Path.Combine(dir, "bin", "java.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }
}
