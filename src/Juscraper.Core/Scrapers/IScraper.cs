using Juscraper.Cnj;
using Juscraper.Http;

namespace Juscraper.Scrapers;

/// <param name="Instancias">"1" (cpopg) e/ou "2" (cposg) — só o e-SAJ separa as instâncias.</param>
/// <param name="MaxPaginasMovimentacoes">Teto de páginas por processo (cada página é uma requisição).</param>
/// <param name="MaxMovimentacoes">Para de paginar ao atingir este número (null = todas).</param>
/// <param name="Desde">
/// Só movimentações nesta data ou depois. A paginação PARA ao passar da data (os portais
/// devolvem do mais recente para o mais antigo): é o que torna barato o acompanhamento diário.
/// </param>
public sealed record OpcoesConsulta(
    IReadOnlyList<string> Instancias,
    int MaxPaginasMovimentacoes = 100,
    int? MaxMovimentacoes = null,
    DateOnly? Desde = null)
{
    public static readonly IReadOnlyList<string> TodasAsInstancias = ["1", "2"];

    public static OpcoesConsulta Padrao { get; } = new(TodasAsInstancias);

    /// <summary>Chave estável para o cache (a lista não tem igualdade estrutural).</summary>
    public string Chave => $"{string.Join(',', Instancias)}|{MaxPaginasMovimentacoes}|{MaxMovimentacoes}|{Desde:yyyy-MM-dd}";

    public bool AntesDaJanela(Movimentacao movimento) =>
        Desde is { } desde && movimento.DataHora is { } quando && DateOnly.FromDateTime(quando.DateTime) < desde;
}

public sealed record ResultadoScraper(List<Processo> Processos, List<string> Avisos);

/// <summary>Um scraper por sistema processual. Lista vazia = não encontrado (desfecho normal, não erro).</summary>
public interface IScraper
{
    string Sistema { get; }

    Task<ResultadoScraper> ConsultarAsync(NumeroCnj numero, Sessao sessao, OpcoesConsulta opcoes, CancellationToken cancellationToken);
}
