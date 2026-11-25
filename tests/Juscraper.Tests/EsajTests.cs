using Juscraper.Scrapers.Esaj;

namespace Juscraper.Tests;

public sealed class EsajTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tjsp_1grau_segue_o_cursor_ate_a_ultima_pagina()
    {
        var (servico, rede) = Montagem.ComCassetes("tjsp_cpopg");

        var resultado = await servico.ConsultarAsync("0000248-42.2024.8.26.0319", ["1"], cancellationToken: Ct);

        Assert.True(resultado.Encontrado);
        var processo = Assert.Single(resultado.Processos);
        Assert.Equal(("TJSP", "G1", "e-SAJ"), (processo.Tribunal, processo.Grau, processo.Sistema));
        Assert.Equal("Procedimento Comum Cível", processo.Classe);
        Assert.Equal("2ª Vara", processo.OrgaoJulgador);
        Assert.Equal(new DateOnly(2024, 2, 9), processo.DataAjuizamento);
        Assert.Equal("Extinto", processo.Situacao);

        // 11 páginas de 5 (a última com 2): o cursor foi seguido até o fim.
        Assert.Equal(52, processo.Movimentos.Count);
        Assert.True(processo.MovimentosCompletos);
        Assert.Equal(11, rede.Pedidos.Count(p => p.Contains("carregarMovimentacoesAjax", StringComparison.Ordinal)));

        var datas = processo.Movimentos.Select(m => m.DataHora).ToList();
        Assert.Equal(datas.OrderDescending(), datas);
        Assert.Equal("Arquivado Definitivamente", processo.Movimentos[0].Nome);
        Assert.Equal(new DateOnly(2024, 2, 9), DateOnly.FromDateTime(processo.Movimentos[^1].DataHora!.Value.DateTime));
        Assert.All(processo.Movimentos, m => Assert.Equal("G1", m.Instancia));
        Assert.Contains(processo.Movimentos, m => m.Complemento is not null);

        Assert.Equal([Polo.Ativo, Polo.Passivo], processo.Partes.Select(p => p.Polo!.Value).Distinct().Order());
        Assert.All(processo.Partes, p => Assert.StartsWith("PARTE", p.Nome, StringComparison.Ordinal)); // pseudonimizado
    }

    [Fact]
    public async Task Tjsp_devolve_1o_e_2o_grau()
    {
        var (servico, _) = Montagem.ComCassetes("tjsp_1e2grau");

        var resultado = await servico.ConsultarAsync("1003603-09.2025.8.26.0319", cancellationToken: Ct);

        var graus = resultado.Processos.ToDictionary(p => p.Grau!);
        Assert.Equal(["G1", "G2"], graus.Keys.Order());

        var segundo = graus["G2"];
        Assert.Equal("Apelação Cível", segundo.Classe);
        Assert.Equal("18ª Câmara de Direito Público", segundo.OrgaoJulgador);
        Assert.Equal("Julgado", segundo.Situacao);
        Assert.StartsWith("MAGISTRADO", segundo.Juiz, StringComparison.Ordinal);
        Assert.True(segundo.MovimentosCompletos);
        Assert.True(segundo.Movimentos.Count > 5);
        Assert.All(segundo.Movimentos, m => Assert.Equal("G2", m.Instancia));
        Assert.Contains(segundo.Partes.SelectMany(p => p.Advogados), a => a.Oab is not null); // OAB em input oculto

        var consolidados = resultado.MovimentosConsolidados.ToList();
        Assert.Equal(graus["G1"].Movimentos.Count + segundo.Movimentos.Count, consolidados.Count);
        Assert.Equal("G2", consolidados[0].Instancia); // o andamento mais recente está no tribunal
    }

    [Fact]
    public async Task Tjal_layout_inline_e_2o_grau_inexistente()
    {
        var (servico, rede) = Montagem.ComCassetes("tjal");

        var resultado = await servico.ConsultarAsync("0700482-85.2026.8.02.0349", cancellationToken: Ct);

        var processo = Assert.Single(resultado.Processos);
        Assert.Equal(19, processo.Movimentos.Count);
        Assert.DoesNotContain(rede.Pedidos, p => p.Contains("carregarMovimentacoesAjax", StringComparison.Ordinal));
        Assert.Equal("R$ 591,34", processo.ValorDaCausa);
        Assert.Empty(resultado.Avisos); // 2º grau sem o processo é "não encontrado", não falha
        Assert.Contains(rede.Pedidos, p => p.Contains("/cposg5/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nao_encontrado_e_sucesso()
    {
        var (servico, _) = Montagem.ComCassetes("tjsp_nao_encontrado");

        var resultado = await servico.ConsultarAsync("9999999-18.2024.8.26.0319", cancellationToken: Ct);

        Assert.False(resultado.Encontrado);
        Assert.Empty(resultado.Processos);
        Assert.Equal("TJSP", resultado.Tribunal);
    }

    [Fact]
    public async Task Limite_de_movimentacoes_para_de_paginar()
    {
        var (servico, rede) = Montagem.ComCassetes("tjsp_cpopg");

        var resultado = await servico.ConsultarAsync("0000248-42.2024.8.26.0319", ["1"], maxMovimentacoes: 7, cancellationToken: Ct);

        var processo = Assert.Single(resultado.Processos);
        Assert.Equal(7, processo.Movimentos.Count);
        Assert.False(processo.MovimentosCompletos);
        Assert.Equal(2, rede.Pedidos.Count(p => p.Contains("carregarMovimentacoesAjax", StringComparison.Ordinal)));
    }

    [Fact]
    public void Segredo_de_justica()
    {
        const string html = """
            <script>$.saj.acessoRecurso = { popupSenha: {
                mostrar: 'true' == 'true', titulo: 'Senha do processo' } };</script>
            <span id="numeroProcesso"></span>
            """;

        Assert.Equal(TipoPaginaEsaj.Segredo, EsajParser.Classificar(html));
        var pagina = EsajParser.ParseDetalhe(html, "TJSP", "G1");
        Assert.True(pagina.Processo.EmSegredoDeJustica);
        Assert.Empty(pagina.Processo.Movimentos);
    }

    [Fact]
    public void Pagina_desconhecida_vira_resposta_inesperada() =>
        Assert.Throws<RespostaInesperadaException>(() =>
            EsajParser.Classificar("<html><title>Portal de Serviços | E-SAJ</title><body></body></html>"));
}
