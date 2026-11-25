using Juscraper.Cnj;
using Juscraper.Http;

namespace Juscraper.Scrapers.Esaj;

/// <summary>Grau do e-SAJ e o caminho do serviço de consulta dele (cpopg, cposg, cposg5…).</summary>
public sealed record InstanciaEsaj(string Chave, string Caminho, string Grau)
{
    public bool SegundoGrau => Chave == "2";
}

/// <summary>
/// e-SAJ (Softplan) — TJSP, TJAL, TJCE, TJAC, TJMS, TJAM.
/// <c>search.do</c> com o número: um resultado redireciona para <c>show.do</c>; vários caem numa
/// lista; nenhum responde "Não existem informações disponíveis".
/// </summary>
public sealed class EsajScraper(string tribunal, string baseUrl, string primeiroGrau = "cpopg", string? segundoGrau = "cposg")
    : IScraper
{
    private const int MaxProcessosPorNumero = 10;

    private static readonly Dictionary<string, string> CabecalhosAjax = new() { ["X-Requested-With"] = "XMLHttpRequest" };

    private readonly string _base = baseUrl.TrimEnd('/');

    public string Sistema => EsajParser.Sistema;

    public IReadOnlyDictionary<string, InstanciaEsaj> Instancias { get; } = segundoGrau is null
        ? new Dictionary<string, InstanciaEsaj> { ["1"] = new("1", primeiroGrau, "G1") }
        : new Dictionary<string, InstanciaEsaj> { ["1"] = new("1", primeiroGrau, "G1"), ["2"] = new("2", segundoGrau, "G2") };

    public async Task<ResultadoScraper> ConsultarAsync(
        NumeroCnj numero, Sessao sessao, OpcoesConsulta opcoes, CancellationToken cancellationToken)
    {
        var processos = new List<Processo>();
        var avisos = new List<string>();
        var falhas = new List<JuscraperException>();
        var alvos = opcoes.Instancias.Select(c => Instancias.GetValueOrDefault(c)).OfType<InstanciaEsaj>().ToList();

        foreach (var instancia in alvos)
        {
            try
            {
                processos.AddRange(await ConsultarInstanciaAsync(numero, instancia, sessao, opcoes, cancellationToken));
            }
            catch (JuscraperException exception)
            {
                // Uma instância fora do ar não deve esconder o que a outra respondeu.
                falhas.Add(exception);
                avisos.Add($"{instancia.Grau}: {exception.Message}");
            }
        }

        if (alvos.Count > 0 && falhas.Count == alvos.Count)
        {
            throw falhas[0];
        }

        return new ResultadoScraper(processos, avisos);
    }

    private async Task<List<Processo>> ConsultarInstanciaAsync(
        NumeroCnj numero, InstanciaEsaj instancia, Sessao sessao, OpcoesConsulta opcoes, CancellationToken cancellationToken)
    {
        var resposta = await sessao.GetAsync($"{_base}/{instancia.Caminho}/search.do", ParametrosBusca(numero, instancia),
            cancellationToken: cancellationToken);

        switch (EsajParser.Classificar(resposta.Corpo))
        {
            case TipoPaginaEsaj.NaoEncontrado:
                return [];
            case TipoPaginaEsaj.Lista:
                var processos = new List<Processo>();
                foreach (var codigo in EsajParser.ParseLista(resposta.Corpo).Take(MaxProcessosPorNumero))
                {
                    var show = await sessao.GetAsync($"{_base}/{instancia.Caminho}/show.do", [new("processo.codigo", codigo)],
                        cancellationToken: cancellationToken);
                    processos.Add(await CompletarAsync(show, instancia, sessao, opcoes, cancellationToken));
                }

                return processos;
            default:
                return [await CompletarAsync(resposta, instancia, sessao, opcoes, cancellationToken)];
        }
    }

    private async Task<Processo> CompletarAsync(
        RespostaHttp resposta, InstanciaEsaj instancia, Sessao sessao, OpcoesConsulta opcoes, CancellationToken cancellationToken)
    {
        var (processo, codigoAjax) = EsajParser.ParseDetalhe(resposta.Corpo, tribunal, instancia.Grau);
        processo.UrlConsulta = resposta.Url.ToString();

        if (codigoAjax is not null && !processo.EmSegredoDeJustica)
        {
            (processo.Movimentos, processo.MovimentosCompletos) =
                await MovimentacoesAjaxAsync(codigoAjax, instancia, sessao, opcoes, cancellationToken);
        }
        else if (opcoes.MaxMovimentacoes is { } maximo && processo.Movimentos.Count > maximo)
        {
            processo.Movimentos = processo.Movimentos[..maximo];
            processo.MovimentosCompletos = false;
        }

        foreach (var movimento in processo.Movimentos)
        {
            movimento.Instancia = instancia.Grau;
        }

        return processo;
    }

    private async Task<(List<Movimentacao>, bool Completos)> MovimentacoesAjaxAsync(
        string codigo, InstanciaEsaj instancia, Sessao sessao, OpcoesConsulta opcoes, CancellationToken cancellationToken)
    {
        var url = $"{_base}/{instancia.Caminho}/carregarMovimentacoesAjax.do";
        var chave = instancia.SegundoGrau ? "cdProcesso" : "processo.codigo";
        var movimentos = new List<Movimentacao>();
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (var pagina = 0; pagina < opcoes.MaxPaginasMovimentacoes; pagina++)
        {
            List<KeyValuePair<string, string>> query = [new(chave, codigo)];
            if (cursor is not null)
            {
                query.Add(new("cursor", cursor));
            }

            var resposta = await sessao.GetAsync(url, query, CabecalhosAjax, cancellationToken);
            var fragmento = EsajParser.LerFragmento(resposta.Corpo);
            var linhas = EsajParser.ParseLinhasMovimentacao(fragmento);
            movimentos.AddRange(linhas);

            // Passou da janela pedida: o resto é mais antigo ("completo" para a janela).
            if (linhas.Count > 0 && opcoes.AntesDaJanela(linhas[^1]))
            {
                return (movimentos, true);
            }

            if (opcoes.MaxMovimentacoes is { } maximo && movimentos.Count >= maximo)
            {
                return (movimentos[..maximo], false);
            }

            cursor = EsajParser.ProximoCursor(fragmento);
            if (cursor is null)
            {
                return (movimentos, true);
            }

            if (!vistos.Add(cursor))
            {
                return (movimentos, false); // o portal repetiu o cursor: defesa contra laço
            }
        }

        return (movimentos, false);
    }

    private static List<KeyValuePair<string, string>> ParametrosBusca(NumeroCnj numero, InstanciaEsaj instancia)
    {
        List<KeyValuePair<string, string>> comuns =
        [
            new("conversationId", ""),
            new("cbPesquisa", "NUMPROC"),
            new("numeroDigitoAnoUnificado", numero.NumeroDigitoAno),
            new("foroNumeroUnificado", numero.Origem),
        ];

        if (instancia.SegundoGrau)
        {
            return
            [
                .. comuns,
                new("paginaConsulta", "0"),
                new("dePesquisaNuUnificado", numero.Formatado),
                new("dePesquisaNuUnificado", "UNIFICADO"),
                new("dePesquisa", ""),
                new("tipoNuProcesso", "UNIFICADO"),
            ];
        }

        return
        [
            .. comuns,
            new("dadosConsulta.valorConsultaNuUnificado", numero.Formatado),
            new("dadosConsulta.valorConsultaNuUnificado", "UNIFICADO"),
            new("dadosConsulta.valorConsulta", ""),
            new("dadosConsulta.tipoNuProcesso", "UNIFICADO"),
        ];
    }
}
