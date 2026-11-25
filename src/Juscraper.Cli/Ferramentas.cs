using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Juscraper.Cli;

/// <summary>
/// Grava cada requisição de uma consulta real (URL, status, corpo) como cassete para os testes.
/// Antes de salvar, pseudonimiza: nomes de partes, advogados e magistrados encontrados pelo
/// próprio parser viram "PARTE 1", "ADVOGADO 2"…, e CPF, CNPJ e OAB são mascarados. Processos
/// são públicos, mas o repositório também é — e o teste não precisa do nome real.
/// </summary>
internal sealed partial class Gravador : DelegatingHandler
{
    public Gravador()
        : base(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
    {
    }

    public List<Dictionary<string, object?>> Interacoes { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var resposta = await base.SendAsync(request, cancellationToken);
        var bytes = await resposta.Content.ReadAsByteArrayAsync(cancellationToken);
        var charset = resposta.Content.Headers.ContentType?.CharSet?.Trim('"');
        var corpo = (charset is { Length: > 0 } ? Encoding.GetEncoding(charset) : Encoding.UTF8).GetString(bytes);

        lock (Interacoes)
        {
            Interacoes.Add(new()
            {
                ["method"] = request.Method.Method,
                ["url"] = request.RequestUri!.ToString(),
                ["status"] = (int)resposta.StatusCode,
                ["location"] = resposta.Headers.Location?.ToString(),
                ["content_type"] = resposta.Content.Headers.ContentType?.ToString() ?? "",
                ["body"] = corpo,
            });
        }

        // O corpo já foi lido (e descomprimido): devolve uma cópia sem Content-Encoding.
        var copia = new HttpResponseMessage(resposta.StatusCode) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        foreach (var (nome, valores) in resposta.Headers)
        {
            copia.Headers.TryAddWithoutValidation(nome, valores);
        }

        if (resposta.Content.Headers.ContentType is { } tipo)
        {
            copia.Content.Headers.ContentType = tipo;
        }

        resposta.Dispose();
        return copia;
    }

    public async Task<int> SalvarAsync(ResultadoConsulta resultado, string numero, FileInfo destino, CancellationToken cancellationToken)
    {
        var nomes = new Dictionary<string, string>(StringComparer.Ordinal);
        var contadores = new Dictionary<string, int>();

        void Registrar(string? nome, string tipo)
        {
            if (nome is { Length: > 3 } && !nomes.ContainsKey(nome))
            {
                contadores[tipo] = contadores.GetValueOrDefault(tipo) + 1;
                nomes[nome] = $"{tipo} {contadores[tipo]}";
            }
        }

        foreach (var processo in resultado.Processos)
        {
            Registrar(processo.Juiz, "MAGISTRADO");
            foreach (var parte in processo.Partes)
            {
                Registrar(parte.Nome, "PARTE");
                parte.Advogados.ForEach(a => Registrar(a.Nome, "ADVOGADO"));
            }
        }

        // Nomes mais longos primeiro: "MARIA DA SILVA SANTOS" antes de "MARIA DA SILVA".
        var ordem = nomes.Keys.OrderByDescending(n => n.Length).ToList();
        foreach (var interacao in Interacoes)
        {
            var corpo = (string)interacao["body"]!;
            foreach (var nome in ordem)
            {
                foreach (var variante in new[] { nome, nome.ToUpperInvariant(), WebUtility.HtmlEncode(nome) }.Distinct())
                {
                    corpo = corpo.Replace(variante, nomes[nome], StringComparison.Ordinal);
                }
            }

            corpo = Cpf().Replace(corpo, "000.000.000-00");
            corpo = Cnpj().Replace(corpo, "00.000.000/0000-00");
            corpo = OabInput().Replace(corpo, "${1}999999${2}");
            corpo = OabTexto().Replace(corpo, "OAB XX999999");
            interacao["body"] = corpo;
        }

        destino.Directory?.Create();
        await using var arquivo = destino.Create();
        await JsonSerializer.SerializeAsync(arquivo,
            new { numero, instancias = resultado.Processos.Select(p => p.Grau).Distinct(), interacoes = Interacoes },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping },
            cancellationToken);
        return nomes.Count;
    }

    [GeneratedRegex(@"\d{3}\.\d{3}\.\d{3}-\d{2}")]
    private static partial Regex Cpf();

    [GeneratedRegex(@"\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}")]
    private static partial Regex Cnpj();

    [GeneratedRegex(@"(<input type=""hidden"" value="")\d{3,7}([A-Z]{2}"")")]
    private static partial Regex OabInput();

    [GeneratedRegex(@"OAB [A-Z]{2}\d+[A-Z]?")]
    private static partial Regex OabTexto();
}

/// <summary>
/// Verifica se um portal PJe usa a NOVA consulta pública (Angular + API REST). Rode de um
/// servidor NO BRASIL: vários tribunais recusam o exterior. No máximo 4 requisições, espaçadas.
/// </summary>
internal static partial class DetectorPje
{
    public static async Task<(string Tipo, string Detalhe, string? Api)> DetectarAsync(string portal, CancellationToken cancellationToken)
    {
        portal = portal.TrimEnd('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("juscraper-detector/1.0");

        using var raiz = await http.GetAsync($"{portal}/", cancellationToken);
        var html = await raiz.Content.ReadAsStringAsync(cancellationToken);
        if (raiz.StatusCode == HttpStatusCode.Forbidden || Bloqueio().IsMatch(html.Length > 20_000 ? html[..20_000] : html))
        {
            return ("BLOQUEADO", $"HTTP {(int)raiz.StatusCode} em {raiz.RequestMessage?.RequestUri}", null);
        }

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        using var env = await http.GetAsync($"{portal}/assets/env.js", cancellationToken);
        if (env.IsSuccessStatusCode && ApiUrl().Match(await env.Content.ReadAsStringAsync(cancellationToken)) is { Success: true } achado)
        {
            var api = achado.Groups[1].Value.TrimEnd('/');
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            using var codigo = await http.GetAsync($"{api}/v1/config/codigo-tribunal", cancellationToken);
            return ("NOVA", $"apiUrl={api} (codigo-tribunal HTTP {(int)codigo.StatusCode})", api);
        }

        foreach (var caminho in new[] { "/pje/ConsultaPublica/listView.seam", "/1g/ConsultaPublica/listView.seam" })
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            using var antiga = await http.GetAsync($"{portal}{caminho}", cancellationToken);
            var corpo = await antiga.Content.ReadAsStringAsync(cancellationToken);
            if (antiga.IsSuccessStatusCode && corpo.Contains("ConsultaPublica", StringComparison.Ordinal))
            {
                return (corpo.Contains("captcha", StringComparison.OrdinalIgnoreCase) ? "ANTIGA_COM_CAPTCHA" : "ANTIGA_SEM_CAPTCHA",
                    antiga.RequestMessage?.RequestUri?.ToString() ?? caminho, null);
            }
        }

        return ("DESCONHECIDO", $"raiz HTTP {(int)raiz.StatusCode}", null);
    }

    [GeneratedRegex(@"apiUrl\s*:\s*['""]([^'""]+)['""]")]
    private static partial Regex ApiUrl();

    [GeneratedRegex(@"Cloudflare|Just a moment|block access from your country|localizados no exterior|Acesso Bloqueado", RegexOptions.IgnoreCase)]
    private static partial Regex Bloqueio();
}
