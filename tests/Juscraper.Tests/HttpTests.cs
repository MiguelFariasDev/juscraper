using System.Net;
using System.Text;

namespace Juscraper.Tests;

public sealed class HttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Html(string corpo, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(corpo, Encoding.UTF8, "text/html") };

    [Fact]
    public async Task Retry_em_falha_transitoria()
    {
        var chamadas = 0;
        using var sessao = Montagem.Sessao(_ => ++chamadas < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Html("ok"));

        var resposta = await sessao.GetAsync("https://portal.jus.br/x", cancellationToken: Ct);

        Assert.Equal("ok", resposta.Corpo);
        Assert.Equal(3, chamadas);
    }

    [Fact]
    public async Task Desiste_apos_as_tentativas_e_marca_transitorio()
    {
        using var sessao = Montagem.Sessao(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var erro = await Assert.ThrowsAsync<TribunalIndisponivelException>(() => sessao.GetAsync("https://portal.jus.br/x", cancellationToken: Ct));

        Assert.True(erro.Transitorio);
    }

    [Fact]
    public async Task Http_403_nao_e_repetido()
    {
        var chamadas = 0;
        using var sessao = Montagem.Sessao(_ =>
        {
            chamadas++;
            return Html("Forbidden", HttpStatusCode.Forbidden);
        });

        await Assert.ThrowsAsync<AcessoBloqueadoException>(() => sessao.GetAsync("https://portal.jus.br/x", cancellationToken: Ct));
        Assert.Equal(1, chamadas); // insistir contra bloqueio só o prolonga
    }

    [Fact]
    public async Task Pagina_de_bloqueio_do_waf_com_status_200()
    {
        var html = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "bloqueio_waf_tjsc.html"), Ct);
        using var sessao = Montagem.Sessao(_ => Html(html));

        await Assert.ThrowsAsync<AcessoBloqueadoException>(() => sessao.GetAsync("https://eproc1g.tjsc.jus.br/eproc/", cancellationToken: Ct));
    }

    [Theory]
    [InlineData("<h1>403 ERROR</h1>The Amazon CloudFront distribution is configured to block access from your country.")]
    [InlineData("Acesso Bloqueado! ... bloqueado para usuários localizados no exterior.")]
    [InlineData("<html><head><title>Just a moment...</title></head></html>")]
    public async Task Bloqueio_geografico_e_desafio_da_cloudflare(string corpo)
    {
        using var sessao = Montagem.Sessao(_ => Html(corpo));

        await Assert.ThrowsAsync<AcessoBloqueadoException>(() => sessao.GetAsync("https://portal.jus.br/", cancellationToken: Ct));
    }

    [Fact]
    public async Task Captcha_e_detectado_e_nao_resolvido()
    {
        using var sessao = Montagem.Sessao(_ => Html("""<form><div class="g-recaptcha" data-sitekey="x"></div></form>"""));

        await Assert.ThrowsAsync<CaptchaExigidoException>(() => sessao.GetAsync("https://portal.jus.br/x", cancellationToken: Ct));
    }

    [Fact]
    public async Task Mencao_a_captcha_em_javascript_nao_e_captcha()
    {
        // O e-SAJ tem "uuidCaptcha" no JS de toda página de processo, sem captcha nenhum.
        using var sessao = Montagem.Sessao(_ => Html("<script>var captcha = $.saj.getUrlParameter('uuidCaptcha');</script>"));

        var resposta = await sessao.GetAsync("https://portal.jus.br/x", cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, resposta.Status);
    }

    private static HttpResponseMessage Redirecionar(string destino, string cookie)
    {
        var resposta = new HttpResponseMessage(HttpStatusCode.Found);
        resposta.Headers.Location = new Uri(destino, UriKind.Relative);
        resposta.Headers.Add("Set-Cookie", cookie);
        return resposta;
    }

    [Fact]
    public async Task Segue_redirecionamento_levando_o_cookie_da_sessao()
    {
        using var sessao = Montagem.Sessao(requisicao => requisicao.RequestUri!.AbsolutePath switch
        {
            "/search.do" => Redirecionar("/show.do", "JSESSIONID=abc; Path=/"),
            "/show.do" when requisicao.Headers.TryGetValues("Cookie", out var c) && c.Single() == "JSESSIONID=abc" => Html("detalhe"),
            _ => Html("sem cookie", HttpStatusCode.BadRequest),
        });

        var resposta = await sessao.GetAsync("https://esaj.exemplo.jus.br/search.do", cancellationToken: Ct);

        Assert.Equal("detalhe", resposta.Corpo);
        Assert.Equal("/show.do", resposta.Url.AbsolutePath);
    }
}
