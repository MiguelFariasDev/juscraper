using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Juscraper.Http;
using Juscraper.Servicos;
using Microsoft.Extensions.DependencyInjection;

namespace Juscraper.Tests;

/// <summary>
/// Reproduz um cassete gravado dos portais reais (pseudonimizado). Casa pela URL normalizada
/// (esquema, host, caminho e pares de query ordenados). Requisição fora do cassete é falha do
/// teste: significa que o scraper mudou o que pede ao tribunal.
/// </summary>
public sealed class Reprodutor : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Interacao>> _respostas = new();

    public Reprodutor(params string[] cassetes)
    {
        foreach (var nome in cassetes)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "cassettes", $"{nome}.json")));
            foreach (var i in json.RootElement.GetProperty("interacoes").EnumerateArray())
            {
                var interacao = new Interacao(
                    i.GetProperty("status").GetInt32(),
                    i.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null,
                    i.GetProperty("content_type").GetString() ?? "text/html",
                    i.GetProperty("body").GetString() ?? string.Empty);
                _respostas.GetOrAdd(Chave(i.GetProperty("method").GetString()!, new Uri(i.GetProperty("url").GetString()!)), _ => new()).Enqueue(interacao);
            }
        }
    }

    public List<string> Pedidos { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Pedidos)
        {
            Pedidos.Add(request.RequestUri!.ToString());
        }

        if (!_respostas.TryGetValue(Chave(request.Method.Method, request.RequestUri!), out var fila) || !fila.TryPeek(out var interacao))
        {
            throw new InvalidOperationException($"Requisição fora do cassete: {request.RequestUri}");
        }

        if (fila.Count > 1)
        {
            fila.TryDequeue(out _);
        }

        var resposta = new HttpResponseMessage((HttpStatusCode)interacao.Status)
        {
            Content = new StringContent(interacao.Corpo, Encoding.UTF8),
            RequestMessage = request,
        };
        resposta.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
            interacao.ContentType.Split(';')[0].Trim() is { Length: > 0 } tipo ? $"{tipo}; charset=utf-8" : "text/html; charset=utf-8");
        if (interacao.Location is not null)
        {
            resposta.Headers.Location = new Uri(interacao.Location, UriKind.RelativeOrAbsolute);
        }

        return Task.FromResult(resposta);
    }

    private static string Chave(string metodo, Uri uri)
    {
        var pares = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => $"{Decodificar(p[0])}={(p.Length > 1 ? Decodificar(p[1]) : "")}")
            .Order(StringComparer.Ordinal);
        return $"{metodo} {uri.Scheme}://{uri.Host}{uri.AbsolutePath}?{string.Join('&', pares)}";
    }

    private static string Decodificar(string valor) => Uri.UnescapeDataString(valor.Replace('+', ' '));

    private sealed record Interacao(int Status, string? Location, string ContentType, string Corpo);
}

/// <summary>Monta o juscraper pelo MESMO caminho da API (AddJuscraper), trocando só a rede.</summary>
public static class Montagem
{
    public static OpcoesHttp HttpRapido() => new()
    {
        Tentativas = 3,
        BackoffBase = TimeSpan.FromMilliseconds(1),
        PoliticaPadrao = new PoliticaHost(TimeSpan.Zero, 4),
    };

    public static (ConsultaProcessual Servico, Reprodutor Rede) ComCassetes(params string[] cassetes)
    {
        var rede = new Reprodutor(cassetes);
        return (Servico(rede), rede);
    }

    public static ConsultaProcessual Servico(HttpMessageHandler rede, TimeSpan? cacheTtl = null)
    {
        var services = new ServiceCollection();
        services.AddJuscraper().ConfigurePrimaryHttpMessageHandler(() => rede);
        services.Configure<OpcoesJuscraper>(o =>
        {
            o.CacheTtl = cacheTtl ?? TimeSpan.Zero;
            o.Http = HttpRapido();
        });
        return services.BuildServiceProvider().GetRequiredService<ConsultaProcessual>();
    }

    public static Sessao Sessao(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var services = new ServiceCollection();
        services.AddJuscraper().ConfigurePrimaryHttpMessageHandler(() => new Stub(responder));
        services.Configure<OpcoesJuscraper>(o => o.Http = HttpRapido());
        return services.BuildServiceProvider().GetRequiredService<FabricaDeSessoes>().Criar("TJXX");
    }

    public sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Chamadas { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Chamadas++;
            return Task.FromResult(responder(request));
        }
    }
}
