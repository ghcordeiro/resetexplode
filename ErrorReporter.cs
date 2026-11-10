using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>
/// Erros e relatórios de problema. Toda falha vira um arquivo em %AppData%\ResetExplode\erros\pendentes\ (nunca se perde) e, com o token do
/// GitHub configurado, uma issue no repositório do app, que o dono lê no Mac com "gh issue list". O repositório é público: antes de enviar,
/// o nome do usuário, a pasta do perfil e o nome do PC são trocados por marcadores. O mesmo erro automático só vai uma vez por dia.
/// </summary>
public static class ErrorReporter
{
    private static readonly string Dir = Path.Combine(AppSettings.Folder, "erros");
    private static readonly string Pending = Path.Combine(Dir, "pendentes");
    private static readonly string LogPath = Path.Combine(Dir, "erros.log");
    private static readonly string SentPath = Path.Combine(Dir, "enviados.txt");
    private static readonly object Lock = new();
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ResetExplode-Relatos");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    /// <summary>Liga a captura de exceções não tratadas (tela, threads e tarefas).</summary>
    public static void Init(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Capture(e.Exception, "tela (exceção não tratada)", fatal: false);
            e.Handled = true; // o app segue: perder a gravação de uma sessão por um erro de tela seria pior
            try
            {
                MessageBox.Show("Aconteceu um erro inesperado. O RESET EXPLODE continua aberto e guardou um relatório" +
                                (HasToken ? " (enviado para o GitHub)." : ". Configure o token no botão de relatório para enviá-lo.") +
                                "\n\n" + e.Exception.Message, "RESET EXPLODE", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Capture(ex, "thread (o app fechou)", fatal: true);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Capture(e.Exception, "tarefa em segundo plano", fatal: false);
            e.SetObserved();
        };
    }

    private static bool HasToken => !string.IsNullOrEmpty(AppSettings.Load().GitHubToken);

    /// <summary>Linha no erros.log (falhas que o app tratou, para dar contexto aos relatórios).</summary>
    public static void Log(string where, Exception ex) => Log($"{where}: {ex.GetType().Name}: {ex.Message}");

    public static void Log(string line)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                    File.WriteAllLines(LogPath, File.ReadAllLines(LogPath).TakeLast(500));
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>Guarda o relatório da exceção em pendentes e tenta enviar (no erro fatal, espera o envio por até 8 s).</summary>
    public static void Capture(Exception ex, string where, bool fatal)
    {
        Log(where, ex);
        string sig = Signature(ex);
        string title = $"[relato] {(fatal ? "Fechou: " : "")}{ex.GetType().Name}: {OneLine(ex.Message, 90)}";
        string body = BuildBody(where, null, ex, null);
        string file = SavePending(title, body, sig);
        var send = Task.Run(() => SendPendingAsync());
        if (fatal) try { send.Wait(TimeSpan.FromSeconds(8)); } catch { }
    }

    /// <summary>Relatório manual: descrição do usuário + log + última sessão. Devolve o link da issue ou o motivo de não ter enviado.</summary>
    public static async Task<string> SendManualAsync(string description, string? sessionFolder)
    {
        string? extra = null;
        if (sessionFolder != null)
        {
            var sb = new StringBuilder();
            foreach (var name in new[] { "laudo.txt", "resumo.json" })
            {
                string p = Path.Combine(sessionFolder, name);
                if (!File.Exists(p)) continue;
                string t = File.ReadAllText(p);
                if (t.Length > 20000) t = t[..20000] + "\n…(cortado)";
                sb.Append("\n<details><summary>").Append(name).Append(" (").Append(Path.GetFileName(sessionFolder)).Append(")</summary>\n\n```\n").Append(t).Append("\n```\n</details>\n");
            }
            extra = sb.ToString();
        }
        string title = "[relato] " + OneLine(description, 80);
        string file = SavePending(title, BuildBody("relatório manual", description, null, extra), null);
        var sent = await SendPendingAsync();
        return sent.FirstOrDefault(s => s.File == file) is { } r ? r.Result : "guardado; não enviado";
    }

    /// <summary>Manda um texto pronto (resumo de teste) como issue "[teste]". Devolve o link ou o motivo de não ter enviado.</summary>
    public static async Task<string> SendTextAsync(string title, string markdown)
    {
        string body = Scrub(markdown);
        if (body.Length > 60000) body = body[..60000] + "\n…(cortado)";
        string file = SavePending("[teste] " + OneLine(title, 90), body, null);
        var sent = await SendPendingAsync();
        return sent.FirstOrDefault(x => x.File == file) is { } r ? r.Result : "guardado; sem token do GitHub, não enviado";
    }

    /// <summary>Envia o que estiver em pendentes. Sem token, não faz nada (os arquivos ficam para depois).</summary>
    public static async Task<List<(string File, string Result)>> SendPendingAsync()
    {
        var results = new List<(string, string)>();
        string? token = AppSettings.Load().GitHubToken;
        if (string.IsNullOrEmpty(token) || !Directory.Exists(Pending)) return results;
        foreach (var file in Directory.GetFiles(Pending, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
                string title = doc.RootElement.GetProperty("title").GetString() ?? "[relato]";
                string body = doc.RootElement.GetProperty("body").GetString() ?? "";
                string? sig = doc.RootElement.TryGetProperty("sig", out var s) ? s.GetString() : null;
                if (sig != null && SentRecently(sig)) { File.Delete(file); results.Add((file, "repetido nas últimas 24 h: não reenviado")); continue; }

                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{Updater.Repo}/issues")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { title, body }), Encoding.UTF8, "application/json"),
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await Http.SendAsync(req);
                string text = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    string why = (int)resp.StatusCode switch
                    {
                        401 => "token inválido ou expirado",
                        403 or 404 => "o token não tem permissão de Issues neste repositório",
                        410 => "as issues estão desligadas no repositório",
                        _ => $"GitHub respondeu {(int)resp.StatusCode}",
                    };
                    results.Add((file, "não enviado: " + why));
                    Log("envio de relatório: " + why);
                    break; // os outros falhariam igual
                }
                using var created = JsonDocument.Parse(text);
                string url = created.RootElement.GetProperty("html_url").GetString() ?? "";
                File.Delete(file);
                if (sig != null) MarkSent(sig);
                results.Add((file, url));
            }
            catch (Exception ex) { results.Add((file, "não enviado: " + ex.Message)); break; }
        }
        return results;
    }

    public static int PendingCount => Directory.Exists(Pending) ? Directory.GetFiles(Pending, "*.json").Length : 0;

    // ------------------------------------------------------------------ montagem

    private static string BuildBody(string where, string? description, Exception? ex, string? extra)
    {
        var sb = new StringBuilder();
        sb.Append($"**Versão:** {Updater.CurrentVersion} · **Windows:** {RuntimeInformation.OSDescription} · **Quando:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        sb.Append($"**Onde:** {where}\n\n");
        if (!string.IsNullOrWhiteSpace(description)) sb.Append("### O que aconteceu\n\n").Append(description.Trim()).Append("\n\n");
        if (ex != null) sb.Append("### Exceção\n\n```\n").Append(ex).Append("\n```\n\n");
        try
        {
            if (File.Exists(LogPath))
                sb.Append("<details><summary>Últimas linhas do erros.log</summary>\n\n```\n")
                  .Append(string.Join('\n', File.ReadAllLines(LogPath).TakeLast(40))).Append("\n```\n</details>\n");
        }
        catch { }
        if (extra != null) sb.Append(extra);
        string body = Scrub(sb.ToString());
        return body.Length > 60000 ? body[..60000] + "\n…(cortado)" : body;
    }

    /// <summary>Tira do texto o que identifica a pessoa e o PC (o repositório é público).</summary>
    public static string Scrub(string text)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (Environment.UserName.Length >= 3) text = text.Replace(Environment.UserName, "<usuario>", StringComparison.OrdinalIgnoreCase);
        if (Environment.MachineName.Length >= 3) text = text.Replace(Environment.MachineName, "<pc>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string SavePending(string title, string body, string? sig)
    {
        string file = Path.Combine(Pending, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        try
        {
            Directory.CreateDirectory(Pending);
            File.WriteAllText(file, JsonSerializer.Serialize(new { title = Scrub(title), body, sig }));
        }
        catch { }
        return file;
    }

    /// <summary>Tipo da exceção + primeira linha da pilha: o mesmo erro em lugares diferentes conta como outro.</summary>
    private static string Signature(Exception ex)
    {
        string frame = (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ex.GetType().FullName + "|" + frame)))[..16];
    }

    private static bool SentRecently(string sig)
    {
        try
        {
            if (!File.Exists(SentPath)) return false;
            return File.ReadAllLines(SentPath).Select(l => l.Split('|')).Any(p => p.Length == 2 && p[0] == sig &&
                DateTime.TryParse(p[1], out var when) && DateTime.Now - when < TimeSpan.FromHours(24));
        }
        catch { return false; }
    }

    private static void MarkSent(string sig)
    {
        try
        {
            var lines = File.Exists(SentPath) ? File.ReadAllLines(SentPath).TakeLast(200).ToList() : new List<string>();
            lines.Add($"{sig}|{DateTime.Now:s}");
            File.WriteAllLines(SentPath, lines);
        }
        catch { }
    }

    private static string OneLine(string s, int max)
    {
        s = string.Join(' ', s.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }
}
