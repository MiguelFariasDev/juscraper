using System.Text.Json;
using System.Text.RegularExpressions;
using Juscraper.Cnj;
using Juscraper.Http;

namespace Juscraper.Scrapers.Pje;

/// <summary>
/// Nova Consulta Pública do PJe (front-end Angular + API REST, sem captcha), descoberta lendo
/// <c>assets/env.js</c> e os bundles do próprio front-end:
/// <code>
/// GET {api}/v1/processos?numeroProcesso=…&amp;page=0      → lista (idProcesso opaco, muda a cada busca)
/// GET {api}/v1/processos/{id}/dados
/// GET {api}/v1/processos/{id}/poloAtivo|poloPassivo|outrosInteressados?page=N
/// GET {api}/v1/processos/{id}/movimentacoes?page=N     → 15 por página, mais recente primeiro
/// </code>
/// Envelope <c>{status, code, messages, result, pageInfo:{current,last,size,count}}</c>;
/// <c>page</c> é 0-based, <c>pageInfo.current</c> é 1-based; não encontrado = <c>result: []</c>.
/// </summary>
public sealed partial class PjeConsultaPublicaScraper(string tribunal, string apiUrl, string grau = "G1", string? urlPortal = null)
    : IScraper
{
    public const string NomeSistema = "PJe";

    private const int MaxPaginasPartes = 10;

    private const int MaxProcessosPorNumero = 5;

    private const int MovimentosPorPagina = 15;

    private static readonly HashSet<string> TiposAdvogado =
        new(["ADVOGADO", "ADVOGADA", "PROCURADOR", "DEFENSOR PÚBLICO", "DEFENSOR"], StringComparer.OrdinalIgnoreCase);

    public string Sistema => NomeSistema;

    public string ApiUrl { get; } = apiUrl.TrimEnd('/');

    public async Task<ResultadoScraper> ConsultarAsync(
        NumeroCnj numero, Sessao sessao, OpcoesConsulta opcoes, CancellationToken cancellationToken)
    {
        using var busca = await GetAsync(sessao, "/v1/processos", [new("numeroProcesso", numero.Formatado), new("page", "0")], cancellationToken);
        var raiz = busca.RootElement;
        var avisos = raiz.TryGetProperty("messages", out var mensagens) && mensagens.ValueKind == JsonValueKind.Array
            ? mensagens.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!).ToList()
            : [];

        var processos = new List<Processo>();
        foreach (var item in Itens(raiz).Take(MaxProcessosPorNumero))
        {
            processos.Add(await DetalharAsync(item, numero, sessao, opcoes, cancellationToken));
        }

        return new ResultadoScraper(processos, avisos);
    }

    private async Task<Processo> DetalharAsync(
        JsonElement item, NumeroCnj numero, Sessao sessao, OpcoesConsulta opcoes, CancellationToken cancellationToken)
    {
        var id = Str(item, "idProcesso") ?? throw new RespostaInesperadaException("PJe: item de busca sem idProcesso.", tribunal);
        var caminho = $"/v1/processos/{Uri.EscapeDataString(id)}";

        using var dados = await GetAsync(sessao, $"{caminho}/dados", null, cancellationToken);
        var processo = ParseDados(dados.RootElement.TryGetProperty("result", out var r) ? r : default, item, tribunal, grau);
        processo.NumeroProcesso = processo.NumeroProcesso.Limpo ?? numero.Formatado;

        foreach (var (recurso, polo) in new[] { ("poloAtivo", Polo.Ativo), ("poloPassivo", Polo.Passivo), ("outrosInteressados", Polo.Outros) })
        {
            var (registros, _) = await PaginarAsync(sessao, $"{caminho}/{recurso}", MaxPaginasPartes, null, cancellationToken);
            processo.Partes.AddRange(ParsePartes(registros, polo));
        }

        var limite = opcoes.MaxMovimentacoes is { } maximo
            ? Math.Min(opcoes.MaxPaginasMovimentacoes, (maximo + MovimentosPorPagina - 1) / MovimentosPorPagina)
            : opcoes.MaxPaginasMovimentacoes;
        Func<JsonElement, bool>? parar = opcoes.Desde is { } desde
            ? registro => Str(registro, "dataAtualizacao").ComoDataHoraBr is { } quando && DateOnly.FromDateTime(quando.DateTime) < desde
            : null;

        var (movs, completos) = await PaginarAsync(sessao, $"{caminho}/movimentacoes", limite, parar, cancellationToken);
        var movimentos = ParseMovimentacoes(movs);
        if (opcoes.MaxMovimentacoes is { } teto && movimentos.Count > teto)
        {
            (movimentos, completos) = (movimentos[..teto], false);
        }

        movimentos.ForEach(m => m.Instancia = grau);
        processo.Movimentos = movimentos;
        processo.MovimentosCompletos = completos;
        processo.UrlConsulta = urlPortal;
        return processo;
    }

    private async Task<JsonDocument> GetAsync(
        Sessao sessao, string caminho, List<KeyValuePair<string, string>>? query, CancellationToken cancellationToken)
    {
        var documento = await sessao.GetJsonAsync($"{ApiUrl}{caminho}", query, cancellationToken);
        var raiz = documento.RootElement;
        if (raiz.ValueKind != JsonValueKind.Object || (Str(raiz, "status") is { } status && status != "ok"))
        {
            documento.Dispose();
            throw new RespostaInesperadaException($"PJe respondeu fora do envelope esperado em {caminho}.", tribunal);
        }

        return documento;
    }

    private async Task<(List<JsonElement> Registros, bool Completos)> PaginarAsync(
        Sessao sessao, string caminho, int maxPaginas, Func<JsonElement, bool>? parar, CancellationToken cancellationToken)
    {
        var registros = new List<JsonElement>();
        for (var pagina = 0; pagina < maxPaginas;)
        {
            using var documento = await GetAsync(sessao, caminho, [new("page", pagina.ToString(System.Globalization.CultureInfo.InvariantCulture))], cancellationToken);
            // Clone: o JsonDocument é descartado ao fim da iteração.
            var novos = Itens(documento.RootElement).Select(e => e.Clone()).ToList();
            registros.AddRange(novos);
            if (parar is not null && novos.Count > 0 && parar(novos[^1]))
            {
                return (registros, true);
            }

            var ultima = documento.RootElement.TryGetProperty("pageInfo", out var info) && info.TryGetProperty("last", out var last)
                && last.TryGetInt32(out var n) ? n : 0;
            pagina++;
            if (pagina >= ultima)
            {
                return (registros, true);
            }
        }

        return (registros, false);
    }

    // -- parsers (puros) ------------------------------------------------------------------

    public static Processo ParseDados(JsonElement dados, JsonElement item, string tribunal, string grau)
    {
        var assuntos = Assuntos(Str(dados, "assunto"));
        if (assuntos.Count == 0 && Str(item, "assunto").Limpo is { } assunto)
        {
            assuntos = [assunto];
        }

        DateOnly? distribuicao = DateTime.TryParse(Str(dados, "dataDistribuicao"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var data) ? DateOnly.FromDateTime(data) : null;

        return new Processo
        {
            NumeroProcesso = Str(dados, "numeroProcesso").Limpo ?? Str(item, "numeroProcesso").Limpo ?? string.Empty,
            Tribunal = tribunal,
            Grau = grau,
            Sistema = NomeSistema,
            DataAjuizamento = distribuicao,
            Classe = SemCodigo(Str(dados, "classeJudicial")) ?? Str(item, "classe").Limpo,
            OrgaoJulgador = Str(dados, "orgaoJulgadorColegiado").Limpo ?? Str(dados, "orgaoJulgador").Limpo,
            Assuntos = assuntos,
            Foro = Str(dados, "jurisdicao").Limpo,
            EmSegredoDeJustica = Sigiloso(item),
        };
    }

    /// <summary>
    /// O PJe lista advogados como participantes do mesmo polo, logo após a parte que representam;
    /// aqui eles são agregados à parte anterior. O campo <c>participante</c> traz CPF/CNPJ por
    /// extenso e é deliberadamente descartado (minimização, LGPD).
    /// </summary>
    public static List<Parte> ParsePartes(IEnumerable<JsonElement> registros, Polo polo)
    {
        var partes = new List<Parte>();
        foreach (var registro in registros)
        {
            if (Str(registro, "nome").Limpo is not { } nome)
            {
                continue;
            }

            var tipo = Str(registro, "tipo").Limpo ?? string.Empty;
            if (TiposAdvogado.Contains(tipo))
            {
                var oab = Oab().Match(Str(registro, "participante") ?? string.Empty);
                partes.LastOrDefault()?.Advogados.Add(new Advogado(nome, oab.Success ? oab.Groups[1].Value.Replace(" ", "", StringComparison.Ordinal) : null));
                continue;
            }

            partes.Add(new Parte { Nome = nome, Tipo = Titulo(tipo), Polo = polo });
        }

        return partes;
    }

    public static List<Movimentacao> ParseMovimentacoes(IEnumerable<JsonElement> registros) =>
    [
        // Movimento excluído pelo cartório: o PJe mantém a linha riscada, mas não é andamento
        // válido e não deve disparar alerta no Iustitia.
        .. from registro in registros
           where !(registro.TryGetProperty("movimentoExcluido", out var excluido) && excluido.ValueKind == JsonValueKind.True)
           let nome = Str(registro, "movimento").Limpo
           where nome is not null
           select new Movimentacao
           {
               Nome = nome,
               DataHora = Str(registro, "dataAtualizacao").ComoDataHoraBr,
               Complemento = Str(registro, "documento").Limpo,
               PossuiDocumento = Str(registro, "idDocumento") is { Length: > 0 },
           },
    ];

    /// <summary>"DIREITO CIVIL (899) - Obrigações (7681) - Espécies (7717)" é o caminho na árvore; o assunto é a folha.</summary>
    public static List<string> Assuntos(string? texto)
    {
        if (texto.Limpo is not { } limpo)
        {
            return [];
        }

        var niveis = SeparadorAssunto().Split(limpo).Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        return niveis.Count > 0 ? [niveis[^1]] : [];
    }

    private static IEnumerable<JsonElement> Itens(JsonElement raiz) =>
        raiz.TryGetProperty("result", out var resultado) && resultado.ValueKind == JsonValueKind.Array
            ? resultado.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string? Str(JsonElement elemento, string nome) =>
        elemento.ValueKind == JsonValueKind.Object && elemento.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()
            : null;

    private static string? SemCodigo(string? texto) => texto.Limpo is { } limpo ? CodigoTabela().Replace(limpo, "") : null;

    private static bool Sigiloso(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("partes", out var partes) && partes.ValueKind == JsonValueKind.Object
        && partes.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object
                                             && p.Value.TryGetProperty("sigiloso", out var s) && s.ValueKind == JsonValueKind.True);

    private static string Titulo(string tipo) =>
        System.Globalization.CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase(tipo.ToLowerInvariant());

    [GeneratedRegex(@"\s*\(\d+\)\s*$")]
    private static partial Regex CodigoTabela();

    [GeneratedRegex(@"\s\(\d+\)(?:\s-\s|$)")]
    private static partial Regex SeparadorAssunto();

    [GeneratedRegex(@"OAB\s+([A-Z]{2}\s?\d+[A-Z]?)")]
    private static partial Regex Oab();
}
