using Juscraper.Servicos;
using Juscraper.Tribunais;

namespace Juscraper.Tests;

/// <summary>O que o acompanhamento diário do Iustitia depende: id estável e paginação até "desde".</summary>
public sealed class AcompanhamentoTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Movimentacao Mov(string nome, int dia) =>
        new() { Nome = nome, DataHora = new DateTimeOffset(2026, 11, dia, 0, 0, 0, TimeSpan.FromHours(-3)), Instancia = "G1" };

    [Fact]
    public void Id_nao_muda_quando_chegam_andamentos_novos()
    {
        List<Movimentacao> antes = [Mov("Conclusos", 10), Mov("Juntada", 9)];
        IdsMovimentacao.Atribuir(new string('0', 20), antes);

        List<Movimentacao> depois = [Mov("Expedido Certidão", 12), Mov("Conclusos", 10), Mov("Juntada", 9)];
        IdsMovimentacao.Atribuir(new string('0', 20), depois);

        Assert.Equal(antes.Select(m => m.Id), depois.Skip(1).Select(m => m.Id));
        Assert.DoesNotContain(depois[0].Id, antes.Select(m => m.Id));
    }

    [Fact]
    public void Andamentos_identicos_no_mesmo_dia_tem_ids_distintos_e_estaveis()
    {
        List<Movimentacao> antes = [Mov("Expedido Certidão", 10), Mov("Juntada", 9)];
        IdsMovimentacao.Atribuir(new string('1', 20), antes);

        List<Movimentacao> depois = [Mov("Expedido Certidão", 10), Mov("Expedido Certidão", 10), Mov("Juntada", 9)];
        IdsMovimentacao.Atribuir(new string('1', 20), depois);

        Assert.Equal(3, depois.Select(m => m.Id).Distinct().Count());
        Assert.Equal(antes[0].Id, depois[1].Id); // o ordinal conta do mais antigo
    }

    [Fact]
    public void Id_e_compativel_com_a_versao_python()
    {
        // Valor calculado pela implementação Python (juscraper.servico.atribuir_ids): ids já
        // gravados pelo Iustitia continuam válidos depois da migração para C#.
        List<Movimentacao> movimentos = [Mov("Conclusos", 10)];
        IdsMovimentacao.Atribuir(new string('0', 20), movimentos);

        Assert.Equal(PythonIdConclusos, movimentos[0].Id);
    }

    [Fact]
    public async Task Desde_para_a_paginacao_do_esaj()
    {
        var (servico, rede) = Montagem.ComCassetes("tjsp_cpopg");

        var resultado = await servico.ConsultarAsync("0000248-42.2024.8.26.0319", ["1"], desde: new DateOnly(2025, 4, 1), cancellationToken: Ct);

        var processo = Assert.Single(resultado.Processos);
        Assert.NotEmpty(processo.Movimentos);
        Assert.All(processo.Movimentos, m => Assert.True(DateOnly.FromDateTime(m.DataHora!.Value.DateTime) >= new DateOnly(2025, 4, 1)));
        Assert.True(rede.Pedidos.Count(p => p.Contains("carregarMovimentacoesAjax", StringComparison.Ordinal)) < 11, "não leu o histórico todo");
    }

    [Fact]
    public async Task Ids_com_desde_batem_com_a_consulta_completa()
    {
        var (completo, _) = Montagem.ComCassetes("tjsp_cpopg");
        var (janela, _) = Montagem.ComCassetes("tjsp_cpopg");

        var todos = (await completo.ConsultarAsync("0000248-42.2024.8.26.0319", ["1"], cancellationToken: Ct)).Processos[0].Movimentos;
        var recentes = (await janela.ConsultarAsync("0000248-42.2024.8.26.0319", ["1"], desde: new DateOnly(2025, 1, 1), cancellationToken: Ct))
            .Processos[0].Movimentos;

        Assert.NotEmpty(recentes);
        Assert.All(recentes, m => Assert.Contains(m.Id, todos.Select(t => t.Id)));
    }

    [Fact]
    public async Task Desde_para_a_paginacao_do_pje()
    {
        var (servico, rede) = Montagem.ComCassetes("tjdft_pje");

        var resultado = await servico.ConsultarAsync("0002221-74.2006.8.07.0001", desde: new DateOnly(2026, 11, 1), cancellationToken: Ct);

        var processo = Assert.Single(resultado.Processos);
        Assert.NotEmpty(processo.Movimentos);
        Assert.Equal(1, rede.Pedidos.Count(p => p.Contains("/movimentacoes", StringComparison.Ordinal)));
    }

    [Fact]
    public void Pje_rest_por_configuracao()
    {
        var tribunal = Assert.Single(RegistroTribunais.PjeRestDeclarados("tjxx=https://api.tjxx.jus.br|https://consulta.tjxx.jus.br"));

        Assert.Equal("TJXX", tribunal.Sigla);
        Assert.Equal("https://api.tjxx.jus.br", ((Scrapers.Pje.PjeConsultaPublicaScraper)tribunal.CriarScraper()).ApiUrl);
        Assert.Empty(RegistroTribunais.PjeRestDeclarados(""));
        Assert.Throws<FormatException>(() => RegistroTribunais.PjeRestDeclarados("TJXX=http://inseguro"));
    }

    [Fact]
    public async Task Cache_evita_segunda_ida_ao_portal()
    {
        var rede = new Reprodutor("tjsp_nao_encontrado");
        var servico = Montagem.Servico(rede, cacheTtl: TimeSpan.FromMinutes(5));

        await servico.ConsultarAsync("9999999-18.2024.8.26.0319", cancellationToken: Ct);
        var pedidos = rede.Pedidos.Count;
        var segunda = await servico.ConsultarAsync("9999999-18.2024.8.26.0319", cancellationToken: Ct);

        Assert.False(segunda.Encontrado);
        Assert.Equal(pedidos, rede.Pedidos.Count); // "não encontrado" também fica em cache
    }

    private const string PythonIdConclusos = "5f9575e9b0fd5ef60c1c4a29579065f4";
}
