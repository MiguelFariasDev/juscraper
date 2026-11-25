using System.Text.Json;
using Juscraper.Scrapers.Pje;

namespace Juscraper.Tests;

public sealed class PjeTests
{
    [Fact]
    public async Task Tjdft_consulta_publica_rest()
    {
        var (servico, _) = Montagem.ComCassetes("tjdft_pje");

        var resultado = await servico.ConsultarAsync("0002221-74.2006.8.07.0001", cancellationToken: TestContext.Current.CancellationToken);

        var processo = Assert.Single(resultado.Processos);
        Assert.Equal(("TJDFT", "PJe"), (processo.Tribunal, processo.Sistema));
        Assert.Equal("CUMPRIMENTO DE SENTENÇA", processo.Classe); // sem o "(156)" da tabela
        Assert.Equal(["Espécies de Títulos de Crédito"], processo.Assuntos); // folha da árvore
        Assert.Equal("1ª Vara da Fazenda Pública do DF", processo.OrgaoJulgador);
        Assert.Equal(new DateOnly(2018, 12, 12), processo.DataAjuizamento);

        Assert.Equal(170, processo.Movimentos.Count);
        Assert.True(processo.MovimentosCompletos);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 17, 45, 11, TimeSpan.FromHours(-3)), processo.Movimentos[0].DataHora);
        Assert.Equal("Recebidos os autos", processo.Movimentos[0].Nome);

        var ativos = processo.Partes.Where(p => p.Polo == Polo.Ativo).ToList();
        Assert.Single(ativos);
        Assert.Single(ativos[0].Advogados);
        Assert.Equal(2, processo.Partes.Count(p => p.Polo == Polo.Passivo)); // advogado não vira parte

        var saida = JsonSerializer.Serialize(resultado, JsonSerializerOptions.Web);
        Assert.DoesNotContain("CPF", saida, StringComparison.Ordinal); // documento pessoal é descartado
        Assert.DoesNotContain("CNPJ", saida, StringComparison.Ordinal);
    }

    [Fact]
    public void Advogado_e_agregado_a_parte_anterior()
    {
        using var json = JsonDocument.Parse("""
            [
              {"nome": "EMPRESA X", "tipo": "REU", "participante": "EMPRESA X - CNPJ: 1 (REU)"},
              {"nome": "FULANO", "tipo": "ADVOGADO", "participante": "FULANO - OAB DF12345 - CPF: 2 (ADVOGADO)"},
              {"nome": "BELTRANO", "tipo": "REU", "participante": "BELTRANO (REU)"}
            ]
            """);

        var partes = PjeConsultaPublicaScraper.ParsePartes(json.RootElement.EnumerateArray(), Polo.Passivo);

        Assert.Equal(["EMPRESA X", "BELTRANO"], partes.Select(p => p.Nome));
        Assert.Equal("DF12345", partes[0].Advogados[0].Oab);
    }

    [Fact]
    public void Movimento_excluido_e_ignorado()
    {
        using var json = JsonDocument.Parse("""
            [
              {"movimento": "Juntada", "dataAtualizacao": "01/02/2026 10:00:00", "movimentoExcluido": true},
              {"movimento": "Conclusos", "dataAtualizacao": "02/02/2026 11:30:00", "idDocumento": "abc"}
            ]
            """);

        var movimentos = PjeConsultaPublicaScraper.ParseMovimentacoes(json.RootElement.EnumerateArray());

        var conclusos = Assert.Single(movimentos);
        Assert.Equal("Conclusos", conclusos.Nome);
        Assert.True(conclusos.PossuiDocumento);
    }

    [Theory]
    [InlineData("DIREITO CIVIL (899) - Obrigações (7681) - Espécies de Títulos de Crédito (7717)", "Espécies de Títulos de Crédito")]
    [InlineData("Indenização por Dano Moral - Cobrança (123)", "Indenização por Dano Moral - Cobrança")]
    public void Assunto_e_a_folha_da_arvore(string caminho, string folha) =>
        Assert.Equal([folha], PjeConsultaPublicaScraper.Assuntos(caminho));
}
