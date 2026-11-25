using System.Net;
using System.Text;
using Juscraper.Http;
using Juscraper.Servicos;
using Juscraper.Tribunais;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juscraper;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o juscraper: registro de tribunais, HttpClient nomeado (sem cookies nem
    /// redirecionamento automático — a <see cref="Sessao"/> cuida dos dois), limitadores por host,
    /// HybridCache e <see cref="ConsultaProcessual"/>. Devolve o builder do HttpClient para quem
    /// quiser trocar o handler primário (testes reproduzem cassetes assim).
    /// </summary>
    public static IHttpClientBuilder AddJuscraper(this IServiceCollection services, IConfiguration? configuracao = null)
    {
        // Portais antigos respondem em windows-1252/ISO-8859-1.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var opcoes = services.AddOptions<OpcoesJuscraper>();
        if (configuracao is not null)
        {
            opcoes.Bind(configuracao);
        }

        services.AddSingleton<IOptions<OpcoesHttp>>(sp => Options.Create(sp.GetRequiredService<IOptions<OpcoesJuscraper>>().Value.Http));
        services.AddSingleton(sp => new RegistroTribunais(sp.GetRequiredService<IOptions<OpcoesJuscraper>>().Value.PjeRest));
        services.AddSingleton<LimitadoresPorHost>();
        services.AddSingleton<FabricaDeSessoes>();
        services.AddSingleton<ConsultaProcessual>();
        services.AddSingleton(TimeProvider.System);
        services.AddHybridCache();

        return services
            .AddHttpClient(FabricaDeSessoes.NomeClienteHttp, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
    }
}
