using Juscraper.Scrapers;
using Juscraper.Scrapers.Esaj;
using Juscraper.Scrapers.Pje;

namespace Juscraper.Tribunais;

public enum StatusIntegracao
{
    /// <summary>Fluxo confirmado com processo real (nov/2026) e coberto por cassete.</summary>
    Validado,

    /// <summary>Mesmo sistema de um validado; o portal respondeu, mas sem processo real conferido.</summary>
    Experimental,

    /// <summary>Captcha, firewall anti-robô ou bloqueio geográfico — registrado para responder com o motivo.</summary>
    Indisponivel,
}

public sealed record Tribunal(
    string Sigla,
    string Sistema,
    StatusIntegracao Status,
    string Portal,
    string? Observacao = null,
    Func<IScraper>? Fabrica = null,
    Func<string, string?, JuscraperException>? Erro = null)
{
    public IScraper CriarScraper() =>
        Fabrica?.Invoke()
        ?? throw (Erro ?? ((m, t) => new TribunalNaoSuportadoException(m, t)))(
            $"{Sigla} ({Sistema}) não está disponível: {Observacao}", Sigla);
}

/// <summary>Qual sistema cada tribunal usa e em que estado está a integração.</summary>
public sealed class RegistroTribunais
{
    private readonly Dictionary<string, Tribunal> _tribunais;

    public RegistroTribunais(string? pjeRestDeclarados = null)
    {
        _tribunais = Fixos().ToDictionary(t => t.Sigla, StringComparer.OrdinalIgnoreCase);
        foreach (var tribunal in PjeRestDeclarados(pjeRestDeclarados))
        {
            _tribunais[tribunal.Sigla] = tribunal;
        }
    }

    public IReadOnlyCollection<Tribunal> Todos => _tribunais.Values;

    public Tribunal Obter(string? sigla)
    {
        if (sigla is null)
        {
            throw new TribunalNaoSuportadoException("O número CNJ não corresponde a nenhum tribunal existente.");
        }

        if (_tribunais.TryGetValue(sigla, out var tribunal))
        {
            return tribunal;
        }

        var suportados = string.Join(", ", _tribunais.Values.Where(t => t.Fabrica is not null).Select(t => t.Sigla));
        throw new TribunalNaoSuportadoException($"{sigla} ainda não tem scraper. Suportados: {suportados}.", sigla);
    }

    /// <summary>
    /// Tribunais PJe com a NOVA consulta pública declarados por configuração
    /// (<c>Juscraper:PjeRest</c>): <c>"TJXX=https://api|https://portal;TJYY=…"</c>. Quando um
    /// tribunal migrar, basta descobrir o apiUrl com <c>juscraper detectar-pje</c> — sem nova versão.
    /// </summary>
    public static IReadOnlyList<Tribunal> PjeRestDeclarados(string? valor)
    {
        var tribunais = new List<Tribunal>();
        foreach (var item in (valor ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var (sigla, urls) = item.Split('=', 2) is [var s, var u] ? (s.Trim().ToUpperInvariant(), u) : (string.Empty, string.Empty);
            var (api, portal) = urls.Split('|', 2) is [var a, var p] ? (a.Trim(), p.Trim()) : (urls.Trim(), null);
            if (sigla.Length == 0 || !api.StartsWith("https://", StringComparison.Ordinal))
            {
                throw new FormatException($"Juscraper:PjeRest inválido: '{item}'.");
            }

            var portalFinal = string.IsNullOrEmpty(portal) ? null : portal;
            tribunais.Add(new Tribunal(sigla, PjeConsultaPublicaScraper.NomeSistema, StatusIntegracao.Experimental,
                portalFinal ?? api, "Nova consulta pública do PJe, declarada por configuração.",
                () => new PjeConsultaPublicaScraper(sigla, api, urlPortal: portalFinal)));
        }

        return tribunais;
    }

    private static IEnumerable<Tribunal> Fixos()
    {
        static Tribunal Esaj(string sigla, string url, StatusIntegracao status, string primeiro = "cpopg", string? segundo = "cposg",
            string? observacao = null) =>
            new(sigla, EsajParser.Sistema, status, url, observacao, () => new EsajScraper(sigla, url, primeiro, segundo));

        static Tribunal Captcha(string sigla, string portal) =>
            new(sigla, PjeConsultaPublicaScraper.NomeSistema, StatusIntegracao.Indisponivel, portal,
                "a consulta pública do PJe exige reCAPTCHA.", Erro: (m, t) => new CaptchaExigidoException(m, t));

        static Tribunal Bloqueado(string sigla, string sistema, string portal, string motivo) =>
            new(sigla, sistema, StatusIntegracao.Indisponivel, portal, motivo, Erro: (m, t) => new AcessoBloqueadoException(m, t));

        const string ForaDoBrasil = "o portal bloqueia acessos de fora do Brasil; não foi possível verificar qual consulta ele usa.";

        return
        [
            Esaj("TJSP", "https://esaj.tjsp.jus.br", StatusIntegracao.Validado,
                observacao: "1º e 2º grau; movimentações sob demanda (AJAX paginado)."),
            Esaj("TJAL", "https://www2.tjal.jus.br", StatusIntegracao.Validado, "cpopg", "cposg5",
                "1º grau validado (movimentações inline); 2º grau em /cposg5 (o antigo /cposg redireciona para o portal novo)."),
            Esaj("TJCE", "https://esaj.tjce.jus.br", StatusIntegracao.Experimental, "cpopg", "cposg5",
                "/cposg respondeu 404 nos testes; 2º grau configurado em /cposg5."),
            Esaj("TJAC", "https://esaj.tjac.jus.br", StatusIntegracao.Experimental,
                observacao: "Parte do acervo do TJAC está no Projudi, que não é coberto."),
            Esaj("TJMS", "https://esaj.tjms.jus.br", StatusIntegracao.Experimental, "cpopg5", "cposg5",
                "Portal não respondeu nos testes (timeout)."),
            Esaj("TJAM", "https://consultasaj.tjam.jus.br", StatusIntegracao.Experimental, "cpopg", "cposgcr",
                "Portal recusou conexões vindas de fora da rede brasileira nos testes."),
            new("TJDFT", PjeConsultaPublicaScraper.NomeSistema, StatusIntegracao.Validado, "https://pje-consultapublica.tjdft.jus.br",
                "Nova consulta pública do PJe (API REST).",
                () => new PjeConsultaPublicaScraper("TJDFT", "https://pje-consultapublica-api.tjdft.jus.br",
                    urlPortal: "https://pje-consultapublica.tjdft.jus.br")),

            Captcha("TJMG", "https://pje-consulta-publica.tjmg.jus.br"),
            Captcha("TJBA", "https://pje.tjba.jus.br"),
            Captcha("TJPE", "https://pje.cloud.tjpe.jus.br"),
            Captcha("TJMA", "https://pje.tjma.jus.br"),
            Captcha("TJRO", "https://pjepg-consulta.tjro.jus.br"),
            Bloqueado("TJPB", "PJe", "https://consultapublica.tjpb.jus.br", "o firewall (Cloudflare) bloqueia acesso automatizado."),
            Bloqueado("TJAP", "PJe", "https://pje.tjap.jus.br", "o portal exige o desafio JavaScript da Cloudflare."),
            Bloqueado("TJES", "PJe", "https://pje.tjes.jus.br", ForaDoBrasil),
            Bloqueado("TJRN", "PJe", "https://pje1gconsulta.tjrn.jus.br", ForaDoBrasil),
            Bloqueado("TJSC", "eproc", "https://eproc1g.tjsc.jus.br", "o firewall do tribunal bloqueia acesso automatizado."),
            Bloqueado("TJRS", "eproc", "https://eproc1g.tjrs.jus.br", "o portal responde 403 a acesso automatizado."),
        ];
    }
}
