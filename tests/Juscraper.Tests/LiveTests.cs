using Juscraper.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Juscraper.Tests;

/// <summary>
/// Contra os portais REAIS. Explícitos: não rodam por padrão nem em CI (rede brasileira, carga
/// nos tribunais). Rode com <c>dotnet test -- --explicit only</c>. Se falharem e os de cassete
/// passarem, o portal mudou: regrave com <c>juscraper gravar-cassete</c> e ajuste o parser.
/// </summary>
public sealed class LiveTests
{
    [Theory(Explicit = true)]
    [InlineData("0000248-42.2024.8.26.0319", "TJSP")]
    [InlineData("0700482-85.2026.8.02.0349", "TJAL")]
    [InlineData("0002221-74.2006.8.07.0001", "TJDFT")]
    public async Task Portal_responde_com_movimentacoes(string numero, string tribunal)
    {
        var services = new ServiceCollection();
        services.AddJuscraper();
        services.Configure<OpcoesJuscraper>(o => o.CacheTtl = TimeSpan.Zero);
        var servico = services.BuildServiceProvider().GetRequiredService<ConsultaProcessual>();

        var resultado = await servico.ConsultarAsync(numero, maxMovimentacoes: 10, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(tribunal, resultado.Tribunal);
        Assert.True(resultado.Encontrado);
        Assert.NotEmpty(resultado.Processos[0].Movimentos);
    }
}
