using Microsoft.AspNetCore.Diagnostics;

namespace Juscraper.Api;

public sealed class OpcoesApi
{
    public const string Secao = "Juscraper:Api";

    /// <summary>Chaves aceitas em X-Api-Key, separadas por vírgula. Vazio = API aberta (só para dev).</summary>
    public string ApiKeys { get; set; } = string.Empty;
}

public sealed record TribunalInfo(string Sigla, string Sistema, string Status, string Portal, string? Observacao);

public sealed record MovimentacoesResposta(
    string NumeroProcesso,
    string? Tribunal,
    bool Encontrado,
    bool Completas,
    IReadOnlyList<Movimentacao> Movimentos,
    DateTimeOffset ConsultadoEm,
    IReadOnlyList<string> Avisos);

/// <summary>
/// <see cref="JuscraperException"/> → ProblemDetails com <c>codigo</c> estável, <c>tribunal</c> e
/// <c>transitorio</c> (se vale tentar de novo). O <c>type</c> aponta para docs/erros.md.
/// </summary>
public sealed class TratadorDeErros(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not JuscraperException erro)
        {
            return false;
        }

        httpContext.Response.StatusCode = erro.StatusHttp;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = Montar(httpContext, erro.StatusHttp, erro.Codigo, erro.Message, erro.Tribunal, erro.Transitorio),
        });
    }

    public static IResult Problema(HttpContext contexto, int status, string codigo, string detalhe, string? tribunal, bool transitorio)
    {
        var problema = Montar(contexto, status, codigo, detalhe, tribunal, transitorio);
        return TypedResults.Problem(problema);
    }

    private static Microsoft.AspNetCore.Mvc.ProblemDetails Montar(
        HttpContext contexto, int status, string codigo, string detalhe, string? tribunal, bool transitorio) => new()
    {
        Type = $"https://github.com/MiguelFariasDev/juscraper/blob/main/docs/erros.md#{codigo.ToLowerInvariant()}",
        Title = codigo,
        Status = status,
        Detail = detalhe,
        Instance = contexto.Request.Path,
        Extensions =
        {
            ["codigo"] = codigo,
            ["tribunal"] = tribunal,
            ["transitorio"] = transitorio,
            ["timestamp"] = DateTimeOffset.UtcNow,
        },
    };
}
