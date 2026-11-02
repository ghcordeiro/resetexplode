using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace ResetExplode;

/// <summary>
/// Programas de terceiros embutidos no .exe (pasta Tools, ver Tools\LEIA-ME.md). São extraídos para
/// %AppData%\ResetExplode\tools e só rodam depois de o SHA-256 bater, conferido com o arquivo aberto SEM permitir escrita
/// ou exclusão até o processo iniciar: como o RESET EXPLODE roda como administrador, isso impede que outro programa
/// troque o executável entre a conferência e a execução.
/// </summary>
public static class EmbeddedTool
{
    public sealed record Tool(string Resource, string FileName, string Sha256);

    public static readonly Tool PresentMon = new("Tools/PresentMon/PresentMon-2.6.0-x64.exe", "PresentMon-2.6.0-x64.exe",
        "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af");

    public static string Folder => Path.Combine(AppSettings.Folder, "tools");

    /// <summary>Inicia a ferramenta (sem janela, saída redirecionada). Lança exceção se o recurso não existir ou o hash não bater.</summary>
    public static Process Start(Tool tool, string arguments)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, tool.FileName);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 1 || !File.Exists(path)) Extract(tool, path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (Convert.ToHexString(SHA256.HashData(locked)).Equals(tool.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    var psi = new ProcessStartInfo(path, arguments)
                    {
                        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Folder,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    };
                    return Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar {tool.FileName}.");
                }
            }
            // Hash diferente (arquivo antigo, corrompido ou trocado): apaga e extrai de novo do próprio .exe.
            File.Delete(path);
        }
        throw new InvalidOperationException($"{tool.FileName} extraído não confere com o embutido.");
    }

    private static void Extract(Tool tool, string path)
    {
        var asm = typeof(EmbeddedTool).Assembly;
        // No build do Windows o nome vem com "\" (RecursiveDir); no do macOS, com "/".
        string? name = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/').Equals(tool.Resource, StringComparison.OrdinalIgnoreCase));
        using var src = (name != null ? asm.GetManifestResourceStream(name) : null)
                        ?? throw new InvalidOperationException($"{tool.FileName} não está embutido neste .exe.");
        string tmp = path + ".tmp";
        using (var dst = File.Create(tmp)) src.CopyTo(dst);
        File.Move(tmp, path, overwrite: true);
    }
}
