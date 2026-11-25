namespace Advocacia.Modules.Legal.Infrastructure.Integrations.Juscraper;

// Espelho do JSON de GET /v1/processos/{numero} do juscraper (camelCase, JsonSerializerDefaults.Web).
// Só o que o adaptador usa; campos extras da resposta (partes, valor da causa…) são ignorados
// pelo desserializador e podem ser adicionados aqui quando a tela precisar deles.

internal sealed record JuscraperResultado(
    string NumeroProcesso,
    string? Tribunal,
    bool Encontrado,
    IReadOnlyList<JuscraperProcesso> Processos,
    DateTimeOffset ConsultadoEm,
    IReadOnlyList<string> Avisos);

internal sealed record JuscraperProcesso(
    string NumeroProcesso,
    string Tribunal,
    string? Grau,
    string Sistema,
    DateOnly? DataAjuizamento,
    bool EmSegredoDeJustica,
    string? Classe,
    string? OrgaoJulgador,
    IReadOnlyList<string> Assuntos,
    IReadOnlyList<JuscraperMovimento> Movimentos,
    int? CodigoMunicipioIbge,
    bool MovimentosCompletos);

internal sealed record JuscraperMovimento(
    string? Id,
    string Nome,
    DateTimeOffset? DataHora,
    string? Complemento,
    bool PossuiDocumento,
    string? Instancia);

/// <summary>ProblemDetails (RFC 7807) com as extensões do juscraper.</summary>
internal sealed record JuscraperProblema(
    string? Title,
    int? Status,
    string? Detail,
    string? Codigo,
    string? Tribunal,
    bool Transitorio);
