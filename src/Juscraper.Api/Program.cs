using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Juscraper;
using Juscraper.Api;
using Juscraper.Servicos;
using Microsoft.AspNetCore.Mvc;

// API HTTP do juscraper — o que o Iustitia consome.
//   GET /health
//   GET /v1/tribunais
//   GET /v1/processos/{numero}                  consulta completa (todas as instâncias)
//   GET /v1/processos/{numero}/movimentacoes    só os andamentos, consolidados; ?desde= para o acompanhamento diário
// Convenções do Iustitia: não encontrado é 200 com encontrado=false (ADR-064); erros em
// ProblemDetails (RFC 9457, ADR-040) com "codigo" estável e "transitorio".

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddJuscraper(builder.Configuration.GetSection(OpcoesJuscraper.Secao));
builder.Services.Configure<OpcoesApi>(builder.Configuration.GetSection(OpcoesApi.Secao));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<TratadorDeErros>();
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapOpenApi();
app.MapHealthChecks("/health");

var chaves = (app.Configuration[$"{OpcoesApi.Secao}:ApiKeys"] ?? string.Empty)
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    .Select(Encoding.UTF8.GetBytes)
    .ToArray();
if (chaves.Length == 0)
{
    app.Logger.LogWarning("Juscraper:Api:ApiKeys vazia: a API está aberta. Defina chaves em produção.");
}

var v1 = app.MapGroup("/v1").AddEndpointFilter(async (contexto, proximo) =>
{
    if (chaves.Length == 0)
    {
        return await proximo(contexto);
    }

    var enviada = Encoding.UTF8.GetBytes(contexto.HttpContext.Request.Headers["X-Api-Key"].ToString());
    // Comparação em tempo constante: não vaza, pelo tempo de resposta, quanto da chave acertou.
    return chaves.Any(chave => CryptographicOperations.FixedTimeEquals(chave, enviada))
        ? await proximo(contexto)
        : TratadorDeErros.Problema(contexto.HttpContext, 401, "NAO_AUTORIZADO", "X-Api-Key ausente ou inválida.", null, false);
});

v1.MapGet("/tribunais", (ConsultaProcessual servico) =>
        servico.Registro.Todos.Select(t => new TribunalInfo(t.Sigla, t.Sistema, t.Status.ToString().ToLowerInvariant(), t.Portal, t.Observacao)))
    .WithName("ListarTribunais")
    .WithSummary("Cobertura e estado de cada tribunal.");

v1.MapGet("/processos/{numero}", async (
        string numero,
        ConsultaProcessual servico,
        CancellationToken cancellationToken,
        [FromQuery] string instancias = "1,2",
        [FromQuery(Name = "max_movimentacoes"), Range(1, 5000)] int? maxMovimentacoes = null,
        [FromQuery(Name = "sem_cache")] bool semCache = false) =>
            await servico.ConsultarAsync(numero, Instancias(instancias), maxMovimentacoes, usarCache: !semCache,
                cancellationToken: cancellationToken))
    .WithName("ConsultarProcesso")
    .WithSummary("Consulta o processo no tribunal derivado do número CNJ. Não encontrado é 200 com encontrado=false.");

v1.MapGet("/processos/{numero}/movimentacoes", async (
        string numero,
        ConsultaProcessual servico,
        CancellationToken cancellationToken,
        [FromQuery] DateOnly? desde = null,
        [FromQuery] string instancias = "1,2",
        [FromQuery(Name = "sem_cache")] bool semCache = false) =>
    {
        var resultado = await servico.ConsultarAsync(numero, Instancias(instancias), desde: desde, usarCache: !semCache,
            cancellationToken: cancellationToken);
        return new MovimentacoesResposta(
            resultado.NumeroProcesso,
            resultado.Tribunal,
            resultado.Encontrado,
            resultado.Processos.All(p => p.MovimentosCompletos),
            [.. resultado.MovimentosConsolidados],
            resultado.ConsultadoEm,
            resultado.Avisos);
    })
    .WithName("ListarMovimentacoes")
    .WithSummary("Andamentos de todas as instâncias, do mais recente para o mais antigo. Com desde, a paginação para ao passar da data.");

app.Run();

static IReadOnlyList<string> Instancias(string valor)
{
    var escolhidas = valor.Split(',', StringSplitOptions.TrimEntries).Where(v => v is "1" or "2").Distinct().ToList();
    return escolhidas.Count > 0 ? escolhidas : ["1", "2"];
}

/// <summary>Exposto para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;
