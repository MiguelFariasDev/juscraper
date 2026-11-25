using System.Security.Cryptography;
using System.Text;
using Juscraper.Cnj;
using Juscraper.Http;
using Juscraper.Scrapers;
using Juscraper.Tribunais;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Juscraper.Servicos;

public sealed class OpcoesJuscraper
{
    public const string Secao = "Juscraper";

    /// <summary>Tempo de cache de um resultado (inclui "não encontrado"). Zero desliga.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(30);

    public int MaxPaginasMovimentacoes { get; set; } = 100;

    /// <summary>Tribunais PJe com a nova consulta declarados por configuração (ver <see cref="RegistroTribunais.PjeRestDeclarados"/>).</summary>
    public string? PjeRest { get; set; }

    public OpcoesHttp Http { get; set; } = new();
}

/// <summary>
/// Ponto de entrada: <c>ConsultarAsync("0000248-42.2024.8.26.0319")</c>. O tribunal NÃO é
/// parâmetro: está no próprio número (campo J.TR); aceitá-lo de fora abriria espaço para
/// divergir do número e devolver "não encontrado" sem causa aparente.
/// </summary>
public sealed class ConsultaProcessual(
    RegistroTribunais registro,
    FabricaDeSessoes fabrica,
    HybridCache cache,
    IOptions<OpcoesJuscraper> opcoes,
    TimeProvider relogio)
{
    public RegistroTribunais Registro => registro;

    public async Task<ResultadoConsulta> ConsultarAsync(
        string numero,
        IReadOnlyList<string>? instancias = null,
        int? maxMovimentacoes = null,
        DateOnly? desde = null,
        bool usarCache = true,
        CancellationToken cancellationToken = default)
    {
        var cnj = NumeroCnj.Parse(numero);
        var tribunal = registro.Obter(cnj.Tribunal);
        var scraper = tribunal.CriarScraper();
        var consulta = new OpcoesConsulta(
            instancias is { Count: > 0 } ? instancias : OpcoesConsulta.TodasAsInstancias,
            opcoes.Value.MaxPaginasMovimentacoes, maxMovimentacoes, desde);

        if (!usarCache || opcoes.Value.CacheTtl <= TimeSpan.Zero)
        {
            return await ExecutarAsync(cnj, tribunal, scraper, consulta, cancellationToken);
        }

        // HybridCache protege contra estouro de demanda: duas requisições simultâneas do mesmo
        // número viram UMA ida ao portal. Resultado parcial (uma instância falhou) não pode
        // entrar no cache — congelaria por todo o TTL uma falha provavelmente transitória —,
        // por isso sai da fábrica como exceção e volta ao chamador sem ser guardado.
        try
        {
            return await cache.GetOrCreateAsync(
                $"juscraper:v1:{cnj.Digitos}:{consulta.Chave}",
                async token =>
                {
                    var resultado = await ExecutarAsync(cnj, tribunal, scraper, consulta, token);
                    return resultado.Avisos.Count == 0 ? resultado : throw new ResultadoParcial(resultado);
                },
                new HybridCacheEntryOptions { Expiration = opcoes.Value.CacheTtl, LocalCacheExpiration = opcoes.Value.CacheTtl },
                cancellationToken: cancellationToken);
        }
        catch (ResultadoParcial parcial)
        {
            return parcial.Resultado;
        }
    }

    private async Task<ResultadoConsulta> ExecutarAsync(
        NumeroCnj cnj, Tribunal tribunal, IScraper scraper, OpcoesConsulta consulta, CancellationToken cancellationToken)
    {
        using var sessao = fabrica.Criar(tribunal.Sigla);
        var resultado = await scraper.ConsultarAsync(cnj, sessao, consulta, cancellationToken);

        foreach (var processo in resultado.Processos)
        {
            processo.Tribunal = tribunal.Sigla;
            // Sem data vai para o fim; OrderBy é estável, preserva a ordem do portal.
            processo.Movimentos = [.. processo.Movimentos.OrderBy(m => m.DataHora is null)];
            IdsMovimentacao.Atribuir(cnj.Digitos, processo.Movimentos);
            if (consulta.Desde is not null)
            {
                processo.Movimentos.RemoveAll(consulta.AntesDaJanela);
            }
        }

        return new ResultadoConsulta
        {
            NumeroProcesso = cnj.Formatado,
            Tribunal = tribunal.Sigla,
            Encontrado = resultado.Processos.Count > 0,
            Processos = resultado.Processos,
            ConsultadoEm = relogio.GetUtcNow(),
            Avisos = resultado.Avisos,
        };
    }

    private sealed class ResultadoParcial(ResultadoConsulta resultado) : Exception("Resultado parcial: não vai para o cache.")
    {
        public ResultadoConsulta Resultado { get; } = resultado;
    }
}

/// <summary>
/// Id estável entre consultas: SHA-256 de (processo, instância, data/hora, nome, complemento)
/// mais um ORDINAL. O e-SAJ só informa a data, e dois andamentos idênticos no mesmo dia são
/// legítimos — sem o ordinal, o segundo seria descartado como duplicata e nunca notificado.
///
/// O ordinal conta do mais ANTIGO para o mais recente: andamentos novos entram no topo, então os
/// existentes não mudam de número. Vale quando o dia inteiro foi lido — o que acontece com
/// <c>desde</c> (a paginação só para depois de passar do dia) e com listas completas.
/// Mesmo algoritmo da versão Python: ids antigos continuam válidos.
/// </summary>
public static class IdsMovimentacao
{
    public static void Atribuir(string digitos, IReadOnlyList<Movimentacao> movimentos)
    {
        var vistos = new Dictionary<(string, string, string, string), int>();
        for (var i = movimentos.Count - 1; i >= 0; i--)
        {
            var movimento = movimentos[i];
            var chave = (movimento.Instancia ?? "", movimento.DataHora?.ComoIso() ?? "", movimento.Nome, movimento.Complemento ?? "");
            var ordinal = vistos[chave] = vistos.GetValueOrDefault(chave) + 1;
            var bruto = string.Join('\u001f', digitos, chave.Item1, chave.Item2, chave.Item3, chave.Item4, ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            movimento.Id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bruto)))[..32];
        }
    }
}
