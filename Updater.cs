using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>Versão nova publicada pelo CI (pre-release "cs2-N" no GitHub).</summary>
public sealed record UpdateInfo(int Build, string Tag, string ExeUrl, string ShaUrl, string PageUrl);

/// <summary>
/// Atualização automática: cada push no branch pc-de-cs2 gera a pre-release "cs2-N" (N = número do build, igual ao terceiro número da versão do app).
/// O app consulta a API pública de releases, baixa o .exe, confere o SHA-256 publicado junto, troca o próprio arquivo (o Windows deixa renomear
/// um .exe em uso) e abre a versão nova. A antiga fica como ResetExplode.exe.old e é apagada na abertura seguinte.
/// </summary>
public static class Updater
{
    public const string Repo = "ghcordeiro/resetexplode";
    public const string RestartArg = "--apos-atualizar";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ResetExplode-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Número do build desta cópia (terceiro número da versão). 0 em compilação local.</summary>
    public static int CurrentBuild => typeof(Updater).Assembly.GetName().Version?.Build ?? 0;

    public static string CurrentVersion => typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Compilação de desenvolvimento (pasta bin\ do projeto): nunca se atualiza sozinha.</summary>
    public static bool IsDevBuild => (Environment.ProcessPath ?? "").Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) || CurrentBuild == 0;

    /// <summary>Pre-release mais nova com número maior que o desta cópia, ou <c>null</c>.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=20", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        UpdateInfo? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            var m = Regex.Match(tag, @"^cs2-(\d+)$");
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int build) || build <= CurrentBuild) continue;
            string? exe = null, sha = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                string url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.Equals("ResetExplode.exe", StringComparison.OrdinalIgnoreCase)) exe = url;
                else if (name.Equals("ResetExplode.exe.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
            }
            if (exe == null || sha == null) continue; // release incompleta: ignora
            if (best == null || build > best.Build) best = new UpdateInfo(build, tag, exe, sha, r.GetProperty("html_url").GetString() ?? "");
        }
        return best;
    }

    /// <summary>Baixa, confere o SHA-256 e troca o .exe. Depois disso o chamador deve fechar o app; a versão nova já foi aberta.</summary>
    public static async Task ApplyAsync(UpdateInfo u, Action<string> status, CancellationToken ct = default)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("não sei onde está o .exe do app");
        string dir = Path.GetDirectoryName(exe)!;
        string tmp = Path.Combine(dir, "ResetExplode.new.exe");

        status("Baixando a versão nova…");
        string expected = (await Http.GetStringAsync(u.ShaUrl, ct)).Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
        await using (var src = await Http.GetStreamAsync(u.ExeUrl, ct))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst, ct);

        status("Conferindo o arquivo…");
        string actual;
        await using (var f = File.OpenRead(tmp))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
        if (expected.Length != 64 || actual != expected)
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException("o arquivo baixado não confere com o SHA-256 publicado; nada foi trocado");
        }

        status("Instalando…");
        string old = exe + ".old";
        try { if (File.Exists(old)) File.Delete(old); } catch { }
        File.Move(exe, old);
        try { File.Move(tmp, exe); }
        catch { File.Move(old, exe); throw; } // devolve o original se a troca falhar
        Process.Start(new ProcessStartInfo(exe, RestartArg) { UseShellExecute = false, WorkingDirectory = dir });
    }

    /// <summary>Apaga sobras de uma atualização anterior (o .old só pode ser apagado depois que a versão antiga fechou).</summary>
    public static void CleanupOld()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            foreach (var f in new[] { exe + ".old", Path.Combine(Path.GetDirectoryName(exe)!, "ResetExplode.new.exe") })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
        catch { }
    }
}
