using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juscraper.Cnj;
using Juscraper.Http;
using Juscraper.Servicos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Juscraper.Tests;

public sealed class ApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient Cliente(string? apiKeys = null, params string[] cassetes)
    {
        var fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseSetting("Juscraper:Api:ApiKeys", apiKeys ?? string.Empty);
            web.ConfigureTestServices(services =>
            {
                services.AddHttpClient(FabricaDeSessoes.NomeClienteHttp).ConfigurePrimaryHttpMessageHandler(() => new Reprodutor(cassetes));
                services.Configure<OpcoesJuscraper>(o => o.Http = Montagem.HttpRapido());
            });
        });
        return fabrica.CreateClient();
    }

    [Fact]
    public async Task Consulta_no_formato_do_iustitia()
    {
        using var http = Cliente(cassetes: "tjdft_pje");

        using var json = JsonDocument.Parse(await http.GetStringAsync("/v1/processos/0002221-74.2006.8.07.0001", Ct));

        var raiz = json.RootElement;
        Assert.True(raiz.GetProperty("encontrado").GetBoolean());
        var processo = raiz.GetProperty("processos")[0];
        // Mesmos nomes do ProcessoTribunal/MovimentoProcesso do Iustitia, em camelCase.
        foreach (var campo in new[] { "numeroProcesso", "tribunal", "grau", "dataAjuizamento", "emSegredoDeJustica", "classe", "sistema", "orgaoJulgador", "assuntos", "movimentos" })
        {
            Assert.True(processo.TryGetProperty(campo, out _), campo);
        }

        var movimento = processo.GetProperty("movimentos")[0];
        Assert.Equal("2026-11-19T17:45:11-03:00", movimento.GetProperty("dataHora").GetString());
        Assert.Equal(32, movimento.GetProperty("id").GetString()!.Length);
        Assert.Equal("ATIVO", processo.GetProperty("partes")[0].GetProperty("polo").GetString());
    }

    [Fact]
    public async Task Nao_encontrado_e_200()
    {
        using var http = Cliente(cassetes: "tjsp_nao_encontrado");

        var resposta = await http.GetFromJsonAsync<JsonElement>("/v1/processos/99999991820248260319", Ct);

        Assert.False(resposta.GetProperty("encontrado").GetBoolean());
    }

    [Fact]
    public async Task Movimentacoes_incrementais()
    {
        using var http = Cliente(cassetes: "tjsp_1e2grau");

        var corpo = await http.GetFromJsonAsync<JsonElement>("/v1/processos/1003603-09.2025.8.26.0319/movimentacoes?desde=2026-11-01", Ct);

        var movimentos = corpo.GetProperty("movimentos").EnumerateArray().ToList();
        Assert.NotEmpty(movimentos); // há andamentos em novembro/2026 no 2º grau
        Assert.All(movimentos, m => Assert.True(string.CompareOrdinal(m.GetProperty("dataHora").GetString(), "2026-11-01") >= 0));
        Assert.True(corpo.GetProperty("completas").GetBoolean());
    }

    [Theory]
    [InlineData("123", HttpStatusCode.BadRequest, "NUMERO_CNJ_INVALIDO", false)]
    [InlineData("TJRJ", (HttpStatusCode)422, "TRIBUNAL_NAO_SUPORTADO", false)]
    [InlineData("TJSC", HttpStatusCode.ServiceUnavailable, "ACESSO_BLOQUEADO", false)]
    [InlineData("TJMG", HttpStatusCode.ServiceUnavailable, "CAPTCHA_EXIGIDO", false)]
    public async Task Erros_em_problem_details(string alvo, HttpStatusCode status, string codigo, bool transitorio)
    {
        var numero = alvo switch
        {
            "TJRJ" => $"0001234-{NumeroCnj.CalcularDv("0001234", "2024", 8, "19", "0001")}.2024.8.19.0001",
            "TJSC" => $"5000001-{NumeroCnj.CalcularDv("5000001", "2024", 8, "24", "0023")}.2024.8.24.0023",
            "TJMG" => $"5000001-{NumeroCnj.CalcularDv("5000001", "2024", 8, "13", "0024")}.2024.8.13.0024",
            _ => alvo,
        };
        using var http = Cliente();

        var resposta = await http.GetAsync($"/v1/processos/{numero}", Ct);

        Assert.Equal(status, resposta.StatusCode);
        Assert.Equal("application/problem+json", resposta.Content.Headers.ContentType?.MediaType);
        var corpo = await resposta.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(codigo, corpo.GetProperty("codigo").GetString());
        Assert.Equal(transitorio, corpo.GetProperty("transitorio").GetBoolean());
    }

    [Fact]
    public async Task Exige_api_key_quando_configurada()
    {
        using var http = Cliente("segredo-1,segredo-2", "tjsp_nao_encontrado");

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/v1/tribunais", Ct)).StatusCode);

        using var errada = new HttpRequestMessage(HttpMethod.Get, "/v1/tribunais") { Headers = { { "X-Api-Key", "errada" } } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(errada, Ct)).StatusCode);

        using var certa = new HttpRequestMessage(HttpMethod.Get, "/v1/tribunais") { Headers = { { "X-Api-Key", "segredo-2" } } };
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(certa, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health", Ct)).StatusCode); // aberto para o orquestrador
    }

    [Fact]
    public async Task Publica_o_documento_openapi()
    {
        using var http = Cliente();

        var documento = await http.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Ct);

        Assert.True(documento.GetProperty("paths").TryGetProperty("/v1/processos/{numero}/movimentacoes", out _));
    }
}
