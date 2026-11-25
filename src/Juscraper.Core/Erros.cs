namespace Juscraper;

/// <summary>
/// Erros tipados, cada um com um <see cref="Codigo"/> estável que a API devolve no
/// ProblemDetails e que o Iustitia mapeia para o próprio catálogo. A distinção importa:
/// <list type="bullet">
/// <item>erro do chamador (número inválido, tribunal não suportado) — repetir não adianta;</item>
/// <item>erro transitório do tribunal (fora do ar, lento) — vale tentar mais tarde;</item>
/// <item>bloqueio (captcha, firewall) — repetir piora; o caminho é outra fonte ou o cadastro manual.</item>
/// </list>
/// "Processo não encontrado" NÃO é erro: é <c>Encontrado = false</c> (ADR-064 do Iustitia).
/// </summary>
public abstract class JuscraperException(string mensagem, string? tribunal = null) : Exception(mensagem)
{
    public abstract string Codigo { get; }

    public abstract int StatusHttp { get; }

    /// <summary>Se faz sentido tentar de novo mais tarde.</summary>
    public virtual bool Transitorio => false;

    public string? Tribunal { get; } = tribunal;
}

public sealed class NumeroCnjInvalidoException(string? numero)
    : JuscraperException($"Número CNJ inválido: '{numero}'.")
{
    public override string Codigo => "NUMERO_CNJ_INVALIDO";

    public override int StatusHttp => 400;
}

public sealed class TribunalNaoSuportadoException(string mensagem, string? tribunal = null)
    : JuscraperException(mensagem, tribunal)
{
    public override string Codigo => "TRIBUNAL_NAO_SUPORTADO";

    public override int StatusHttp => 422;
}

/// <summary>Rede, timeout ou 5xx persistente depois das tentativas.</summary>
public sealed class TribunalIndisponivelException(string mensagem, string? tribunal = null)
    : JuscraperException(mensagem, tribunal)
{
    public override string Codigo => "TRIBUNAL_INDISPONIVEL";

    public override int StatusHttp => 502;

    public override bool Transitorio => true;
}

/// <summary>O portal exige captcha. O juscraper não tenta resolvê-lo, por decisão de projeto.</summary>
public sealed class CaptchaExigidoException(string mensagem, string? tribunal = null)
    : JuscraperException(mensagem, tribunal)
{
    public override string Codigo => "CAPTCHA_EXIGIDO";

    public override int StatusHttp => 503;
}

/// <summary>Firewall/WAF ou bloqueio geográfico recusou o acesso.</summary>
public sealed class AcessoBloqueadoException(string mensagem, string? tribunal = null)
    : JuscraperException(mensagem, tribunal)
{
    public override string Codigo => "ACESSO_BLOQUEADO";

    public override int StatusHttp => 503;
}

/// <summary>HTML/JSON fora da estrutura esperada — sinal de que o portal mudou de layout.</summary>
public sealed class RespostaInesperadaException(string mensagem, string? tribunal = null)
    : JuscraperException(mensagem, tribunal)
{
    public override string Codigo => "RESPOSTA_INESPERADA";

    public override int StatusHttp => 502;
}
