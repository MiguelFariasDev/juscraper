using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Advocacia.BuildingBlocks.Domain.Errors;
using Advocacia.BuildingBlocks.Domain.Results;
using Advocacia.Modules.Legal.Application.Abstractions;
using Advocacia.Modules.Legal.Domain.Shared;
using Microsoft.Extensions.Logging;

namespace Advocacia.Modules.Legal.Infrastructure.Integrations.Juscraper;

/// <summary>
/// Adaptador entre a porta do módulo Legal (<see cref="IConsultaProcessoTribunal"/>) e o
/// serviço juscraper, que consulta direto os portais públicos dos tribunais (e-SAJ e a nova
/// consulta pública do PJe). É a "outra fonte" prevista no ADR-064: trocar o DataJud por
/// ele é trocar o registro em DependencyInjection — handler, endpoint e tela não mudam.
///
/// Vocabulário do juscraper (instâncias, ProblemDetails, códigos próprios) para aqui; o que
/// segue para o handler é <see cref="ProcessoTribunal"/>.
///
/// NUNCA loga o corpo da resposta (partes e andamentos de processo real) — mesma regra do
/// DataJudClient. O log carrega tribunal, desfecho e código de erro.
/// </summary>
public sealed class JuscraperConsultaProcessoTribunal(
    HttpClient httpClient,
    ILogger<JuscraperConsultaProcessoTribunal> logger) : IConsultaProcessoTribunal
{
    public const string HttpClientName = "juscraper";

    /// <summary>Quantos movimentos seguem para a tela — o mesmo teto do adaptador DataJud.</summary>
    private const int MaxMovimentos = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<ProcessoTribunal?>> ConsultarAsync(
        CNJNumber cnjNumber, CancellationToken cancellationToken = default)
    {
        // O juscraper deriva o tribunal do número, como o TribunalResolver. Checar aqui só
        // evita uma chamada de rede para um número que não aponta para lugar nenhum.
        var tribunal = cnjNumber.Tribunal;
        if (tribunal is null)
        {
            return Result.Failure<ProcessoTribunal?>(
                ErrorFactory.From(ErrorCode.CNJ_TRIBUNAL_NOT_SUPPORTED, $"{cnjNumber.Segment}.{cnjNumber.Court}"));
        }

        // max_movimentacoes por instância: cada tribunal devolve do mais recente para o mais
        // antigo, então os 10 mais recentes de cada instância bastam para os 10 mais recentes
        // no total — e no TJSP isso são 2 requisições ao portal em vez de dezenas.
        var caminho = $"v1/processos/{cnjNumber.Digits}?max_movimentacoes={MaxMovimentos}";

        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(caminho, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                          && !cancellationToken.IsCancellationRequested)
        {
            var codigo = exception is TaskCanceledException ? ErrorCode.CNJ_TIMEOUT : ErrorCode.CNJ_UNAVAILABLE;
            logger.LogWarning(exception, "juscraper: consulta ao {Tribunal} falhou ({Codigo})", tribunal, codigo);
            return Result.Failure<ProcessoTribunal?>(ErrorFactory.From(codigo));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return Result.Failure<ProcessoTribunal?>(await TraduzirErroAsync(response, tribunal, cancellationToken));
            }

            JuscraperResultado? resultado;
            try
            {
                resultado = await response.Content.ReadFromJsonAsync<JuscraperResultado>(Json, cancellationToken);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "juscraper: resposta ilegível para o {Tribunal}", tribunal);
                return Result.Failure<ProcessoTribunal?>(ErrorFactory.From(ErrorCode.CNJ_INVALID_RESPONSE));
            }

            if (resultado is null)
            {
                return Result.Failure<ProcessoTribunal?>(ErrorFactory.From(ErrorCode.CNJ_INVALID_RESPONSE));
            }

            logger.LogInformation(
                "juscraper: {Tribunal} respondeu encontrado={Encontrado} instancias={Instancias} avisos={Avisos}",
                tribunal, resultado.Encontrado, resultado.Processos.Count, resultado.Avisos.Count);

            // Não encontrado é SUCESSO com null (contrato da porta e ADR-064).
            return Result.Success(resultado.Encontrado ? Traduzir(resultado, cnjNumber) : null);
        }
    }

    private static ProcessoTribunal? Traduzir(JuscraperResultado resultado, CNJNumber cnjNumber)
    {
        if (resultado.Processos.Count == 0)
        {
            return null;
        }

        // Metadados do 1º grau quando houver: o cadastro é do processo de origem (vara,
        // classe, distribuição). Movimentos de TODAS as instâncias: com recurso pendente, o
        // andamento que importa costuma estar no tribunal.
        var principal = resultado.Processos.FirstOrDefault(p => p.Grau == "G1") ?? resultado.Processos[0];

        var movimentos = resultado.Processos
            .SelectMany(p => p.Movimentos)
            .OrderByDescending(m => m.DataHora ?? DateTimeOffset.MinValue)
            .Take(MaxMovimentos)
            .Select(m => new MovimentoProcesso(NomeComComplemento(m), m.DataHora))
            .ToArray();

        return new ProcessoTribunal(
            // Número MASCARADO, forma canônica do sistema.
            cnjNumber.Value,
            principal.Tribunal,
            principal.Grau,
            principal.DataAjuizamento,
            resultado.Processos.Any(p => p.EmSegredoDeJustica),
            principal.Classe,
            principal.Sistema,
            principal.OrgaoJulgador,
            principal.Assuntos,
            movimentos,
            principal.CodigoMunicipioIbge);
    }

    /// <summary>
    /// O e-SAJ separa o título do movimento ("Expedido Certidão") do complemento que o
    /// cartório escreve ("Certidão de Remessa da Intimação para o Portal Eletrônico"). Sem o
    /// complemento, metade dos andamentos fica indistinguível na timeline.
    /// </summary>
    private static string NomeComComplemento(JuscraperMovimento movimento)
    {
        if (string.IsNullOrWhiteSpace(movimento.Complemento)
            || movimento.Complemento.Equals(movimento.Nome, StringComparison.OrdinalIgnoreCase))
        {
            return movimento.Nome;
        }

        const int limite = 300;
        var complemento = movimento.Complemento.Length > limite
            ? movimento.Complemento[..limite].TrimEnd() + "…"
            : movimento.Complemento;
        return $"{movimento.Nome} — {complemento}";
    }

    private async Task<Error> TraduzirErroAsync(
        HttpResponseMessage response, string tribunal, CancellationToken cancellationToken)
    {
        JuscraperProblema? problema = null;
        try
        {
            problema = await response.Content.ReadFromJsonAsync<JuscraperProblema>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            // Sem ProblemDetails (proxy, gateway): cai no mapeamento por status abaixo.
        }

        logger.LogWarning(
            "juscraper: {Tribunal} respondeu {Status} {Codigo}", tribunal, (int)response.StatusCode, problema?.Codigo);

        return problema?.Codigo switch
        {
            "NUMERO_CNJ_INVALIDO" => ErrorFactory.From(ErrorCode.CNJ_INVALID_REQUEST),
            // Tribunal sem scraper, com captcha ou com bloqueio anti-robô: para a tela, é
            // "não dá para consultar este tribunal automaticamente" — preencha manualmente.
            "TRIBUNAL_NAO_SUPORTADO" or "CAPTCHA_EXIGIDO" or "ACESSO_BLOQUEADO"
                => ErrorFactory.From(ErrorCode.CNJ_TRIBUNAL_NOT_SUPPORTED, tribunal),
            "TRIBUNAL_INDISPONIVEL" => ErrorFactory.From(ErrorCode.CNJ_UNAVAILABLE),
            "RESPOSTA_INESPERADA" => ErrorFactory.From(ErrorCode.CNJ_INVALID_RESPONSE),
            "NAO_AUTORIZADO" => ErrorFactory.From(ErrorCode.CNJ_AUTH_FAILED),
            _ => response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ErrorFactory.From(ErrorCode.CNJ_AUTH_FAILED),
                HttpStatusCode.TooManyRequests => ErrorFactory.From(ErrorCode.CNJ_RATE_LIMITED),
                HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout => ErrorFactory.From(ErrorCode.CNJ_TIMEOUT),
                _ => ErrorFactory.From(ErrorCode.CNJ_UNAVAILABLE),
            },
        };
    }
}
