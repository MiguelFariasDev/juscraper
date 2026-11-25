using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Juscraper;
using Juscraper.Cli;
using Juscraper.Http;
using Juscraper.Servicos;
using Microsoft.Extensions.DependencyInjection;

// juscraper consultar 0000248-42.2024.8.26.0319 [--json] [--instancias 1,2] [--max-movimentacoes N] [--desde AAAA-MM-DD]
// juscraper tribunais
// juscraper detectar-pje https://pje-consultapublica.tjdft.jus.br --sigla TJDFT
// juscraper gravar-cassete 0000248-42.2024.8.26.0319 tests/cassettes/tjsp_cpopg.json --instancias 1

var json = new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true };

var numero = new Argument<string>("numero") { Description = "Número CNJ, com ou sem máscara." };
var instancias = new Option<string>("--instancias") { Description = "1 | 2 | 1,2 (e-SAJ).", DefaultValueFactory = _ => "1,2" };
var maxMovimentacoes = new Option<int?>("--max-movimentacoes") { Description = "Para de paginar ao atingir N movimentações." };
var desde = new Option<DateOnly?>("--desde") { Description = "Só movimentações a partir desta data (AAAA-MM-DD)." };
var comoJson = new Option<bool>("--json") { Description = "Saída em JSON, no formato da API." };

var consultar = new Command("consultar", "Consulta um processo pelo número CNJ.") { numero, instancias, maxMovimentacoes, desde, comoJson };
consultar.SetAction(async (resultado, cancellationToken) =>
{
    var servico = Servico(cacheTtl: TimeSpan.Zero);
    try
    {
        var consulta = await servico.ConsultarAsync(
            resultado.GetValue(numero)!, Instancias(resultado.GetValue(instancias)), resultado.GetValue(maxMovimentacoes),
            resultado.GetValue(desde), cancellationToken: cancellationToken);

        if (resultado.GetValue(comoJson))
        {
            Console.WriteLine(JsonSerializer.Serialize(consulta, json));
        }
        else
        {
            Resumo.Imprimir(consulta);
        }

        return 0;
    }
    catch (JuscraperException erro)
    {
        await Console.Error.WriteLineAsync($"[{erro.Codigo}] {erro.Message}");
        return erro.Transitorio ? 2 : 1;
    }
});

var tribunais = new Command("tribunais", "Lista os tribunais e o estado de cada integração.");
tribunais.SetAction(_ =>
{
    foreach (var t in Servico().Registro.Todos)
    {
        Console.WriteLine($"{t.Sigla,-6} {t.Sistema,-6} {t.Status.ToString().ToLowerInvariant(),-13} {t.Observacao}");
    }
});

var portal = new Argument<string>("portal") { Description = "URL base do portal de consulta." };
var sigla = new Option<string?>("--sigla") { Description = "Sigla do tribunal, para montar a linha de configuração." };
var detectar = new Command("detectar-pje", "Verifica se um portal PJe já usa a NOVA consulta pública. Rode de um servidor no Brasil.") { portal, sigla };
detectar.SetAction(async (resultado, cancellationToken) =>
{
    var (tipo, detalhe, api) = await DetectorPje.DetectarAsync(resultado.GetValue(portal)!, cancellationToken);
    Console.WriteLine($"{tipo}: {detalhe}");
    if (api is not null)
    {
        Console.WriteLine();
        Console.WriteLine($"Juscraper__PjeRest=\"{resultado.GetValue(sigla) ?? "TJXX"}={api}|{resultado.GetValue(portal)!.TrimEnd('/')}\"");
    }

    return api is null ? 1 : 0;
});

var destino = new Argument<FileInfo>("destino") { Description = "Arquivo .json do cassete." };
var gravar = new Command("gravar-cassete", "Grava uma consulta real como cassete de teste, com dados pessoais pseudonimizados.")
{
    numero, destino, instancias,
};
gravar.SetAction(async (resultado, cancellationToken) =>
{
    var gravador = new Gravador();
    var servico = Servico(cacheTtl: TimeSpan.Zero, rede: gravador);
    var consulta = await servico.ConsultarAsync(resultado.GetValue(numero)!, Instancias(resultado.GetValue(instancias)),
        cancellationToken: cancellationToken);
    var arquivo = resultado.GetValue(destino)!;
    var nomes = await gravador.SalvarAsync(consulta, resultado.GetValue(numero)!, arquivo, cancellationToken);
    Console.WriteLine($"{arquivo.FullName}: {gravador.Interacoes.Count} requisições, {nomes} nomes pseudonimizados.");
});

var raiz = new RootCommand("juscraper — consulta de processos e movimentações nos portais públicos dos tribunais.")
{
    consultar, tribunais, detectar, gravar,
};
return await raiz.Parse(args).InvokeAsync();

static IReadOnlyList<string> Instancias(string? valor) =>
    (valor ?? "1,2").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

static ConsultaProcessual Servico(TimeSpan? cacheTtl = null, HttpMessageHandler? rede = null)
{
    var services = new ServiceCollection();
    var http = services.AddJuscraper();
    if (rede is not null)
    {
        http.ConfigurePrimaryHttpMessageHandler(() => rede);
    }

    services.Configure<OpcoesJuscraper>(o =>
    {
        o.CacheTtl = cacheTtl ?? o.CacheTtl;
        o.PjeRest = Environment.GetEnvironmentVariable("Juscraper__PjeRest");
    });
    return services.BuildServiceProvider().GetRequiredService<ConsultaProcessual>();
}

namespace Juscraper.Cli
{
    internal static class Resumo
    {
        public static void Imprimir(ResultadoConsulta resultado)
        {
            if (!resultado.Encontrado)
            {
                Console.WriteLine($"{resultado.NumeroProcesso}: não encontrado no {resultado.Tribunal}.");
            }

            foreach (var p in resultado.Processos)
            {
                Console.WriteLine();
                Console.WriteLine($"{p.NumeroProcesso} · {p.Tribunal} {p.Grau} · {p.Sistema}");
                foreach (var (rotulo, valor) in new[]
                         {
                             ("Classe", p.Classe), ("Assunto", string.Join(", ", p.Assuntos)), ("Órgão", p.OrgaoJulgador),
                             ("Juiz/Relator", p.Juiz), ("Situação", p.Situacao),
                             ("Distribuição", p.DataAjuizamento?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)),
                         })
                {
                    if (!string.IsNullOrEmpty(valor))
                    {
                        Console.WriteLine($"  {rotulo,-13} {valor}");
                    }
                }

                foreach (var parte in p.Partes)
                {
                    var advogados = string.Join("; ", parte.Advogados.Select(a => a.Nome));
                    Console.WriteLine($"  {parte.Tipo,-13} {parte.Nome}{(advogados.Length > 0 ? $"  (adv.: {advogados})" : "")}");
                }

                Console.WriteLine($"  Movimentações: {p.Movimentos.Count}{(p.MovimentosCompletos ? "" : " (lista truncada)")}");
                foreach (var m in p.Movimentos.Take(10))
                {
                    Console.WriteLine($"    {m.DataHora?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "??/??/????"}  {m.Nome}");
                }
            }

            foreach (var aviso in resultado.Avisos)
            {
                Console.Error.WriteLine($"! {aviso}");
            }
        }
    }
}
