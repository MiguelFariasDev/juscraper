using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Juscraper.Http;

/// <summary>Política de acesso a UM portal.</summary>
/// <param name="IntervaloMinimo">Tempo entre o início de duas requisições ao mesmo host.</param>
/// <param name="Concorrencia">Requisições simultâneas ao mesmo host.</param>
public sealed record PoliticaHost(TimeSpan IntervaloMinimo, int Concorrencia = 2);

public sealed class OpcoesHttp
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public int Tentativas { get; set; } = 3;

    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(2);

    public string UserAgent { get; set; } = "Mozilla/5.0 (compatible; juscraper/1.0; consulta processual pontual)";

    public PoliticaHost PoliticaPadrao { get; set; } = new(TimeSpan.FromSeconds(1));

    public Dictionary<string, PoliticaHost> Politicas { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int MaxRedirecionamentos { get; set; } = 10;
}

/// <summary>Resposta já lida: status, URL final (após redirecionamentos) e corpo.</summary>
public sealed record RespostaHttp(Uri Url, HttpStatusCode Status, string Corpo, string? ContentType);

/// <summary>
/// Um limitador por host, compartilhado por todas as consultas do processo: token bucket de
/// 1 token (intervalo mínimo entre requisições) mais um limite de concorrência.
///
/// Os portais são serviços públicos sem SLA nem limite documentado; ir devagar é o que mantém
/// o acesso e o que distingue uma consulta pontual de uma varredura. O limitador vive no
/// processo: com várias réplicas, cada uma tem o seu orçamento (para escalar, Redis).
/// </summary>
public sealed class LimitadoresPorHost(IOptions<OpcoesHttp> opcoes) : IDisposable
{
    private readonly ConcurrentDictionary<string, (RateLimiter? Intervalo, ConcurrencyLimiter Concorrencia)> _porHost =
        new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<IDisposable> AdquirirAsync(string host, CancellationToken cancellationToken)
    {
        var (intervalo, concorrencia) = _porHost.GetOrAdd(host, Criar);
        var vaga = await concorrencia.AcquireAsync(1, cancellationToken);
        if (intervalo is null)
        {
            return vaga;
        }

        var ficha = await intervalo.AcquireAsync(1, cancellationToken);
        return new Liberacoes(vaga, ficha);
    }

    private (RateLimiter?, ConcurrencyLimiter) Criar(string host)
    {
        var politica = opcoes.Value.Politicas.GetValueOrDefault(host) ?? opcoes.Value.PoliticaPadrao;
        var concorrencia = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = Math.Max(1, politica.Concorrencia),
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });

        RateLimiter? intervalo = politica.IntervaloMinimo <= TimeSpan.Zero
            ? null
            : new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = 1,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = politica.IntervaloMinimo,
                QueueLimit = int.MaxValue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            });

        return (intervalo, concorrencia);
    }

    public void Dispose()
    {
        foreach (var (intervalo, concorrencia) in _porHost.Values)
        {
            intervalo?.Dispose();
            concorrencia.Dispose();
        }
    }

    // A ficha do token bucket não precisa ser devolvida (repõe por tempo), mas é descartada.
    private sealed class Liberacoes(RateLimitLease vaga, RateLimitLease ficha) : IDisposable
    {
        public void Dispose()
        {
            ficha.Dispose();
            vaga.Dispose();
        }
    }
}

/// <summary>Cria uma <see cref="Sessao"/> por consulta. Registrada pelo <c>AddJuscraper</c>.</summary>
public sealed class FabricaDeSessoes(
    IHttpClientFactory httpClientFactory,
    LimitadoresPorHost limitadores,
    IOptions<OpcoesHttp> opcoes,
    TimeProvider timeProvider,
    ILoggerFactory? loggerFactory = null)
{
    public const string NomeClienteHttp = "juscraper";

    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline = CriarPipeline(opcoes.Value, timeProvider, loggerFactory);

    public Sessao Criar(string tribunal) =>
        new(httpClientFactory.CreateClient(NomeClienteHttp), _pipeline, limitadores, opcoes.Value, tribunal,
            loggerFactory?.CreateLogger<Sessao>() ?? NullLogger<Sessao>.Instance);

    /// <summary>
    /// Retry com backoff exponencial e jitter, só para falha transitória (rede, timeout, 408,
    /// 429, 5xx), respeitando Retry-After. Sem jitter, consultas paralelas repetiriam em
    /// rajada sincronizada contra um portal já instável.
    /// </summary>
    private static ResiliencePipeline<HttpResponseMessage> CriarPipeline(
        OpcoesHttp opcoes, TimeProvider timeProvider, ILoggerFactory? loggerFactory)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = timeProvider };
        if (opcoes.Tentativas > 1)
        {
            builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = opcoes.Tentativas - 1,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = opcoes.BackoffBase,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutException>()
                    .Handle<TaskCanceledException>()
                    .HandleResult(r => Transitorio(r.StatusCode)),
                DelayGenerator = args =>
                    args.Outcome.Result?.Headers.RetryAfter?.Delta is { } atraso
                        ? ValueTask.FromResult<TimeSpan?>(TimeSpan.FromSeconds(Math.Min(atraso.TotalSeconds, 60)))
                        : ValueTask.FromResult<TimeSpan?>(null),
                OnRetry = args =>
                {
                    args.Outcome.Result?.Dispose();
                    return ValueTask.CompletedTask;
                },
            });
        }

        builder.AddTimeout(opcoes.Timeout);
        if (loggerFactory is not null)
        {
            builder.ConfigureTelemetry(loggerFactory);
        }

        return builder.Build();
    }

    internal static bool Transitorio(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}

/// <summary>
/// Uma sessão HTTP (cookies próprios) para UMA consulta. O e-SAJ amarra o AJAX das
/// movimentações à sessão aberta no <c>show.do</c>; consultas paralelas não podem misturar cookies.
/// Por isso o handler primário não guarda cookies nem segue redirecionamentos: a sessão faz as duas coisas.
/// </summary>
public sealed partial class Sessao(
    HttpClient http,
    ResiliencePipeline<HttpResponseMessage> pipeline,
    LimitadoresPorHost limitadores,
    OpcoesHttp opcoes,
    string tribunal,
    ILogger<Sessao> logger) : IDisposable
{
    private readonly CookieContainer _cookies = new();

    public string Tribunal => tribunal;

    public async Task<RespostaHttp> GetAsync(
        string url,
        IEnumerable<KeyValuePair<string, string>>? query = null,
        IReadOnlyDictionary<string, string>? cabecalhos = null,
        CancellationToken cancellationToken = default)
    {
        var atual = new Uri(ComQuery(url, query));

        for (var salto = 0; salto <= opcoes.MaxRedirecionamentos; salto++)
        {
            using var resposta = await EnviarAsync(atual, cabecalhos, cancellationToken);

            if ((int)resposta.StatusCode is >= 300 and < 400 && resposta.Headers.Location is { } destino)
            {
                atual = destino.IsAbsoluteUri ? destino : new Uri(atual, destino);
                continue;
            }

            var corpo = await LerCorpoAsync(resposta, cancellationToken);
            var tipo = resposta.Content.Headers.ContentType?.MediaType;
            DetectorBloqueio.Verificar(resposta.StatusCode, corpo, tipo, atual.Host, tribunal);

            if (FabricaDeSessoes.Transitorio(resposta.StatusCode))
            {
                throw new TribunalIndisponivelException(
                    $"{atual.Host} não respondeu após {opcoes.Tentativas} tentativas (HTTP {(int)resposta.StatusCode}).", tribunal);
            }

            if (!resposta.IsSuccessStatusCode)
            {
                throw new RespostaInesperadaException($"{atual.Host} respondeu HTTP {(int)resposta.StatusCode}.", tribunal);
            }

            return new RespostaHttp(atual, resposta.StatusCode, corpo, tipo);
        }

        throw new RespostaInesperadaException($"Redirecionamentos demais a partir de {url}.", tribunal);
    }

    public async Task<System.Text.Json.JsonDocument> GetJsonAsync(
        string url, IEnumerable<KeyValuePair<string, string>>? query = null, CancellationToken cancellationToken = default)
    {
        var resposta = await GetAsync(url, query, new Dictionary<string, string> { ["Accept"] = "application/json" }, cancellationToken);
        try
        {
            return System.Text.Json.JsonDocument.Parse(resposta.Corpo);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new RespostaInesperadaException($"{resposta.Url.Host} devolveu JSON inválido: {exception.Message}", tribunal);
        }
    }

    private async Task<HttpResponseMessage> EnviarAsync(
        Uri uri, IReadOnlyDictionary<string, string>? cabecalhos, CancellationToken cancellationToken)
    {
        try
        {
            return await pipeline.ExecuteAsync(async token =>
            {
                using var _ = await limitadores.AdquirirAsync(uri.Host, token);
                using var requisicao = new HttpRequestMessage(HttpMethod.Get, uri);
                requisicao.Headers.UserAgent.ParseAdd(opcoes.UserAgent);
                requisicao.Headers.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9");
                foreach (var (nome, valor) in cabecalhos ?? new Dictionary<string, string>())
                {
                    requisicao.Headers.TryAddWithoutValidation(nome, valor);
                }

                if (_cookies.GetCookieHeader(uri) is { Length: > 0 } cookie)
                {
                    requisicao.Headers.TryAddWithoutValidation("Cookie", cookie);
                }

                var resposta = await http.SendAsync(requisicao, HttpCompletionOption.ResponseHeadersRead, token);
                if (resposta.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var setCookie in setCookies)
                    {
                        try
                        {
                            _cookies.SetCookies(uri, setCookie);
                        }
                        catch (CookieException)
                        {
                            // Cookie malformado do portal: ignora, não derruba a consulta.
                        }
                    }
                }

                return resposta;
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException
                                              or Polly.Timeout.TimeoutRejectedException
                                              || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogFalha(logger, uri.Host, exception.GetType().Name);
            throw new TribunalIndisponivelException(
                $"{uri.Host} não respondeu após {opcoes.Tentativas} tentativas ({exception.GetType().Name}).", tribunal);
        }
    }

    private static async Task<string> LerCorpoAsync(HttpResponseMessage resposta, CancellationToken cancellationToken)
    {
        var bytes = await resposta.Content.ReadAsByteArrayAsync(cancellationToken);
        var charset = resposta.Content.Headers.ContentType?.CharSet?.Trim('"');
        Encoding encoding;
        try
        {
            encoding = charset is { Length: > 0 } ? Encoding.GetEncoding(charset) : Encoding.UTF8;
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        return encoding.GetString(bytes);
    }

    private static string ComQuery(string url, IEnumerable<KeyValuePair<string, string>>? query)
    {
        if (query is null)
        {
            return url;
        }

        var partes = query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}").ToList();
        if (partes.Count == 0)
        {
            return url;
        }

        return url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + string.Join('&', partes);
    }

    public void Dispose() => http.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Portal {Host} indisponível após as tentativas ({Falha}).")]
    private static partial void LogFalha(ILogger logger, string host, string falha);
}

/// <summary>
/// Página de bloqueio, 403 e captcha viram erros tipados e NÃO são repetidos: insistir contra um
/// bloqueio só o prolonga. Não há nenhuma tentativa de contornar captcha ou detecção de robô.
/// </summary>
public static partial class DetectorBloqueio
{
    public static void Verificar(HttpStatusCode status, string corpo, string? contentType, string host, string tribunal)
    {
        var ehTexto = contentType is null || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                                          || contentType.Contains("text", StringComparison.OrdinalIgnoreCase);
        var inicio = ehTexto ? WebUtility.HtmlDecode(corpo.Length > 20_000 ? corpo[..20_000] : corpo) : string.Empty;

        if (inicio.Length > 0 && Bloqueio().IsMatch(inicio))
        {
            throw new AcessoBloqueadoException(
                Geografico().IsMatch(inicio)
                    ? $"O portal {host} recusa acessos de fora do Brasil."
                    : $"O portal {host} bloqueou o acesso automatizado (página de bloqueio do firewall).",
                tribunal);
        }

        if (status == HttpStatusCode.Forbidden)
        {
            throw new AcessoBloqueadoException($"O portal {host} recusou o acesso (HTTP 403).", tribunal);
        }

        if (ehTexto && Captcha().IsMatch(corpo))
        {
            throw new CaptchaExigidoException(
                $"O portal {host} exige captcha para esta consulta; o juscraper não resolve captchas.", tribunal);
        }
    }

    [GeneratedRegex(@"Bloqueio tempor[aá]rio|classificaram esse acesso como n[aã]o autorizado|Request Rejected|Attention Required! \| Cloudflare|Support ID[^<]{0,40}_BOT\b|<title>Just a moment\.\.\.</title>|Sorry, you have been blocked|configured to block access from your country|localizados no exterior", RegexOptions.IgnoreCase)]
    private static partial Regex Bloqueio();

    [GeneratedRegex(@"configured to block access from your country|localizados no exterior", RegexOptions.IgnoreCase)]
    private static partial Regex Geografico();

    // Captcha ATIVO (widget renderizado), não a mera menção em JavaScript — o e-SAJ tem
    // "uuidCaptcha" no JS de toda página de processo, sem captcha nenhum.
    [GeneratedRegex(@"class=""g-recaptcha""|class=""h-captcha""|class=""cf-turnstile""|www\.google\.com/recaptcha/api\.js|hcaptcha\.com/1/api\.js", RegexOptions.IgnoreCase)]
    private static partial Regex Captcha();
}
