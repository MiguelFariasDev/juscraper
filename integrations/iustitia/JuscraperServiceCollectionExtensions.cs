using Advocacia.Modules.Legal.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Advocacia.Modules.Legal.Infrastructure.Integrations.Juscraper;

/// <summary>Seção "Juscraper" do appsettings. A ApiKey vai em user-secrets/Key Vault.</summary>
public sealed class JuscraperOptions
{
    public const string SectionName = "Juscraper";

    /// <summary>Endereço do serviço juscraper, ex.: http://juscraper:8000/.</summary>
    public string BaseUrl { get; init; } = "http://localhost:8000/";

    /// <summary>Valor de X-Api-Key (uma das chaves em JUSCRAPER_API_KEYS do serviço).</summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Teto por tentativa. O juscraper pagina o portal com intervalo mínimo entre requisições
    /// (é o que mantém o acesso), então uma consulta fria leva de 2 a 20 s.
    /// </summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public static class JuscraperServiceCollectionExtensions
{
    /// <summary>
    /// Registra o juscraper como implementação de <see cref="IConsultaProcessoTribunal"/>.
    /// Em Legal.Infrastructure/DependencyInjection.cs, substitui:
    /// <code>services.AddScoped&lt;IConsultaProcessoTribunal, DataJudConsultaProcessoTribunal&gt;();</code>
    /// por:
    /// <code>services.AddJuscraperConsultaProcessoTribunal(configuration);</code>
    /// </summary>
    public static IServiceCollection AddJuscraperConsultaProcessoTribunal(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(JuscraperOptions.SectionName);
        services.Configure<JuscraperOptions>(section);
        var options = section.Get<JuscraperOptions>() ?? new JuscraperOptions();

        services
            .AddHttpClient<IConsultaProcessoTribunal, JuscraperConsultaProcessoTribunal>(
                JuscraperConsultaProcessoTribunal.HttpClientName, client =>
                {
                    client.BaseAddress = new Uri(options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/");
                    // Timeout fica a cargo do pipeline de resiliência.
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    client.DefaultRequestHeaders.Accept.Add(new("application/json"));
                    if (!string.IsNullOrWhiteSpace(options.ApiKey))
                    {
                        client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
                    }
                })
            .AddStandardResilienceHandler(resiliencia =>
            {
                resiliencia.AttemptTimeout.Timeout = options.AttemptTimeout;
                resiliencia.TotalRequestTimeout.Timeout = options.AttemptTimeout * 2 + TimeSpan.FromSeconds(10);
                // O juscraper já faz retry com backoff contra o portal. Aqui, só uma nova
                // tentativa, para falha de rede entre o Iustitia e o serviço — repetir mais
                // multiplicaria as requisições ao tribunal.
                resiliencia.Retry.MaxRetryAttempts = 1;
                // O circuit breaker exige janela de amostragem >= 2x o timeout de tentativa.
                resiliencia.CircuitBreaker.SamplingDuration = options.AttemptTimeout * 2 + TimeSpan.FromSeconds(1);
            });

        return services;
    }
}
