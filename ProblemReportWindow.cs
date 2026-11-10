using System.Windows;
using System.Windows.Controls;

namespace ResetExplode;

/// <summary>Janela "Relatar problema": descrição livre, opção de anexar a última sessão e o token do GitHub (pedido só na primeira vez).</summary>
public sealed class ProblemReportWindow : Window
{
    private const string TokenUrl = "https://github.com/settings/personal-access-tokens/new";
    private readonly AppSettings _settings;
    private readonly string? _lastSession;
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly CheckBox _attach = new() { Content = "Anexar o laudo e o resumo da última sessão gravada", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly PasswordBox _token = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold };
    private readonly Button _send;

    public ProblemReportWindow(AppSettings settings, string? lastSessionFolder)
    {
        _settings = settings;
        _lastSession = lastSessionFolder;
        Title = "Relatar problema";
        Width = 560; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(Label("O QUE ACONTECEU?"));
        root.Children.Add(Muted("Descreva o que você fazia e o que esperava. Vai como issue no GitHub do app (repositório público: nome de usuário e do PC são trocados por marcadores)."));
        root.Children.Add(_text);
        _attach.IsEnabled = _lastSession != null;
        if (_lastSession == null) _attach.IsChecked = false;
        root.Children.Add(_attach);

        bool hasToken = !string.IsNullOrEmpty(settings.GitHubToken);
        var tokenBox = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        tokenBox.Children.Add(Label(hasToken ? "TOKEN DO GITHUB (CONFIGURADO; PREENCHA SÓ PARA TROCAR)" : "TOKEN DO GITHUB (SÓ NA PRIMEIRA VEZ)"));
        tokenBox.Children.Add(Muted("Crie um token \"fine-grained\": Repository access → Only select repositories → resetexplode; " +
                                    "Permissions → Issues: Read and write. Nada mais. Ele fica cifrado neste PC (DPAPI)."));
        var open = new Button { Content = "CRIAR TOKEN NO GITHUB", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
        open.SetResourceReference(StyleProperty, "OutlineButton");
        open.Click += (_, _) => ShellRunner.OpenUrl(TokenUrl);
        tokenBox.Children.Add(open);
        tokenBox.Children.Add(_token);
        root.Children.Add(tokenBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        _send = new Button { Content = "ENVIAR", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0) };
        _send.SetResourceReference(StyleProperty, "PrimaryButton");
        _send.Click += Send_Click;
        var close = new Button { Content = "FECHAR", Padding = new Thickness(18, 8, 18, 8) };
        close.SetResourceReference(StyleProperty, "OutlineButton");
        close.Click += (_, _) => Close();
        buttons.Children.Add(_send);
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        int pending = ErrorReporter.PendingCount;
        if (pending > 0) _status.Text = $"{pending} relatório(s) de erro esperando envio: vão junto.";
        Content = root;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_token.Password.Trim().Length > 0) { _settings.GitHubToken = _token.Password.Trim(); _settings.Save(); }
        if (string.IsNullOrEmpty(_settings.GitHubToken)) { _status.Text = "Cole o token do GitHub para enviar."; return; }
        if (_text.Text.Trim().Length < 5) { _status.Text = "Escreva o que aconteceu."; return; }
        _send.IsEnabled = false;
        _status.Text = "Enviando…";
        try
        {
            string r = await ErrorReporter.SendManualAsync(_text.Text, _attach.IsChecked == true ? _lastSession : null);
            _status.Text = r.StartsWith("http") ? $"Enviado: {r}" : $"Guardado neste PC e não enviado ({r}). Ele vai na próxima vez que der certo.";
            if (r.StartsWith("http")) _text.Text = "";
        }
        catch (Exception ex) { _status.Text = "Falhou: " + ex.Message; }
        finally { _send.IsEnabled = true; }
    }

    private static TextBlock Label(string t)
    {
        var b = new TextBlock { Text = t, Margin = new Thickness(0, 0, 0, 6) };
        b.SetResourceReference(StyleProperty, "Label");
        return b;
    }

    private static TextBlock Muted(string t)
    {
        var b = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
        b.SetResourceReference(ForegroundProperty, "Muted");
        return b;
    }
}
